using System.Globalization;
using System.Text.Json.Nodes;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Core.Responses;

/// <summary>One denial or rejection a payer sent back: on a claim (Line 0) or a line, with its codes.</summary>
public sealed record Denial(string Account, int Line, string Procedure, string Code, string Text, IReadOnlyList<string> Remarks, decimal Amount, string From, string Batch = "")
{
    /// <summary>Its codes and their meaning, for a person: "CO-197 Precertification… (N54 …)".</summary>
    public string Description => Text + (Remarks.Count > 0 ? " (" + string.Join("; ", Remarks.Select(r => $"{r} {ResponseCodes.Describe(ResponseCodes.Rarc, r)}")) + ")" : "");
}

public enum Verdict
{
    /// <summary>A check flagged the claim (or the line) for the reason the payer gave.</summary>
    Caught,

    /// <summary>A check could have caught it and didn't: a rule to add or turn on.</summary>
    Missed,

    /// <summary>Nothing in a claim can show it (eligibility, medical necessity, the payer's records).</summary>
    NotCheckable,

    /// <summary>The claim it's about isn't open.</summary>
    NoClaim,
}

/// <summary>A denial, the claim it's about, the checks that answer to its reason, and whether one caught it.</summary>
public sealed class ReplayRow
{
    public required Denial Denial { get; init; }
    public ScrubResult? Result { get; init; }
    public Claim? Claim { get; init; }
    public required IReadOnlyList<string> Rules { get; init; }
    public required IReadOnlyList<string> Kinds { get; init; }
    public required IReadOnlyList<Finding> CaughtBy { get; init; }
    public required Verdict Verdict { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>A response file replayed against the open claims and their findings.</summary>
public sealed class ReplayReport
{
    public required ResponseFile Response { get; init; }
    public required IReadOnlyList<ReplayRow> Rows { get; init; }
    /// <summary>The open files' scrubs the replay was run against.</summary>
    public IReadOnlyList<ScrubResult> Results { get; init; } = [];
    public int Count(Verdict v) => Rows.Count(r => r.Verdict == v);

    public string Summary =>
        $"{Rows.Count} denial{(Rows.Count == 1 ? "" : "s")}: {Count(Verdict.Caught)} caught by a check, {Count(Verdict.Missed)} missed, " +
        $"{Count(Verdict.NotCheckable)} not checkable from a claim" + (Count(Verdict.NoClaim) > 0 ? $", {Count(Verdict.NoClaim)} for claims not open" : "") + ".";
}

/// <summary>
/// Denial replay: each denial in an 835 (and each rejection in a 277CA or 999), set against the scrub of the claim it's
/// about. The reason codes map to the checks that answer to them (CARC 197, prior authorization, to the prior-auth payer
/// rules; CARC 97, bundled, to NCCI; CARC 16 with RARC M76, an invalid diagnosis, to the diagnosis checks), so the
/// replay says which denials the scrubber would have stopped, which it missed, and which no check could catch.
/// </summary>
public static class Replay
{
    /// <summary>A reason's checks: rule ids, payer-rule kinds, and why it can't be checked when it can't.</summary>
    public sealed record Mapping(string[] Rules, string[] Kinds, string? NotCheckable = null);

    private static readonly string[] Structural = ["E1", "E2", "E3", "E4", "E5", "E6", "E7", "S1", "S2", "S3", "L1", "L2", "L3", "L4", "L5", "L6", "L7", "L8", "L9", "L10", "L11", "L12"];

    /// <summary>CARCs (the 835's reasons) and what answers to each. Ones not here are normal adjustments or unknown.</summary>
    public static readonly IReadOnlyDictionary<string, Mapping> Carc = new Dictionary<string, Mapping>
    {
        ["4"] = new(["C5"], ["require-modifier"]),
        ["5"] = new(["C6"], []),
        ["58"] = new(["C6"], []),
        ["6"] = new(["N4"], []),
        ["9"] = new(["N4"], []),
        ["7"] = new(["N3"], []),
        ["10"] = new(["N3"], []),
        ["16"] = new(Structural, []),
        ["18"] = new([], [], "a duplicate of a claim already sent: the scrubber sees one batch at a time"),
        ["22"] = new([], [], "coordination of benefits: the payer's records, not the claim"),
        ["26"] = new([], [], "coverage dates: eligibility, not the claim"),
        ["27"] = new([], [], "coverage dates: eligibility, not the claim"),
        ["29"] = new([], ["timely-filing"]),
        ["31"] = new([], [], "the patient isn't found as insured: eligibility, not the claim"),
        ["49"] = new([], [], "a routine or screening service the plan doesn't cover: benefits"),
        ["50"] = new([], [], "medical necessity: a clinical judgement"),
        ["55"] = new([], [], "experimental treatment: a clinical judgement"),
        ["96"] = new([], ["not-covered"]),
        ["204"] = new([], ["not-covered"]),
        ["97"] = new(["N1"], []),
        ["234"] = new(["N1"], []),
        ["236"] = new(["N1", "C5"], []),
        ["109"] = new([], [], "sent to the wrong payer: the payer's records"),
        ["119"] = new(["N2"], ["max-units"]),
        ["151"] = new(["N2"], ["max-units"]),
        ["222"] = new(["N2"], ["max-units"]),
        ["146"] = new(["C1", "C2"], []),
        ["167"] = new([], [], "the diagnosis isn't covered: the payer's policy"),
        ["181"] = new(["C3", "C4"], []),
        ["182"] = new(["C5"], []),
        ["170"] = new([], [], "the provider type: the payer's credentialing records"),
        ["185"] = new([], [], "the rendering provider's eligibility: the payer's credentialing records"),
        ["B7"] = new([], [], "the provider's certification: the payer's records"),
        ["197"] = new([], ["prior-auth"]),
        ["206"] = new(["C7", "S1"], []),
        ["207"] = new(["C7"], []),
        ["208"] = new([], [], "the NPI isn't in the payer's records (the NPPES lookup is off)"),
        ["226"] = new([], [], "information the payer asked for: a request, not the claim"),
        ["252"] = new([], [], "an attachment is needed: documentation, not the claim"),
        ["242"] = new([], [], "out of network: the payer's records"),
        ["B13"] = new([], [], "paid before: the payer's records"),
    };

    /// <summary>RARCs that narrow CARC 16 (or A1) to particular checks.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Rarc = new Dictionary<string, string[]>
    {
        ["M76"] = ["C1", "C2", "L8", "L11", "S4"],
        ["M51"] = ["C3", "C4"],
        ["M20"] = ["C3"],
        ["N56"] = ["C3", "C4"],
        ["N290"] = ["C7"],
        ["N362"] = ["N2"],
        ["N54"] = [],
    };

    /// <summary>The 277CA's status codes (with category A3, A6, A7 or A8, a rejection) and the checks that answer to them.</summary>
    public static readonly IReadOnlyDictionary<string, Mapping> Status = new Dictionary<string, Mapping>
    {
        ["400"] = new(["A1"], []),
        ["178"] = new(["A1", "S3"], []),
        ["187"] = new(["S2", "A3", "A4"], []),
        ["510"] = new(["A3"], []),
        ["511"] = new(["S1"], []),
        ["255"] = new(["C1", "C2", "S4", "L8"], []),
        ["454"] = new(["C3", "C4"], []),
        ["507"] = new(["C3"], []),
        ["453"] = new(["C5"], []),
        ["562"] = new(["C7"], []),
        ["145"] = new(["C8"], []),
        ["32"] = new([], [], "the subscriber or policy isn't found: eligibility, not the claim"),
        ["33"] = new([], [], "the subscriber isn't found: eligibility, not the claim"),
    };

    /// <summary>Adjustments that are how payment works, not denials: contract write-downs, cost sharing, reductions.</summary>
    private static readonly HashSet<string> Normal = ["1", "2", "3", "23", "24", "45", "59", "253"];

    /// <summary>The denials and rejections in a response file.</summary>
    public static List<Denial> Denials(ResponseFile response)
    {
        var list = new List<Denial>();
        foreach (var c in response.Remits)
        {
            foreach (var a in c.Adjustments.Where(IsDenial))
                list.Add(new Denial(c.Account, 0, "", a.Reason, a.Text, c.Remarks, a.Amount, "835"));
            foreach (var l in c.Lines)
                foreach (var a in l.Adjustments.Where(IsDenial))
                    list.Add(new Denial(c.Account, l.Number, l.Procedure, a.Reason, a.Text, l.Remarks.Concat(c.Remarks).ToList(), a.Amount, "835"));
        }
        foreach (var s in response.Statuses)
        {
            foreach (var st in s.Statuses.Where(x => x.Rejected))
                list.Add(new Denial(s.Account, 0, "", "277:" + st.Code, st.Text, [], 0, "277CA"));
            foreach (var (l, i) in s.Lines.Select((l, i) => (l, i)))
                foreach (var st in l.Statuses.Where(x => x.Rejected))
                    list.Add(new Denial(s.Account, i + 1, l.Procedure, "277:" + st.Code, st.Text, [], 0, "277CA"));
        }
        return list;
    }

    private static bool IsDenial(Adjustment a) => a.Group != "PR" && !Normal.Contains(a.Reason) && a.Amount > 0;

    /// <summary>Replays a response file's denials against the open files' scrubs (claims matched by CLM01).</summary>
    public static ReplayReport Run(ResponseFile response, IEnumerable<ScrubResult> results)
    {
        var all = results.ToList();
        var rows = new List<ReplayRow>();
        foreach (var d in Denials(response).Concat(AckDenials(response, all)))
        {
            var (result, claim) = d.Account.Length > 0 ? Find(d.Account, all) : (null, null);
            if (d.Account.Length == 0 && d.From == "999")
            {
                // A batch-level 999 error: caught if the batch has an envelope or structure finding.
                var (codes, _, _) = Map(d);
                var batch = all.Where(r => d.Batch.Length == 0 || r.Batch.Name == d.Batch).SelectMany(r => r.Envelope).Where(f => codes.Contains(f.Rule.Id)).ToList();
                rows.Add(new ReplayRow
                {
                    Denial = d, Rules = codes, Kinds = [], CaughtBy = batch, Verdict = batch.Count > 0 ? Verdict.Caught : Verdict.Missed,
                    Explanation = batch.Count > 0 ? "Caught by " + string.Join(", ", batch.Select(f => f.Rule.Id).Distinct()) + ": " + batch[0].Message : "Missed: no envelope or structure check found it.",
                });
                continue;
            }
            var (rules, kinds, notCheckable) = Map(d);
            if (claim == null || result == null)
            {
                rows.Add(new ReplayRow { Denial = d, Rules = rules, Kinds = kinds, CaughtBy = [], Verdict = Verdict.NoClaim, Explanation = $"No open file has a claim {d.Account}." });
                continue;
            }
            var line = d.Line > 0 ? LineFor(claim, d, response) : null;
            var caught = result.Of(claim)
                .Where(f => rules.Contains(f.Rule.Id) || (f.Rule.Kind.Length > 0 && kinds.Contains(f.Rule.Kind)))
                .Where(f => line == null || f.Line == 0 || f.Line == line.Number)
                .ToList();
            var (verdict, why) = caught.Count > 0
                ? (Verdict.Caught, "Caught by " + string.Join(", ", caught.Select(f => f.Rule.Id).Distinct()) + ": " + caught[0].Message)
                : notCheckable != null
                    ? (Verdict.NotCheckable, "Not checkable from a claim: " + notCheckable + ".")
                    : rules.Count == 0 && kinds.Count == 0
                        ? (Verdict.NotCheckable, "No check answers to this reason.")
                        : (Verdict.Missed, "Missed: " + string.Join("; ", new[]
                        {
                            rules.Count > 0 ? $"checks {string.Join(", ", rules.Take(6))}{(rules.Count > 6 ? "…" : "")} found nothing" : null,
                            kinds.Count > 0 ? $"a payer rule ({string.Join(", ", kinds)}) would catch it" : null,
                        }.Where(x => x != null)) + ".");
            rows.Add(new ReplayRow { Denial = d, Result = result, Claim = claim, Rules = rules, Kinds = kinds, CaughtBy = caught, Verdict = verdict, Explanation = why });
        }
        return new ReplayReport { Response = response, Rows = rows, Results = all };
    }

    /// <summary>
    /// A 999's errors placed on claims: the transaction is matched by its control numbers (AK1-02 to GS06, AK2-02 to
    /// ST02), and each IK3's position (counting from ST) to the claim whose loop holds that segment. Errors outside
    /// every claim are the batch's (account "").
    /// </summary>
    public static List<Denial> AckDenials(ResponseFile response, IReadOnlyList<ScrubResult> results)
    {
        var list = new List<Denial>();
        foreach (var ack in response.Acks.Where(a => a.Errors.Count > 0 || a.Rejected))
        {
            var match = results.SelectMany(r => r.Batch.File.Transactions.Where(t => t.Code == "837").Select(t => (r, t)))
                .FirstOrDefault(x => x.t.Control == ack.TransactionControl && (x.t.Group == null || x.t.Group[6].Trim() == ack.GroupControl));
            var file = match.r?.Batch.Name ?? "";
            if (ack.Errors.Count == 0)
                list.Add(new Denial("", 0, "", "999:t" + ack.Reason, $"Transaction {ack.TransactionControl}: {ack.StatusText}" + (ack.Reason.Length > 0 ? $" ({Ack999Codes.TransactionError(ack.Reason)})" : ""), [], 0, "999", file));
            foreach (var e in ack.Errors)
            {
                var account = match.r == null ? "" : ClaimAt(match.r.Batch, match.t.Start.Index + e.Position - 1)?.Account ?? "";
                list.Add(new Denial(account, 0, "", $"999:{(e.Elements.Count > 0 ? e.Elements[0].Code : "s" + e.Code)}:{e.SegmentId}", e.Text, [], 0, "999", file));
            }
        }
        return list;
    }

    /// <summary>
    /// The claim a segment belongs to: the claim whose loops hold it, or, for a subscriber's or patient's segment before
    /// a CLM, the next claim. The billing provider's and the envelope's segments are the batch's (null).
    /// </summary>
    public static Claim? ClaimAt(ClaimBatch batch, int index)
    {
        if (batch.Claims.FirstOrDefault(c => c.Loop.All().SelectMany(l => l.Segments).Any(x => x.Index == index)) is { } held) return held;
        if (batch.Claims.Any(c => c.BillingProvider is { } b && (b.Segments.Concat(b.Children.Where(x => x.Id.StartsWith("2010A", StringComparison.Ordinal)).SelectMany(x => x.Segments))).Any(x => x.Index == index)))
            return null;
        var next = batch.Claims.Where(c => c.Clm.Index > index).MinBy(c => c.Clm.Index);
        var st = batch.File.Transactions.FirstOrDefault(t => t.Start.Index < index && (t.Trailer == null || t.Trailer.Index > index));
        return next != null && (st == null || st.Trailer == null || next.Clm.Index < st.Trailer.Index) ? next : null;
    }

    /// <summary>The checks a 999 error's segment answers to.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> SegmentRules = new Dictionary<string, string[]>
    {
        ["HL"] = ["L1", "L2", "L3"], ["NM1"] = ["L4", "L5", "L6", "L7", "C7"], ["SE"] = ["E5", "E6"], ["CLM"] = ["A1", "A5", "A6", "S3", "C6"],
        ["HI"] = ["C1", "C2", "S4", "L8", "L11"], ["SV1"] = ["C3", "C4", "C5", "C6", "A2", "L11"], ["SV2"] = ["C3", "C4", "C5", "A2"],
        ["DTP"] = ["S2", "A3", "A4"], ["LX"] = ["L10"], ["PRV"] = ["C8"], ["REF"] = ["A5"],
    };

    /// <summary>The checks a 999 error code answers to: element errors (IK4-03) or segment errors (IK3-04, "s" first).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AckRules = new Dictionary<string, string[]>
    {
        ["1"] = ["S1"], ["2"] = ["S1"], ["4"] = ["S1"], ["5"] = ["S1"], ["6"] = ["S1"], ["7"] = ["S1", "S4", "A6"], ["8"] = ["S2"], ["9"] = ["S2"],
        ["I12"] = ["S1", "S4"], ["s3"] = ["L4", "L5", "L6", "L7", "L8", "L9"], ["s8"] = ["S1", "S2", "S3", "S4"],
    };

    private static (IReadOnlyList<string> Rules, IReadOnlyList<string> Kinds, string? NotCheckable) Map(Denial d)
    {
        if (d.Code.StartsWith("999:t", StringComparison.Ordinal))
            return (d.Code[5..] switch { "3" => ["E5"], "4" => ["E6"], "2" => ["E5", "E6"], _ => Structural }, [], null);
        if (d.Code.StartsWith("999:", StringComparison.Ordinal))
        {
            var parts = d.Code.Split(':');
            var rules = (AckRules.TryGetValue(parts[1], out var r) ? r : []).Concat(SegmentRules.TryGetValue(parts[2], out var sr) ? sr : []).Distinct().ToArray();
            return (rules.Length > 0 ? rules : Structural, [], null);
        }
        if (d.Code.StartsWith("277:", StringComparison.Ordinal))
            return Status.TryGetValue(d.Code[4..], out var s) ? (s.Rules, s.Kinds, s.NotCheckable) : ([], [], null);
        var narrowed = d.Remarks.Where(Rarc.ContainsKey).SelectMany(r => Rarc[r]).Distinct().ToArray();
        if (d.Code is "16" or "A1" && narrowed.Length > 0) return (narrowed, [], null);
        return Carc.TryGetValue(d.Code, out var m) ? (m.Rules, m.Kinds, m.NotCheckable) : ([], [], null);
    }

    /// <summary>The claim with this account (CLM01) in the open files; the first, if two have it.</summary>
    public static (ScrubResult? Result, Claim? Claim) Find(string account, IEnumerable<ScrubResult> results)
    {
        foreach (var r in results)
            if (r.Batch.Claims.FirstOrDefault(c => c.Account == account) is { } c) return (r, c);
        return (null, null);
    }

    /// <summary>The 837 line an 835 or 277 line is about: the same procedure (and date, when given), in order.</summary>
    private static ServiceLine? LineFor(Claim claim, Denial d, ResponseFile response)
    {
        var remit = response.Remits.FirstOrDefault(r => r.Account == d.Account)?.Lines.FirstOrDefault(l => l.Number == d.Line);
        var same = claim.Lines.Where(l => l.Procedure == d.Procedure && (remit == null || remit.Date.Length == 0 || l.DateText.StartsWith(remit.Date, StringComparison.Ordinal))).ToList();
        if (same.Count == 0) return claim.Lines.FirstOrDefault(l => l.Number == d.Line);
        var nth = response.Remits.FirstOrDefault(r => r.Account == d.Account)?.Lines.Where(l => l.Number < d.Line && l.Procedure == d.Procedure).Count() ?? 0;
        return nth < same.Count ? same[nth] : same[0];
    }

    /// <summary>A drafted pack as the text of a rule pack file (indented, apostrophes as they are).</summary>
    public static string Text(JsonObject pack) => pack.ToJsonString(new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>
    /// A draft rule pack from the denials a payer rule would have caught and none did: prior authorization and
    /// not-covered procedures, a filing limit (below the shortest gap denied), unit limits (the units paid). It's a
    /// draft for a person to check against the payer's policy: each rule's "why" says what it was drafted from.
    /// Null when there's nothing to draft.
    /// </summary>
    public static JsonObject? Draft(ReplayReport report, DateOnly? today = null)
    {
        var missed = report.Rows.Where(r => r.Verdict == Verdict.Missed && r.Claim != null && r.Kinds.Count > 0).ToList();
        if (missed.Count == 0) return null;
        var payers = missed.Select(r => r.Claim!.Payer?[9].Trim() ?? "").Where(p => p.Length > 0).Distinct().ToList();
        var name = report.Response.Payer.Length > 0 ? report.Response.Payer : payers.FirstOrDefault() ?? "Payer";
        var rules = new JsonArray();
        string From(IEnumerable<ReplayRow> rows, string code) =>
            $"{rows.Count()} denial{(rows.Count() == 1 ? "" : "s")} in {report.Response.Name} (CARC {code}: {ResponseCodes.Describe(ResponseCodes.Carc, code)})";

        foreach (var g in missed.Where(r => r.Kinds.Contains("prior-auth")).GroupBy(r => r.Denial.Code))
            rules.Add(new JsonObject
            {
                ["id"] = "draft-prior-auth", ["kind"] = "prior-auth",
                ["procedures"] = new JsonArray([.. g.Select(r => r.Denial.Procedure).Where(p => p.Length > 0).Distinct().Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]),
                ["why"] = $"Drafted from {From(g, g.Key)}: check which procedures {name} needs authorization for.",
            });
        foreach (var g in missed.Where(r => r.Kinds.Contains("not-covered")).GroupBy(r => r.Denial.Code))
            rules.Add(new JsonObject
            {
                ["id"] = "draft-not-covered", ["kind"] = "not-covered",
                ["procedures"] = new JsonArray([.. g.Select(r => r.Denial.Procedure).Where(p => p.Length > 0).Distinct().Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]),
                ["why"] = $"Drafted from {From(g, g.Key)}: check {name}'s coverage policy.",
            });
        var late = missed.Where(r => r.Kinds.Contains("timely-filing") && r.Claim!.Created != null).ToList();
        var gaps = late.Select(r => r.Claim!.Created!.Value.DayNumber - (r.Claim.Lines.Select(l => Parse.Date(l.DateText)).Where(d => d != null).Min() ?? r.Claim.Created.Value).DayNumber).Where(g => g > 1).ToList();
        if (gaps.Count > 0)
            rules.Add(new JsonObject
            {
                ["id"] = "draft-timely", ["kind"] = "timely-filing", ["days"] = gaps.Min() - 1,
                ["why"] = $"Drafted from {From(late, "29")}: the shortest gap denied was {gaps.Min()} days, so the limit is less; check {name}'s contract for the real one.",
            });
        // A modifier a place of service needs: one every paid line of the procedure in that place had, and the denied one didn't.
        foreach (var r in missed.Where(r => r.Kinds.Contains("require-modifier") && r.Denial.Procedure.Length > 0))
        {
            var denied = r.Claim!.Lines.FirstOrDefault(l => l.Procedure == r.Denial.Procedure);
            if (denied == null) continue;
            var place = denied.PlaceOfService.Length > 0 ? denied.PlaceOfService : r.Claim.Facility;
            var paid = report.Response.Remits.SelectMany(c => c.Lines.Where(l => l.Paid > 0 && l.Procedure == r.Denial.Procedure).Select(l => (c.Account, l)))
                .Select(x => Find(x.Account, report.Results).Claim)
                .Where(c => c != null && (c.Lines.FirstOrDefault(l => l.Procedure == r.Denial.Procedure)?.PlaceOfService is { Length: > 0 } p ? p : c.Facility) == place)
                .Select(c => c!.Lines.First(l => l.Procedure == r.Denial.Procedure).Modifiers.ToHashSet())
                .ToList();
            if (paid.Count == 0) continue;
            var common = paid.Aggregate((a, b) => [.. a.Intersect(b)]);
            common.ExceptWith(denied.Modifiers);
            if (common.Count == 0 || rules.OfType<JsonObject>().Any(x => (string?)x["kind"] == "require-modifier" && (string?)x["modifier"] == common.Min())) continue;
            rules.Add(new JsonObject
            {
                ["id"] = $"draft-modifier-{common.Min()!.ToLowerInvariant()}", ["kind"] = "require-modifier",
                ["places"] = new JsonArray(place), ["modifier"] = common.Min(),
                ["why"] = $"Drafted from {From([r], r.Denial.Code)}: {name} paid {r.Denial.Procedure} in place of service {place} with modifier {common.Min()}, and denied it without.",
            });
        }
        foreach (var g in missed.Where(r => r.Kinds.Contains("max-units") && r.Denial.Procedure.Length > 0).GroupBy(r => r.Denial.Procedure))
        {
            var paid = g.Select(r => report.Response.Remits.FirstOrDefault(x => x.Account == r.Denial.Account)?.Lines.FirstOrDefault(l => l.Number == r.Denial.Line)?.Units).Where(u => u > 0).ToList();
            if (paid.Count == 0) continue;
            rules.Add(new JsonObject
            {
                ["id"] = $"draft-units-{g.Key.ToLowerInvariant()}", ["kind"] = "max-units", ["procedure"] = g.Key, ["units"] = paid.Min(),
                ["why"] = $"Drafted from {From(g, g.First().Denial.Code)}: {name} paid {paid.Min():0.##} units of {g.Key}.",
            });
        }
        if (rules.Count == 0) return null;
        return new JsonObject
        {
            ["format"] = RulePack.Format,
            ["name"] = $"{name} (drafted)",
            // The day it was drafted: the remittance's own date is about the claims, so it stays out of the pack.
            ["version"] = (today ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            ["description"] = $"Drafted by AVA Claim Scrubber from the denials in {report.Response.Name} that a payer rule would have caught. A draft: check each rule against {name}'s policy before relying on it.",
            ["draft"] = true,
            ["payers"] = new JsonArray([.. payers.Select(p => (JsonNode)p)]),
            ["rules"] = rules,
        };
    }
}
