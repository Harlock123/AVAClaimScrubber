using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.CodeSets;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 4: the codes on the claim, against the code sets. ICD-10-CM codes must exist and be billable (a header such as
/// E11 isn't: a code under it is); HCPCS Level II codes must exist and be in effect on the service date; CPT codes have
/// CPT's shape (and exist, if a licensed CPT file is loaded); modifiers must exist; the place of service must be one of
/// CMS's; every NPI must pass its check digit; a taxonomy must be one of NUCC's. A set that isn't loaded is said once,
/// as information, and its codes aren't judged.
/// </summary>
public sealed class CodeSetChecks(CodeSets.CodeSets? sets) : ICheck
{
    public static readonly Rule C1 = new("C1", 4, "Diagnosis codes exist", "Each diagnosis is a code in the ICD-10-CM code set loaded.", "CMS ICD-10-CM code set (the year loaded)", Severity.Reject);
    public static readonly Rule C2 = new("C2", 4, "Diagnosis codes are billable", "A header code (a category such as E11) has codes under it; only a full code can be billed.", "CMS ICD-10-CM code set (header flag)", Severity.Reject);
    public static readonly Rule C3 = new("C3", 4, "HCPCS codes exist and are in effect", "A HCPCS Level II code must be in the code set, added before the service and not ended by it.", "CMS HCPCS Level II quarterly file", Severity.Deny);
    public static readonly Rule C4 = new("C4", 4, "CPT codes look like CPT codes", "CPT codes are five digits, or four digits and F, T or U; with a licensed CPT file loaded, they must be in it.", "AMA CPT (licensed): shape only unless a CPT file is loaded", Severity.Reject);
    public static readonly Rule C5 = new("C5", 4, "Modifiers exist", "A letter modifier must be a HCPCS Level II modifier; a two-digit one is CPT's (shape only, unless a CPT file is loaded).", "CMS HCPCS Level II quarterly file (modifiers)", Severity.Deny);
    public static readonly Rule C6 = new("C6", 4, "The place of service is a real one", "CLM05-1 (and SV1-05 on a line) is one of CMS's place of service codes.", PlaceOfService.Source, Severity.Reject);
    public static readonly Rule C7 = new("C7", 4, "NPIs pass their check digit", "An NPI is ten digits whose last is a Luhn check digit over the prefix 80840; a mistyped NPI fails it.", "CMS NPI standard (45 CFR 162.406; the check digit)", Severity.Reject);
    public static readonly Rule C8 = new("C8", 4, "Taxonomy codes exist", "A provider taxonomy (PRV03) is one of NUCC's codes: ten characters ending in X.", "NUCC Health Care Provider Taxonomy", Severity.Reject);
    public static readonly Rule C9 = new("C9", 4, "The code sets are loaded", "Without a code set, its codes can't be checked: Update in the Code Sets tab downloads them.", "AVA Claim Scrubber", Severity.Info);

    public IEnumerable<Rule> Rules => [C1, C2, C3, C4, C5, C6, C7, C8, C9];

    public void Run(ScrubContext c)
    {
        if (c.Batch.Claims.Count == 0) return;
        var missing = new List<string>();
        if (sets?.Icd == null) missing.Add("ICD-10-CM");
        if (sets?.Hcpcs == null) missing.Add("HCPCS Level II");
        if (sets?.Taxonomy == null) missing.Add("the NUCC taxonomy");
        if (missing.Count > 0) c.Add(C9, $"{string.Join(", ", missing)} {(missing.Count == 1 ? "isn't" : "aren't")} loaded, so {(missing.Count == 1 ? "its" : "their")} codes weren't checked.", loop: "");
        foreach (var claim in c.Batch.Claims) Claim(c, claim);
    }

    private void Claim(ScrubContext c, Claim claim)
    {
        if (sets?.Icd is { } icd)
            foreach (var dx in claim.Diagnoses.Where(d => !d.Code.Contains('.')))
            {
                if (!icd.TryGetValue(dx.Code, out var code))
                    c.Add(C1, $"Diagnosis {dx.Number} ({dx.Code}) isn't an ICD-10-CM code.", claim, segment: dx.Segment, element: $"HI{dx.Element:00}-2");
                else if (!code.Billable)
                {
                    var under = icd.Values.Where(x => x.Billable && x.Code.StartsWith(dx.Code, StringComparison.OrdinalIgnoreCase)).Select(x => x.Code).Order(StringComparer.Ordinal).Take(4).ToList();
                    c.Add(C2, $"Diagnosis {dx.Number} ({dx.Code}, {code.Title}) is a header code, not billable" + (under.Count > 0 ? $"; billable codes under it include {string.Join(", ", under)}." : "."),
                        claim, segment: dx.Segment, element: $"HI{dx.Element:00}-2");
                }
            }

        foreach (var line in claim.Lines)
        {
            var sv = line.Service;
            var where = sv.Id == "SV2" ? "SV2-02" : "SV1-01";
            var date = Parse.Date(line.DateText);
            if (line.ProcedureQualifier == "HC" && line.Procedure.Length > 0)
            {
                if (char.IsLetter(line.Procedure[0]))
                {
                    if (sets?.Hcpcs is { } hcpcs)
                    {
                        if (!hcpcs.TryGetValue(line.Procedure, out var h))
                            c.Add(C3, $"Line {line.Number}'s procedure {line.Procedure} isn't a HCPCS Level II code.", claim, line, sv, where);
                        else if (date is { } d && h.Terminated is { } end && end < d)
                            c.Add(C3, $"Line {line.Number}'s procedure {line.Procedure} ({h.Title}) ended {d.DayNumber - end.DayNumber} days before the service.", claim, line, sv, where);
                        else if (date is { } d2 && h.Added is { } start && start > d2)
                            c.Add(C3, $"Line {line.Number}'s procedure {line.Procedure} ({h.Title}) wasn't added until {start.DayNumber - d2.DayNumber} days after the service.", claim, line, sv, where);
                    }
                }
                else if (!IsCptShape(line.Procedure))
                    c.Add(C4, $"Line {line.Number}'s procedure \"{line.Procedure}\" isn't the shape of a CPT code (five digits, or four and F, T or U).", claim, line, sv, where);
                else if (sets?.Cpt is { } cpt && !cpt.ContainsKey(line.Procedure))
                    c.Add(C4, $"Line {line.Number}'s procedure {line.Procedure} isn't in the CPT file loaded.", claim, line, sv, where);
            }
            foreach (var m in line.Modifiers)
            {
                // A two-digit modifier is CPT's (shape only); a letter one must be in the HCPCS list, when it's loaded.
                var ok = m.Length == 2 && (m.All(char.IsDigit) || (sets?.HcpcsModifiers?.Contains(m) ?? true));
                if (!ok) c.Add(C5, $"Line {line.Number}'s modifier {m} isn't a HCPCS Level II modifier.", claim, line, sv, where);
            }
            if (line.PlaceOfService.Length > 0 && !PlaceOfService.Codes.ContainsKey(line.PlaceOfService))
                c.Add(C6, $"Line {line.Number}'s place of service {line.PlaceOfService} isn't one of CMS's codes.", claim, line, sv, "SV1-05");
        }
        if (claim.Kind == ClaimKind.Professional && claim.Facility.Length > 0 && !PlaceOfService.Codes.ContainsKey(claim.Facility))
            c.Add(C6, $"The claim's place of service {claim.Facility} isn't one of CMS's codes" + (int.TryParse(claim.Facility, out _) ? " (it's an unassigned number)." : "."), claim, segment: claim.Clm, element: "CLM05-1");

        // Every NPI on the claim and round it (the billing provider, the claim's and lines' providers).
        var named = new List<(Segment Nm1, string Loop)>();
        if (claim.BillingProvider?.Child("2010AA")?.Find("NM1") is { } billing) named.Add((billing, "2010AA"));
        named.AddRange(claim.Loop.All().Where(l => l.Id.StartsWith("231", StringComparison.Ordinal) || l.Id.StartsWith("242", StringComparison.Ordinal)).Select(l => (l.Segments[0], l.Id)));
        foreach (var (nm1, loop) in named.Where(n => n.Nm1[8].Trim() == "XX"))
        {
            var npi = nm1[9].Trim();
            if (!IsNpi(npi)) c.Add(C7, $"The {Role(nm1[1].Trim())}'s NPI ({npi}) fails its check digit.", claim, segment: nm1, element: "NM109", loop: loop);
        }
        foreach (var l in new[] { claim.BillingProvider }.Concat(claim.Loop.All()).Where(l => l != null))
            foreach (var prv in l!.FindAll("PRV").Where(p => p[2].Trim() == "PXC"))
            {
                var code = prv[3].Trim();
                if (code.Length != 10 || !code.EndsWith('X')) c.Add(C8, $"Taxonomy {code} isn't the shape of a taxonomy code (ten characters ending in X).", claim, segment: prv, element: "PRV03", loop: l.Id);
                else if (sets?.Taxonomy is { } tax && !tax.ContainsKey(code)) c.Add(C8, $"Taxonomy {code} isn't in NUCC's code set.", claim, segment: prv, element: "PRV03", loop: l.Id);
            }
    }

    private static string Role(string qualifier) => qualifier switch
    {
        "85" => "billing provider", "82" => "rendering provider", "77" => "service facility", "DN" => "referring provider",
        "DQ" => "supervising provider", "71" => "attending provider", "72" => "operating physician", "DK" => "ordering provider", _ => "provider",
    };

    public static bool IsCptShape(string code) => code.Length == 5 && char.IsDigit(code[0]) && char.IsDigit(code[1]) && char.IsDigit(code[2]) && char.IsDigit(code[3]) && (char.IsDigit(code[4]) || code[4] is 'F' or 'T' or 'U');

    /// <summary>An NPI's check digit: Luhn over "80840" and its first nine digits.</summary>
    public static bool IsNpi(string npi)
    {
        if (npi.Length != 10 || !npi.All(char.IsDigit)) return false;
        var digits = "80840" + npi[..9];
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return (10 - sum % 10) % 10 == npi[9] - '0';
    }
}
