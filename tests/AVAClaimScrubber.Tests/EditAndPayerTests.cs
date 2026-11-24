using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Tests;

/// <summary>Layer 5 (NCCI, MUE, age and sex), layer 6 (payer rules from a pack), and the corrected copy.</summary>
public sealed class EditAndPayerTests
{
    private static ScrubSettings WithSunPlan => new() { CodeSets = Samples.CodeSets, Packs = [RulePack.Load(Samples.Path("rules/sunplan.json"))] };

    [Fact]
    public void NCCI_MUE_age_and_sex_problems_are_found_and_a_modifier_lets_a_pair_through()
    {
        var r = Samples.Scrub("edits-837p.x12");
        Assert.Equal(["N1 NCCI-01 line 2", "N1 NCCI-03 line 2", "N2 MUE-04 line 1", "N3 SEX-05", "N4 AGE-06"], r.Pinned());
        Assert.Null(r.Status(r.Batch.Claim("NCCI-OK-02")));
        Assert.Contains("modifier such as 59 or XU", r.Findings.Single(f => f.Rule.Id == "N1" && f.Claim == 3).Message);
        Assert.Equal("G0439 bills 2 units on line 1's day; its MUE is 1 a day (Code Descriptor / CPT Instruction).", r.Findings.Single(f => f.Rule.Id == "N2").Message);
    }

    [Fact]
    public void Payer_rules_from_a_pack_find_late_unauthorized_and_unmodified_telehealth_claims()
    {
        var r = Samples.Scrub("payer-837p.x12", WithSunPlan);
        Assert.Equal(["P-sunplan-auth AUTH-02 line 1", "P-sunplan-telehealth TELE-04 line 1", "P-sunplan-timely LATE-01"], r.Pinned());
        Assert.Equal("The first service was 137 days before the batch was made; Sun Plan takes claims within 90 days.", r.Findings.Single(f => f.Rule.Id == "P-sunplan-timely").Message);
        var rule = r.Findings.Single(f => f.Rule.Id == "P-sunplan-auth").Rule;
        Assert.Equal(6, rule.Layer);
        Assert.Equal("Rule pack \"Sun Plan\" version 2026.11", rule.Source);
        Assert.StartsWith("Sun Plan needs prior authorization", rule.Why);
        Assert.Null(r.Status(r.Batch.Claim("AUTH-OK-03")));
        Assert.Null(r.Status(r.Batch.Claim("TELE-OK-05")));
        // Without the pack, the same claims are clean.
        Assert.Empty(Samples.Scrub("payer-837p.x12").Findings);
        // A pack's rules are checked when it's read.
        Assert.Contains("kind is \"late\"", Assert.Throws<FormatException>(() => RulePack.Read("""{ "format": "avaclaimscrubber.rules", "name": "x", "rules": [ { "kind": "late" } ] }""")).Message);
        Assert.Contains("needs \"days\"", Assert.Throws<FormatException>(() => RulePack.Read("""{ "format": "avaclaimscrubber.rules", "name": "x", "rules": [ { "kind": "timely-filing" } ] }""")).Message);
        Assert.Contains(Scrubber.AllRules(WithSunPlan), x => x.Id == "P-sunplan-telehealth");
    }

    [Fact]
    public void A_corrected_copy_makes_the_fixes_and_nothing_else_and_checks_clean()
    {
        foreach (var name in new[] { "envelope-errors.x12", "clinic-837p.x12", "structure-errors.x12" })
        {
            var original = File.ReadAllText(Samples.Path(name));
            var r = Samples.Scrub(name);
            var (text, changes) = Corrector.Apply(original, r.Batch.File, r.Findings);
            Assert.Equal(r.Findings.Count(f => f.Fix != null), changes.Count);
            var after = Core.Checks.Scrubber.Scrub(Core.Claims.ClaimReader.Read(name, X12File.Parse(text)), new ScrubSettings { CodeSets = Samples.CodeSets });
            Assert.DoesNotContain(after.Findings, f => f.Fix != null);
            // Only the fixed segments changed; the layout is the original's.
            var a = original.Split('\n');
            var b = text.Split('\n');
            Assert.Equal(a.Length, b.Length);
            Assert.Equal(changes.Select(c => c.SegmentIndex).Distinct().Count(), a.Zip(b).Count(p => p.First != p.Second));
        }
        var env = Samples.Scrub("envelope-errors.x12");
        var (_, list) = Corrector.Apply(File.ReadAllText(Samples.Path("envelope-errors.x12")), env.Batch.File, env.Findings);
        Assert.Contains(list, c => c.Element == "SE01" && c.Was == "27" && c.Now == "26" && c.Why == "E6: Set SE01 to 26");
        Assert.Contains(list, c => c.Element == "IEA02" && c.Was == "000000008" && c.Now == "000000007");
    }
}
