using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Eligibility;

/// <summary>
/// What a payer's 271 said about one person, as far as the eligibility checks need it: who, whether the payer found
/// them, whether coverage is active and between which dates, whether another payer is primary, and which kinds of care
/// need authorization. AVAEligibility reads the whole answer; this is the part a claim can be checked against.
/// </summary>
public sealed record EligibilityAnswer
{
    public required string File { get; init; }
    public string PayerId { get; init; } = "";
    public string PayerName { get; init; } = "";
    public string MemberId { get; init; } = "";
    public string Last { get; init; } = "";
    public string First { get; init; } = "";
    public bool Dependent { get; init; }

    /// <summary>The subscriber's member ID, for a dependent.</summary>
    public string SubscriberId { get; init; } = "";

    /// <summary>AAA03 when the payer couldn't answer about this person.</summary>
    public string Rejection { get; init; } = "";

    /// <summary>True active, false inactive, null when the answer doesn't say.</summary>
    public bool? Active { get; init; }
    public DateOnly? Begins { get; init; }
    public DateOnly? Ends { get; init; }

    /// <summary>The payer the 271 names as primary (EB01 R with a PRP entity), or "" when none.</summary>
    public string PrimaryElsewhere { get; init; } = "";

    /// <summary>The service types the answer says need authorization (EB11 Y).</summary>
    public IReadOnlyList<string> NeedsAuthorization { get; init; } = [];

    /// <summary>When the payer answered (BHT04).</summary>
    public DateOnly? Made { get; init; }

    public required Segment First271 { get; init; }
}

/// <summary>Reads a 271's people (2000C and 2000D) into <see cref="EligibilityAnswer"/>s, and finds the one about a claim.</summary>
public static class EligibilityAnswers
{
    public static List<EligibilityAnswer> Read(X12File file, string name)
    {
        var list = new List<EligibilityAnswer>();
        foreach (var t in file.Transactions.Where(t => t.Code == "271"))
        {
            var repeat = t.Interchange is { } isa && isa[11] is { Length: 1 } r && !char.IsLetterOrDigit(r[0]) ? r[0] : (char?)null;
            var made = t.Body.FirstOrDefault(s => s.Id == "BHT") is { } bht ? Parse.Date(bht[4]) : null;
            string payerId = "", payerName = "";
            var people = new List<List<Segment>>();
            var levels = new List<(string Level, string Parent, string Id)>();
            List<Segment>? current = null;
            var level = "";
            foreach (var s in t.Body)
            {
                if (s.Id == "HL")
                {
                    level = s[3].Trim();
                    current = level is "22" or "23" ? [s] : null;
                    if (current != null)
                    {
                        people.Add(current);
                        levels.Add((level, s[2].Trim(), s[1].Trim()));
                    }
                    continue;
                }
                if (current != null) current.Add(s);
                else if (level == "20" && s.Id == "NM1" && s[1].Trim() == "PR")
                {
                    payerName = s[3].Trim();
                    payerId = s[9].Trim();
                }
            }
            var ids = new Dictionary<string, string>();
            for (var i = 0; i < people.Count; i++)
            {
                var segs = people[i];
                var (lvl, parent, hl) = levels[i];
                var nm1 = segs.FirstOrDefault(s => s.Id == "NM1" && s[1].Trim() is "IL" or "03");
                var member = nm1 != null && nm1[8].Trim() == "MI" ? nm1[9].Trim() : "";
                ids[hl] = member;
                // The person's own loop runs to the first EB; dates there are the plan's.
                var own = segs.TakeWhile(s => s.Id != "EB").ToList();
                var benefits = Benefits(segs);
                var plan = benefits.Where(b => b.Eb[3].Trim().Length == 0 || Split(b.Eb[3], repeat).Contains("30")).ToList();
                bool? active = plan.Any(b => b.Eb[1].Trim() is "1" or "2" or "3" or "4" or "5") ? true
                    : plan.Any(b => b.Eb[1].Trim() is "6" or "7" or "8") ? false : null;
                DateOnly? Date(params string[] qualifiers) =>
                    own.Concat(plan.SelectMany(b => b.Loop)).Where(s => s.Id == "DTP" && qualifiers.Contains(s[1].Trim()) && s[2].Trim() == "D8").Select(s => Parse.Date(s[3])).FirstOrDefault(d => d != null);
                var range = own.FirstOrDefault(s => s.Id == "DTP" && s[1].Trim() is "291" or "307" && s[2].Trim() == "RD8")?[3].Split('-');
                var other = benefits.Where(b => b.Eb[1].Trim() == "R").SelectMany(b => b.Loop).FirstOrDefault(s => s.Id == "NM1" && s[1].Trim() == "PRP");
                list.Add(new EligibilityAnswer
                {
                    File = name, PayerId = payerId, PayerName = payerName, MemberId = member,
                    Last = nm1?[3].Trim() ?? "", First = nm1?[4].Trim() ?? "", Dependent = lvl == "23",
                    SubscriberId = lvl == "23" ? ids.GetValueOrDefault(parent, "") : "",
                    Rejection = own.FirstOrDefault(s => s.Id == "AAA")?[3].Trim() ?? "",
                    Active = active,
                    Begins = Date("346", "356", "348") ?? (range is { Length: 2 } ? Parse.Date(range[0]) : null),
                    Ends = Date("347", "357", "349") ?? (range is { Length: 2 } ? Parse.Date(range[1]) : null),
                    PrimaryElsewhere = other?[3].Trim() ?? "",
                    NeedsAuthorization = [.. benefits.Where(b => b.Eb[11].Trim() == "Y").SelectMany(b => Split(b.Eb[3], repeat)).Distinct()],
                    Made = made, First271 = segs[0],
                });
            }
        }
        return list;
    }

    private static List<(Segment Eb, List<Segment> Loop)> Benefits(List<Segment> segs)
    {
        var list = new List<(Segment, List<Segment>)>();
        foreach (var s in segs)
        {
            if (s.Id == "EB") list.Add((s, []));
            else if (list.Count > 0) list[^1].Item2.Add(s);
        }
        return list;
    }

    private static List<string> Split(string value, char? repeat) =>
        value.Trim().Length == 0 ? [] : repeat is { } r ? [.. value.Split(r).Select(v => v.Trim()).Where(v => v.Length > 0)] : [value.Trim()];

    /// <summary>
    /// The answer about a claim's patient: the same payer (by ID), and the subscriber's member ID with the patient's name
    /// (the subscriber's own, or a dependent under that member ID). The newest answer when there are several.
    /// </summary>
    public static EligibilityAnswer? For(Claim claim, IEnumerable<EligibilityAnswer> answers)
    {
        var payer = claim.Payer?[9].Trim() ?? "";
        var member = claim.Member?[9].Trim() ?? "";
        var patient = claim.Patient?.Child("2010CA")?.Find("NM1", "QC");
        var (last, first) = patient != null ? (patient[3].Trim(), patient[4].Trim()) : (claim.Member?[3].Trim() ?? "", claim.Member?[4].Trim() ?? "");
        return answers.Where(a => a.PayerId.Equals(payer, StringComparison.OrdinalIgnoreCase)
                && a.Last.Equals(last, StringComparison.OrdinalIgnoreCase) && a.First.Equals(first, StringComparison.OrdinalIgnoreCase)
                && (patient != null ? a.Dependent && a.SubscriberId.Equals(member, StringComparison.OrdinalIgnoreCase) || a.MemberId.Equals(member, StringComparison.OrdinalIgnoreCase)
                                    : a.MemberId.Equals(member, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.Made)
            .FirstOrDefault();
    }
}
