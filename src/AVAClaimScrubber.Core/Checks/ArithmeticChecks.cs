using System.Globalization;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 3: arithmetic and dates. The claim's total is the sum of its lines; every line bills some units; no service
/// is dated after the batch was made; an institutional claim's lines fall inside its statement period; a replacement
/// or void names the claim it replaces. Dates are compared with the batch's own creation date (BHT04), not today's,
/// so a batch checks the same whenever it's checked.
/// </summary>
public sealed class ArithmeticChecks : ICheck
{
    private const string Guide = "HIPAA 5010 837 implementation guides (claim and line rules)";

    public static readonly Rule A1 = new("A1", 3, "The claim total is the sum of its lines", "CLM02 must equal the sum of the line charges; clearinghouses reject a claim whose total doesn't balance.", Guide, Severity.Reject);
    public static readonly Rule A2 = new("A2", 3, "Every line bills some units", "A line's units (SV1-04, SV2-05) must be more than zero.", Guide, Severity.Reject);
    public static readonly Rule A3 = new("A3", 3, "No service is in the future", "A service date can't be after the day the batch was made (BHT04).", Guide, Severity.Reject);
    public static readonly Rule A4 = new("A4", 3, "Lines fall inside the statement period", "On an institutional claim, each line's date must be within the statement period (DTP*434).", Guide, Severity.Deny);
    public static readonly Rule A5 = new("A5", 3, "A replacement or void names the original", "A claim with frequency 7 (replacement) or 8 (void) must give the payer's claim number for the original (REF*F8).", Guide, Severity.Reject);
    public static readonly Rule A6 = new("A6", 3, "The frequency code is one that exists", "CLM05-3 is 1 (original), 7 (replacement) or 8 (void) on a professional claim.", Guide, Severity.Reject);

    public IEnumerable<Rule> Rules => [A1, A2, A3, A4, A5, A6];

    public void Run(ScrubContext c)
    {
        foreach (var claim in c.Batch.Claims)
        {
            var sum = claim.Lines.Sum(l => l.Charge ?? 0);
            if (claim.Total is { } total && claim.Lines.Count > 0 && claim.Lines.All(l => l.Charge != null) && total != sum)
            {
                var right = sum.ToString("0.00", CultureInfo.InvariantCulture);
                c.Add(A1, $"CLM02 is {total.ToString("0.00", CultureInfo.InvariantCulture)} but the lines add up to {right}.", claim, segment: claim.Clm, element: "CLM02",
                    fix: new Fix(claim.Clm.Index, 2, 0, right, $"Set CLM02 to {right}"));
            }
            foreach (var line in claim.Lines.Where(l => l.Units is <= 0))
                c.Add(A2, $"Line {line.Number} bills {line.Units?.ToString(CultureInfo.InvariantCulture)} units.", claim, line, line.Service, $"{line.Service.Id}-{line.UnitsElement:00}");

            if (claim.Created is { } made)
                foreach (var line in claim.Lines)
                    if (Parse.Date(line.DateText) is { } d && d > made)
                        c.Add(A3, $"Line {line.Number}'s service date is {d.DayNumber - made.DayNumber} days after the batch was made.", claim, line, line.DateSegment, "DTP03");

            var period = claim.Loop.Find("DTP", "434");
            if (claim.Kind == ClaimKind.Institutional && period != null && period[3].Split('-') is [var a, var b] && Parse.Date(a) is { } from && Parse.Date(b) is { } to)
                foreach (var line in claim.Lines)
                    if (Parse.Date(line.DateText) is { } d && (d < from || d > to))
                        c.Add(A4, d > to ? $"Line {line.Number} is dated {d.DayNumber - to.DayNumber} days after the statement period ends." : $"Line {line.Number} is dated {from.DayNumber - d.DayNumber} days before the statement period starts.",
                            claim, line, line.DateSegment, "DTP03");

            if (claim.Frequency is "7" or "8" && claim.Loop.Find("REF", "F8") == null)
                c.Add(A5, $"The claim is a {(claim.Frequency == "7" ? "replacement" : "void")} (frequency {claim.Frequency}) but has no REF*F8 with the original claim's number.", claim, segment: claim.Clm, element: "CLM05-3");
            if (claim.Kind == ClaimKind.Professional && claim.Frequency.Length > 0 && claim.Frequency is not ("1" or "7" or "8"))
                c.Add(A6, $"CLM05-3 is \"{claim.Frequency}\": a professional claim's frequency is 1, 7 or 8.", claim, segment: claim.Clm, element: "CLM05-3");
        }
    }
}
