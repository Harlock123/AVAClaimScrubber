using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.Eligibility;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 7: eligibility, from the payer's own 271 answers when they're open (none open: the layer checks nothing). Each
/// claim is matched to the answer about its patient (payer ID, member ID and name), and checked against it: coverage on
/// the service dates (V1), whether the payer found the member at all (V2), another payer primary while this claim goes
/// here as primary (V3), and care the answer says needs authorization billed with no authorization number (V4).
/// Messages say what the answer says without quoting people's details.
/// </summary>
public sealed class EligibilityChecks(IReadOnlyList<EligibilityAnswer> answers) : ICheck
{
    private const string Source = "The payer's 271 eligibility answer (ASC X12 005010X279A1)";

    public static readonly Rule V1 = new("V1", 7, "Coverage covers the dates of service",
        "A payer denies services outside the patient's coverage (CARC 26, 27); the 271 says when coverage begins and ends, and whether it's active.", Source, Severity.Deny);
    public static readonly Rule V2 = new("V2", 7, "The payer has the member on file",
        "A payer that couldn't find the member when asked about eligibility (AAA 72, 73, 75…) rejects or denies the claim the same way (CARC 31; 277CA status 33).", Source, Severity.Reject);
    public static readonly Rule V3 = new("V3", 7, "This payer is primary when billed as primary",
        "When the 271 names another payer as primary, a claim billed here as primary is denied for coordination of benefits (CARC 22).", Source, Severity.Deny);
    public static readonly Rule V4 = new("V4", 7, "Care the plan authorizes carries the authorization",
        "When the 271 says a kind of care needs authorization, a claim for it without the authorization number (REF*G1) is denied (CARC 197).", Source, Severity.Deny);

    public IEnumerable<Rule> Rules => [V1, V2, V3, V4];

    /// <summary>The claims an answer was found for, so the replay can tell "no 271 open for it" from "the 271 didn't show it".</summary>
    public HashSet<int> Answered { get; } = [];

    public void Run(ScrubContext c)
    {
        if (answers.Count == 0) return;
        foreach (var claim in c.Batch.Claims)
        {
            if (EligibilityAnswers.For(claim, answers) is not { } a) continue;
            Answered.Add(claim.Number);
            c.Answered.Add(claim.Number);
            var member = claim.Member;
            if (a.Rejection.Length > 0)
            {
                c.Add(V2, $"The payer's 271 ({a.File}) couldn't find this member: AAA {a.Rejection}, {EligibilityCodes.Say(EligibilityCodes.Reject, a.Rejection)}. Check the member ID, name and birth date against the card, or ask for the current one.", claim, segment: member, element: "NM109", loop: "2010BA");
                continue;
            }
            if (a.Active == false)
                c.Add(V1, $"The payer's 271 ({a.File}) says coverage isn't active" + (a.Ends is { } e && claim.Created is { } made ? $": it ended {made.DayNumber - e.DayNumber} days before the batch was made." : ".") + " Bill the patient's current plan, or the patient.", claim, segment: member, loop: "2010BA");
            else
                foreach (var line in claim.Lines)
                {
                    if (Parse.Date(line.DateText) is not { } day) continue;
                    if (a.Ends is { } end && day > end)
                        c.Add(V1, $"The payer's 271 ({a.File}) says coverage ends {day.DayNumber - end.DayNumber} days before this service.", claim, line);
                    else if (a.Begins is { } begin && day < begin)
                        c.Add(V1, $"The payer's 271 ({a.File}) says coverage begins {begin.DayNumber - day.DayNumber} days after this service.", claim, line);
                }
            var sbr = claim.Subscriber?.Find("SBR");
            if (a.PrimaryElsewhere.Length > 0 && sbr != null && sbr[1].Trim() == "P")
                c.Add(V3, $"The payer's 271 ({a.File}) says {a.PrimaryElsewhere} is primary, and this claim is billed here as primary (SBR01 P). Bill {a.PrimaryElsewhere} first, then this payer as secondary.", claim, segment: sbr, element: "SBR01", loop: "2000B");
            if (a.NeedsAuthorization.Count > 0 && !HasAuthorization(claim))
                foreach (var line in claim.Lines)
                    if (ServiceTypes.Care(line.Procedure) is { } care && a.NeedsAuthorization.Contains(care))
                        c.Add(V4, $"The payer's 271 ({a.File}) says {EligibilityCodes.Say(EligibilityCodes.ServiceType, care)} needs authorization, and the claim has no authorization number (REF*G1).", claim, line);
        }
    }

    private static bool HasAuthorization(Claim claim) =>
        claim.Loop.All().SelectMany(l => l.Segments).Any(s => s.Id == "REF" && s[1].Trim() == "G1" && s[2].Trim().Length > 0);
}
