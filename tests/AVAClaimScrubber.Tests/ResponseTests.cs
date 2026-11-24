using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Responses;

namespace AVAClaimScrubber.Tests;

/// <summary>999, 277CA and 835 files: read, matched to the claims, and replayed against the findings.</summary>
public sealed class ResponseTests
{
    private static ResponseFile Response(string name) => ResponseReader.Load(Samples.Path("responses/" + name))!;

    private static readonly string[] All = ["clinic-837p.x12", "hospital-837i.x12", "envelope-errors.x12", "structure-errors.x12", "coding-errors.x12", "edits-837p.x12", "payer-837p.x12"];

    private static List<ScrubResult> Scrubbed(ScrubSettings? settings = null) => [.. All.Select(n => Samples.Scrub(n, settings))];

    internal static string Line(ReplayRow r) => $"{r.Verdict} {(r.Denial.Account.Length > 0 ? r.Denial.Account : "batch")}{(r.Denial.Line > 0 ? " line " + r.Denial.Line : "")} {r.Denial.Code}" +
                                                (r.CaughtBy.Count > 0 ? " by " + string.Join(",", r.CaughtBy.Select(f => f.Rule.Id).Distinct()) : "");

    /// <summary>Writes every replay as text (CLAIMS_REPLAY=path), for writing the pinned lists.</summary>
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_REPLAY") is not { } path) return;
        var lines = new List<string>();
        foreach (var n in new[] { "structure-999.x12", "envelope-999.x12", "clinic-277ca.x12", "sunplan-835.x12" })
        {
            var report = Replay.Run(Response(n), Scrubbed());
            lines.Add($"== {n}: {report.Response.Summary}. {report.Summary}");
            lines.AddRange(report.Rows.Select(r => Line(r) + "  | " + r.Denial.Description + "  | " + r.Explanation));
            if (Replay.Draft(report) is { } d) lines.Add(d.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        File.WriteAllLines(path, lines);
    }
}

public sealed class ReplayTests
{
    private static ResponseFile Response(string name) => ResponseReader.Load(Samples.Path("responses/" + name))!;

    private static readonly string[] All = ["clinic-837p.x12", "hospital-837i.x12", "envelope-errors.x12", "structure-errors.x12", "coding-errors.x12", "edits-837p.x12", "payer-837p.x12"];

    private static List<ScrubResult> Scrubbed(ScrubSettings? settings = null) => [.. All.Select(n => Samples.Scrub(n, settings))];

    [Fact]
    public void The_three_kinds_of_response_read_as_their_guides_say()
    {
        var ack = Response("structure-999.x12");
        Assert.Equal("999", ack.Kind);
        var a = Assert.Single(ack.Acks);
        Assert.Equal(("9", "0001", "R", "5"), (a.GroupControl, a.TransactionControl, a.Status, a.Reason));
        Assert.Equal(["HL (segment 56 of the transaction): Segment has data element errors; HL02: Invalid code value",
                      "NM1 (segment 70 of the transaction, loop 2010BB): Required segment missing"], a.Errors.Select(e => e.Text));

        var status = Response("clinic-277ca.x12");
        Assert.Equal("7 claims, 5 rejected", status.Summary);
        var total = status.Statuses.Single(s => s.Account == "TOTAL-02");
        Assert.Equal(new StatusCode("A7", "400", ""), Assert.Single(total.Statuses));
        Assert.Equal("SP2770002", total.PayerClaim);

        var remit = Response("sunplan-835.x12");
        Assert.Equal("SUNPLAN", remit.Payer);
        Assert.Equal(new DateOnly(2026, 11, 20), remit.Date);
        Assert.Equal("10 claims, 5 denied, 7 lines unpaid, 611.00 paid", remit.Summary);
        var mue = remit.Remits.Single(r => r.Account == "MUE-04").Lines.Single();
        Assert.Equal((1m, 2m, 175m), (mue.Units!.Value, mue.OriginalUnits!.Value, mue.Paid));
        Assert.Equal([new Adjustment("CO", "151", 175m, null)], mue.Adjustments);
        Assert.Equal(["N362"], mue.Remarks);
        var tele = remit.Remits.Single(r => r.Account == "TELE-OK-05");
        Assert.Equal([new Adjustment("CO", "45", 25m, null), new Adjustment("PR", "3", 20m, null)], tele.Lines[0].Adjustments);
        Assert.Equal(20m, tele.PatientShare);
        Assert.Null(ResponseReader.Load(Samples.Path("clinic-837p.x12")));
    }

    [Fact]
    public void Each_response_replays_against_the_findings()
    {
        Assert.Equal(["Caught PARENT-04 999:7:HL by L2", "Caught NOPAYER-05 999:s3:NM1 by L6"],
            Replay.Run(Response("structure-999.x12"), Scrubbed()).Rows.Select(ResponseTests.Line));
        Assert.Equal(["Caught batch 999:t4 by E6"], Replay.Run(Response("envelope-999.x12"), Scrubbed()).Rows.Select(ResponseTests.Line));
        Assert.Equal(["Caught TOTAL-02 277:400 by A1", "Caught FUTURE-03 277:510 by A3", "Caught DOT-04 277:255 by S4", "NotCheckable REPLACE-06 277:33", "Caught BADDATE-07 277:187 by S2"],
            Replay.Run(Response("clinic-277ca.x12"), Scrubbed()).Rows.Select(ResponseTests.Line));
        var remit = Replay.Run(Response("sunplan-835.x12"), Scrubbed());
        Assert.Equal(
        [
            "Missed LATE-01 line 1 29", "Missed AUTH-02 line 1 197", "Missed TELE-04 line 1 4",
            "Caught NCCI-03 line 2 97 by N1", "Caught MUE-04 line 1 151 by N2", "Caught SEX-05 line 1 10 by N3",
            "NotCheckable CODES-CLEAN-01 line 1 31", "NotCheckable CODES-CLEAN-01 line 2 31",
        ], remit.Rows.Select(ResponseTests.Line));
        Assert.Equal("8 denials: 3 caught by a check, 3 missed, 2 not checkable from a claim.", remit.Summary);
        // With Sun Plan's pack, its payer rules catch the three.
        var packed = Replay.Run(Response("sunplan-835.x12"), Scrubbed(new ScrubSettings { CodeSets = Samples.CodeSets, Packs = [RulePack.Load(Samples.Path("rules/sunplan.json"))] }));
        Assert.Equal(["Caught LATE-01 line 1 29 by P-sunplan-timely", "Caught AUTH-02 line 1 197 by P-sunplan-auth", "Caught TELE-04 line 1 4 by P-sunplan-telehealth"],
            packed.Rows.Take(3).Select(ResponseTests.Line));
        // A claim that isn't open is said so.
        var alone = Replay.Run(Response("sunplan-835.x12"), [Samples.Scrub("payer-837p.x12")]);
        Assert.Equal(5, alone.Count(Verdict.NoClaim));
    }

    [Fact]
    public void Denials_draft_a_rule_pack_that_would_have_caught_them()
    {
        var report = Replay.Run(Response("sunplan-835.x12"), Scrubbed());
        var draft = Replay.Draft(report)!;
        var pack = RulePack.Read(Replay.Text(draft));
        Assert.Equal("SUNPLAN (drafted)", pack.Name);
        Assert.Equal(["SUN01"], pack.Payers);
        Assert.Equal(["prior-auth 90837", "timely-filing 136", "require-modifier 95 in 10"], pack.Rules.Select(r => (string?)r!["kind"] switch
        {
            "prior-auth" => $"prior-auth {string.Join(",", r!["procedures"]!.AsArray().Select(x => (string?)x))}",
            "timely-filing" => $"timely-filing {(int?)r!["days"]}",
            var k => $"{k} {(string?)r!["modifier"]} in {(string?)r["places"]![0]}",
        }));
        Assert.Contains("SUNPLAN's contract", Replay.Text(draft));
        // Checked with the drafted pack, the three denied claims are flagged, and a fresh replay catches them all.
        var settings = new ScrubSettings { CodeSets = Samples.CodeSets, Packs = [pack] };
        var again = Replay.Run(report.Response, Scrubbed(settings));
        Assert.Equal(0, again.Count(Verdict.Missed));
        Assert.Null(Replay.Draft(again));
    }
}

public sealed class ReplayNoteTests
{
    [Fact]
    public void A_miss_by_a_check_whose_code_set_isnt_loaded_says_so()
    {
        var response = ResponseReader.Load(Samples.Path("responses/sunplan-835.x12"))!;
        var noSets = new Core.CodeSets.CodeSets();
        var results = new[] { "edits-837p.x12", "payer-837p.x12", "coding-errors.x12" }.Select(n => Samples.Scrub(n, new ScrubSettings { CodeSets = noSets })).ToList();
        var row = Replay.Run(response, results, noSets).Rows.Single(r => r.Denial.Account == "NCCI-03");
        Assert.Equal(Verdict.Missed, row.Verdict);
        Assert.EndsWith("CMS's NCCI PTP edits aren't loaded (the Code Sets tab).", row.Explanation);
        Assert.DoesNotContain("loaded", Replay.Run(response, results).Rows.Single(r => r.Denial.Account == "NCCI-03").Explanation);
    }
}
