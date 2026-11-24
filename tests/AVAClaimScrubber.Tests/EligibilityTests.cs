using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Eligibility;
using AVAClaimScrubber.Core.Responses;

namespace AVAClaimScrubber.Tests;

/// <summary>Layer 7: the claims checked against the payer's 271 answers, and eligibility denials replayed against them.</summary>
public sealed class EligibilityTests
{
    private static ResponseFile Answers() => ResponseReader.Load(Samples.Path("responses/sunplan-271.x12"))!;

    private static ScrubSettings With271() => new() { CodeSets = Samples.CodeSets, Eligibility = Answers().Eligibility };

    [Fact]
    public void The_271_is_read_as_each_persons_answer()
    {
        var r = Answers();
        Assert.Equal(("271", "271 eligibility answer", "SUNPLAN"), (r.Kind, r.KindName, r.Payer));
        Assert.Equal("5 people: 3 active, 1 not active, 1 not found", r.Summary);
        var a = r.Eligibility.ToDictionary(x => x.MemberId);
        Assert.Equal(("75", (bool?)null), (a["SUN500000"].Rejection, a["SUN500000"].Active));
        Assert.Equal(["MH"], a["SUN700001"].NeedsAuthorization);
        Assert.Equal((false, new DateOnly(2026, 10, 31)), (a["SUN600004"].Active, a["SUN600004"].Ends));
        Assert.Equal("MEDICARE PART B", a["SUN700004"].PrimaryElsewhere);
        Assert.All(r.Eligibility, x => Assert.Equal(("SUN01", new DateOnly(2026, 11, 1)), (x.PayerId, x.Made)));
    }

    [Fact]
    public void With_the_271_open_the_claims_get_the_eligibility_findings_planted_for_them()
    {
        string[] V(string file) => [.. Samples.Scrub(file, With271()) is var r ? r.Findings.Where(f => f.Rule.Id.StartsWith('V')).Select(f => $"{f.Rule.Id} {r.Batch.Claims[f.Claim - 1].Account} {f.Place}: {f.Message}") : []];
        Assert.Equal(
        [
            "V4 AUTH-02 claim 2 line 1 2400: The payer's 271 (sunplan-271.x12) says Mental Health needs authorization, and the claim has no authorization number (REF*G1).",
            "V3 TELE-OK-05 claim 5 2000B SBR01: The payer's 271 (sunplan-271.x12) says MEDICARE PART B is primary, and this claim is billed here as primary (SBR01 P). Bill MEDICARE PART B first, then this payer as secondary.",
        ], V("payer-837p.x12"));
        Assert.Equal(["V1 SEX-05 claim 5 2010BA: The payer's 271 (sunplan-271.x12) says coverage isn't active: it ended 15 days before the batch was made. Bill the patient's current plan, or the patient."], V("edits-837p.x12"));
        Assert.Equal(["V2 CODES-CLEAN-01 claim 1 2010BA NM109: The payer's 271 (sunplan-271.x12) couldn't find this member: AAA 75, Subscriber/Insured Not Found. Check the member ID, name and birth date against the card, or ask for the current one."], V("coding-errors.x12"));
        Assert.Empty(V("clinic-837p.x12"));
        // AUTH-OK-03 has its authorization number (REF*G1): no V4.
        Assert.Equal([2, 3, 5], Samples.Scrub("payer-837p.x12", With271()).Answered.Order());
        // Without a 271 the layer checks nothing, and the findings the other tests pin are unchanged.
        Assert.DoesNotContain(Samples.Scrub("payer-837p.x12").Findings, f => f.Rule.Id.StartsWith('V'));
        Assert.Equal(Severity.Reject, EligibilityChecks.V2.Severity);
        Assert.All(new[] { EligibilityChecks.V1, EligibilityChecks.V2, EligibilityChecks.V3, EligibilityChecks.V4 }, r => Assert.Equal(7, r.Layer));
    }

    [Fact]
    public void Eligibility_denials_are_caught_with_the_271_and_not_checkable_without_it()
    {
        var remit = ResponseReader.Load(Samples.Path("responses/sunplan-835.x12"))!;
        string[] files = ["payer-837p.x12", "edits-837p.x12", "coding-errors.x12"];
        var without = Replay.Run(remit, [.. files.Select(f => Samples.Scrub(f))], Samples.CodeSets);
        Assert.Equal("8 denials: 3 caught by a check, 3 missed, 2 not checkable from a claim.", without.Summary);
        var notFound = without.Rows.First(r => r.Denial.Code == "31");
        Assert.Equal((Verdict.NotCheckable, "Not checkable from a claim: the patient isn't found as insured: eligibility; open the payer's 271 about this patient to check it."), (notFound.Verdict, notFound.Explanation));
        Assert.Equal("Missed: a payer rule (prior-auth) would catch it; with the payer's 271 about this patient open, V4 would check it.", without.Rows.Single(r => r.Denial.Code == "197").Explanation);

        var with = Replay.Run(remit, [.. files.Select(f => Samples.Scrub(f, With271()))], Samples.CodeSets);
        Assert.Equal("8 denials: 6 caught by a check, 2 missed, 0 not checkable from a claim.", with.Summary);
        Assert.All(with.Rows.Where(r => r.Denial.Code is "31" or "197"), r => Assert.Equal(Verdict.Caught, r.Verdict));
        Assert.StartsWith("Caught by V2:", with.Rows.First(r => r.Denial.Code == "31").Explanation);

        // A 271 that found the member and said nothing wrong: the denial is a real miss.
        var text = File.ReadAllText(Samples.Path("responses/sunplan-271.x12")).Replace("AAA*N**75*C~", "DMG*D8*19800415*F~\nEB*1*IND*30*PR*SUN PLAN SILVER~");
        var found = ResponseReader.Read("sunplan-271.x12", Core.X12.X12File.Parse(text))!;
        var missed = Replay.Run(remit, [Samples.Scrub("coding-errors.x12", new ScrubSettings { CodeSets = Samples.CodeSets, Eligibility = found.Eligibility })], Samples.CodeSets)
            .Rows.First(r => r.Denial.Code == "31");
        Assert.Equal((Verdict.Missed, "Missed: the payer's 271 about this patient didn't show it (V2 found nothing); the answer may be older than the service."), (missed.Verdict, missed.Explanation));
    }

    [Fact]
    public void Coverage_dates_are_checked_line_by_line_and_a_dependent_is_matched_under_their_subscriber()
    {
        var x = Core.X12.X12File.Parse(File.ReadAllText(Samples.Path("responses/sunplan-271.x12")).Replace("EB*6*IND*30*PR*SUN PLAN SILVER~", "EB*1*IND*30*PR*SUN PLAN SILVER~"));
        var answers = EligibilityAnswers.Read(x, "x.x12");
        var hale = Samples.Scrub("edits-837p.x12", new ScrubSettings { Eligibility = answers }).Findings.Single(f => f.Rule.Id == "V1");
        Assert.Equal("The payer's 271 (x.x12) says coverage ends 3 days before this service.", hale.Message);
        Assert.Equal(1, hale.Line);
        Assert.DoesNotMatch(@"20\d{6}", hale.Message);
    }
}
