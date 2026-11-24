using AVAClaimScrubber.Core.Checks;

namespace AVAClaimScrubber.Tests;

/// <summary>Every planted problem in the samples is found, and nothing else: false hits fail these as surely as misses.</summary>
public sealed class FindingsTests
{
    [Fact]
    public void The_professional_batch_finds_each_planted_problem_and_nothing_else()
    {
        var r = Samples.Scrub("clinic-837p.x12");
        Assert.Equal(
        [
            "A1 TOTAL-02",
            "A2 UNITS-05 line 1",
            "A3 FUTURE-03 line 1",
            "A5 REPLACE-06",
            "S2 BADDATE-07 line 1",
            "S4 DOT-04",
        ], r.Pinned());
        Assert.Null(r.Status(r.Batch.Claim("CLEAN-01")));
        Assert.Equal(Severity.Reject, r.Status(r.Batch.Claim("TOTAL-02")));
    }

    [Fact]
    public void The_institutional_batch_finds_the_line_outside_its_statement_period()
    {
        var r = Samples.Scrub("hospital-837i.x12");
        Assert.Equal(["A4 INPT-OUTSIDE-02 line 2"], r.Pinned());
        var f = r.Findings.Single();
        Assert.Equal("Line 2 is dated 2 days after the statement period ends.", f.Message);
        Assert.Equal(Severity.Deny, f.Severity);
    }

    [Fact]
    public void The_envelope_errors_are_found_with_fixes()
    {
        var r = Samples.Scrub("envelope-errors.x12");
        Assert.Equal(["E1 envelope", "E4 envelope", "E6 envelope"], r.Pinned());
        var se = r.Findings.Single(f => f.Rule.Id == "E6");
        Assert.Equal("SE01 says 27 segments; the transaction has 26.", se.Message);
        Assert.Equal(("26", 1), (se.Fix!.NewValue, se.Fix.Element));
        Assert.Equal("Set IEA02 to ISA13", r.Findings.Single(f => f.Rule.Id == "E1").Fix!.Description);
    }

    [Fact]
    public void The_structure_problems_are_found_on_the_claims_they_break()
    {
        var r = Samples.Scrub("structure-errors.x12");
        Assert.Equal(
        [
            "L10 LXGAP-02 line 3",
            "L11 POINTER-01 line 1",
            "L2 PARENT-04",
            "L6 NOPAYER-05",
            "L8 NODX-03",
        ], r.Pinned());
        Assert.Equal(["POINTER-01", "LXGAP-02", "NODX-03", "PARENT-04", "NOPAYER-05"], r.Batch.Claims.Select(c => c.Account));
        Assert.Equal("Number it 2", r.Findings.Single(f => f.Rule.Id == "L10").Fix!.Description);
        Assert.Equal("Line 1 points at diagnosis 3, and the claim has 1.", r.Findings.Single(f => f.Rule.Id == "L11").Message);
    }

    [Fact]
    public void Rule_packs_turn_rules_off_or_change_their_severity_for_their_payers()
    {
        var pack = Core.Checks.RulePack.Read("""
            { "format": "avaclaimscrubber.rules", "name": "Sun Plan", "payers": ["SUN01"],
              "overrides": { "A4": { "severity": "warn", "why": "Sun Plan pays lines outside the period" },
                             "S4": { "enabled": false } } }
            """);
        var settings = new ScrubSettings { Packs = [pack], CodeSets = Samples.CodeSets };
        var p = Samples.Scrub("clinic-837p.x12", settings);
        Assert.DoesNotContain(p.Findings, f => f.Rule.Id == "S4");
        var i = Samples.Scrub("hospital-837i.x12", settings);
        Assert.Equal(Severity.Warn, i.Findings.Single().Severity);
        // A pack for another payer changes nothing.
        var other = new ScrubSettings { CodeSets = Samples.CodeSets, Packs = [Core.Checks.RulePack.Read("""{ "format": "avaclaimscrubber.rules", "name": "Other", "payers": ["XYZ"], "overrides": { "S4": { "enabled": false } } }""")] };
        Assert.Contains(Samples.Scrub("clinic-837p.x12", other).Findings, f => f.Rule.Id == "S4");
        Assert.Contains("doesn't exist", Assert.Throws<FormatException>(() => Core.Checks.RulePack.Read("""{ "format": "avaclaimscrubber.rules", "name": "x", "overrides": { "Z9": { "enabled": false } } }""")).Message);
        Assert.Contains("info, warn, deny or reject", Assert.Throws<FormatException>(() => Core.Checks.RulePack.Read("""{ "format": "avaclaimscrubber.rules", "name": "x", "overrides": { "A1": { "severity": "loud" } } }""")).Message);
    }

    [Fact]
    public void Findings_say_where_and_how_to_fix_without_quoting_dates_or_names()
    {
        var r = Samples.Scrub("clinic-837p.x12");
        var total = r.Findings.Single(f => f.Rule.Id == "A1");
        Assert.Equal("CLM02 is 250.00 but the lines add up to 225.00.", total.Message);
        Assert.Equal("claim 2 2300 CLM02", total.Place);
        Assert.Equal("225.00", total.Fix!.NewValue);
        var future = r.Findings.Single(f => f.Rule.Id == "A3");
        Assert.Equal("Line 1's service date is 27 days after the batch was made.", future.Message);
        Assert.Equal("claim 3 line 1 2400 DTP03", future.Place);
        var dot = r.Findings.Single(f => f.Rule.Id == "S4");
        Assert.Equal("Diagnosis 1 (F32.9) has a decimal point; X12 sends it as F329.", dot.Message);
        // No finding quotes a date, a member ID or an account number.
        foreach (var f in r.Findings)
        {
            Assert.DoesNotMatch(@"20\d{6}", f.Message);
            Assert.DoesNotContain("SUN1", f.Message);
            if (f.Claim > 0) Assert.DoesNotContain(r.Batch.Claims.First(c => c.Number == f.Claim).Account, f.Message);
        }
    }

    [Fact]
    public void Every_rule_has_its_why_and_its_source()
    {
        var rules = Scrubber.AllRules().ToList();
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct().Count());
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Why));
            Assert.False(string.IsNullOrWhiteSpace(r.Source));
        });
    }
}
