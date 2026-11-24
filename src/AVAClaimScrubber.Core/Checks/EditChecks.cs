using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.CodeSets;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 5: the edits Medicare publishes and most payers follow. NCCI procedure-to-procedure edits (two codes billed
/// for one patient on one day where the second is part of the first; allowed only when the edit's modifier indicator
/// is 1 and the line carries an NCCI-associated modifier), medically unlikely edits (more units than one patient gets,
/// per line or per day), and diagnoses that don't fit the patient's sex or age, by ICD-10-CM chapter as the Medicare
/// Code Editor's categories do (not the MCE's full tables).
/// </summary>
public sealed class EditChecks(CodeSets.CodeSets? sets) : ICheck
{
    public static readonly Rule N1 = new("N1", 5, "No NCCI procedure-to-procedure pair", "Two codes an NCCI edit pairs, billed for one patient on one day, pay only the first, unless the edit allows a modifier and the line has one (59, XE, XS, XP, XU…).", "CMS NCCI PTP edits (the quarter imported)", Severity.Deny);
    public static readonly Rule N2 = new("N2", 5, "Units within the medically unlikely edit", "A code's units can't be more than its MUE: per line (MAI 1) or per day (MAI 2 and 3).", "CMS NCCI MUE tables (practitioner; outpatient hospital)", Severity.Deny);
    public static readonly Rule N3 = new("N3", 5, "The diagnosis fits the patient's sex", "Pregnancy codes (O, Z33–Z3A) and female genital codes (N70–N98) are for women; male genital codes (N40–N53, C60–C63) for men.", "Medicare Code Editor categories (sex conflict), by ICD-10-CM chapter", Severity.Deny);
    public static readonly Rule N4 = new("N4", 5, "The diagnosis fits the patient's age", "Perinatal codes (P00–P96) are for newborns (age 0); pregnancy codes (O) for ages 9 to 64.", "Medicare Code Editor categories (age conflict), by ICD-10-CM chapter", Severity.Deny);

    /// <summary>Modifiers NCCI lets bypass a PTP edit whose indicator is 1.</summary>
    public static readonly HashSet<string> NcciModifiers =
        ["24", "25", "27", "57", "58", "59", "78", "79", "91", "XE", "XP", "XS", "XU", "E1", "E2", "E3", "E4", "FA", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9",
         "LC", "LD", "LM", "LT", "RC", "RI", "RT", "TA", "T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T9"];

    public IEnumerable<Rule> Rules => [N1, N2, N3, N4];

    public void Run(ScrubContext c)
    {
        foreach (var claim in c.Batch.Claims)
        {
            Ptp(c, claim);
            Mue(c, claim);
            Demographics(c, claim);
        }
    }

    private void Ptp(ScrubContext c, Claim claim)
    {
        if (sets?.Ptp is not { } ptp) return;
        foreach (var day in claim.Lines.Where(l => l.Procedure.Length == 5).GroupBy(l => l.DateText))
        {
            var lines = day.ToList();
            foreach (var a in lines)
                foreach (var b in lines.Where(b => b != a))
                {
                    if (!ptp.TryGetValue(a.Procedure + "|" + b.Procedure, out var edit)) continue;
                    var date = Parse.Date(day.Key);
                    if (date is { } d && (edit.Effective > d || edit.Deleted is { } del && del < d)) continue;
                    var bypassed = edit.Modifier == '1' && (b.Modifiers.Concat(a.Modifiers).Any(NcciModifiers.Contains));
                    if (bypassed) continue;
                    c.Add(N1, edit.Modifier == '0'
                            ? $"Line {b.Number}'s {b.Procedure} is part of line {a.Number}'s {a.Procedure} on the same day (NCCI pair; no modifier allows it)."
                            : $"Line {b.Number}'s {b.Procedure} is part of line {a.Number}'s {a.Procedure} on the same day (NCCI pair): if they were distinct services, a modifier such as 59 or XU says so.",
                        claim, b, b.Service, b.Service.Id == "SV2" ? "SV2-02" : "SV1-01");
                }
        }
    }

    private void Mue(ScrubContext c, Claim claim)
    {
        var table = claim.Kind == ClaimKind.Professional ? sets?.MuePractitioner : sets?.MueHospital;
        if (table == null) return;
        foreach (var line in claim.Lines.Where(l => l.Units is > 0 && table.ContainsKey(l.Procedure)))
        {
            var mue = table[line.Procedure];
            var units = mue.Mai == 1 ? line.Units!.Value : claim.Lines.Where(l => l.Procedure == line.Procedure && l.DateText == line.DateText).Sum(l => l.Units ?? 0);
            if (units <= mue.Units) continue;
            if (mue.Mai != 1 && claim.Lines.First(l => l.Procedure == line.Procedure && l.DateText == line.DateText) != line) continue; // once a day
            c.Add(N2, $"{line.Procedure} bills {units:0.##} units {(mue.Mai == 1 ? "on line " + line.Number : "on line " + line.Number + "'s day")}; its MUE is {mue.Units} {(mue.Mai == 1 ? "a line" : "a day")}" + (mue.Rationale.Length > 0 ? $" ({mue.Rationale})." : "."),
                claim, line, line.Service, $"{line.Service.Id}-{line.UnitsElement:00}");
        }
    }

    private static void Demographics(ScrubContext c, Claim claim)
    {
        var dmg = claim.Demographics;
        if (dmg == null) return;
        var sex = dmg[3].Trim();
        var birth = dmg[1].Trim() == "D8" ? Parse.Date(dmg[2]) : null;
        var served = claim.Lines.Select(l => Parse.Date(l.DateText)).FirstOrDefault(d => d != null) ?? claim.Created;
        int? age = birth is { } b && served is { } s ? s.Year - b.Year - (s < b.AddYears(s.Year - b.Year) ? 1 : 0) : null;
        foreach (var dx in claim.Diagnoses)
        {
            var code = dx.Code.Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
            var female = code.StartsWith('O') || InRange(code, "Z33", "Z3A") || InRange(code, "N70", "N98");
            var male = InRange(code, "N40", "N53") || InRange(code, "C60", "C63");
            if (sex == "M" && female || sex == "F" && male)
                c.Add(N3, $"Diagnosis {dx.Number} ({dx.Code}) is for {(female ? "women" : "men")}, and the patient is {(sex == "M" ? "male" : "female")}.", claim, segment: dx.Segment, element: $"HI{dx.Element:00}-2");
            if (age is { } a)
            {
                if (InRange(code, "P00", "P96") && a > 0)
                    c.Add(N4, $"Diagnosis {dx.Number} ({dx.Code}) is perinatal, for newborns, and the patient is {a}.", claim, segment: dx.Segment, element: $"HI{dx.Element:00}-2");
                if (code.StartsWith('O') && a is < 9 or > 64)
                    c.Add(N4, $"Diagnosis {dx.Number} ({dx.Code}) is a pregnancy code, for ages 9 to 64, and the patient is {a}.", claim, segment: dx.Segment, element: $"HI{dx.Element:00}-2");
            }
        }
    }

    /// <summary>Whether a code's first three characters fall in a category range (N40 to N53, Z33 to Z3A).</summary>
    private static bool InRange(string code, string from, string to) =>
        code.Length >= 3 && string.CompareOrdinal(code[..3], from) >= 0 && string.CompareOrdinal(code[..3], to) <= 0;
}
