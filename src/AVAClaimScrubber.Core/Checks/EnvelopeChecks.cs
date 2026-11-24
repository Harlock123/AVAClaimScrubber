using System.Globalization;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 1: the envelope and syntax, as a clearinghouse checks them before anything else (a 999 rejects the file or
/// transaction): control numbers that match, counts that add up, the 5010 implementation guide's version, the
/// elements every claim and line must have, real dates, amounts as numbers, and diagnosis codes without the decimal
/// point (X12 sends ICD-10 codes without it).
/// </summary>
public sealed class EnvelopeChecks : ICheck
{
    private const string Base = "X12 005010 (base standard)";

    public static readonly Rule E1 = new("E1", 1, "The interchange control numbers match", "ISA13 and IEA02 must be the same number, or the interchange is rejected.", Base, Severity.Reject);
    public static readonly Rule E2 = new("E2", 1, "The interchange counts its groups", "IEA01 must be the number of GS groups in the interchange.", Base, Severity.Reject);
    public static readonly Rule E3 = new("E3", 1, "The group control numbers match", "GS06 and GE02 must be the same number.", Base, Severity.Reject);
    public static readonly Rule E4 = new("E4", 1, "The group counts its transactions", "GE01 must be the number of ST transactions in the group.", Base, Severity.Reject);
    public static readonly Rule E5 = new("E5", 1, "The transaction control numbers match", "ST02 and SE02 must be the same.", Base, Severity.Reject);
    public static readonly Rule E6 = new("E6", 1, "The transaction counts its segments", "SE01 must be the number of segments from ST to SE, both included.", Base, Severity.Reject);
    public static readonly Rule E7 = new("E7", 1, "It's a 5010 837", "GS08 and ST03 name the implementation guide: 005010X222A1 (professional) or 005010X223A2 (institutional).", "HIPAA 5010 implementation guides (version identifiers)", Severity.Reject);
    public static readonly Rule S1 = new("S1", 1, "Required elements are there", "A claim needs its account number, total and facility code; a line its procedure or revenue code, charge and units; the billing provider its NPI.", Base, Severity.Reject);
    public static readonly Rule S2 = new("S2", 1, "Dates are real dates", "Dates are CCYYMMDD (D8) or a CCYYMMDD-CCYYMMDD range (RD8), and must be days that exist.", Base, Severity.Reject);
    public static readonly Rule S3 = new("S3", 1, "Amounts are numbers", "Charges and totals are decimal numbers with at most two places.", Base, Severity.Reject);
    public static readonly Rule S4 = new("S4", 1, "Diagnosis codes have no decimal point", "X12 carries ICD-10 codes without the dot (E11.9 is sent as E119).", "X12 005010 (ICD-10 code values)", Severity.Reject);

    public IEnumerable<Rule> Rules => [E1, E2, E3, E4, E5, E6, E7, S1, S2, S3, S4];

    public void Run(ScrubContext c)
    {
        Envelope(c);
        foreach (var claim in c.Batch.Claims) Syntax(c, claim);
    }

    private static void Envelope(ScrubContext c)
    {
        var segments = c.Batch.File.Segments;
        Segment? isa = null, gs = null, st = null;
        int groups = 0, transactions = 0, count = 0;
        foreach (var s in segments)
        {
            if (st != null) count++;
            switch (s.Id)
            {
                case "ISA":
                    isa = s;
                    groups = 0;
                    break;
                case "GS":
                    gs = s;
                    groups++;
                    transactions = 0;
                    if (s[8].Trim() is not ("005010X222A1" or "005010X223A2" or "005010X224A2") && segments.Any(x => x.Id == "ST" && x[1].Trim() == "837"))
                        c.Add(E7, $"GS08 is \"{s[8].Trim()}\", not a 5010 837 guide's version.", segment: s, element: "GS08", loop: "GS");
                    break;
                case "ST":
                    st = s;
                    count = 1;
                    transactions++;
                    break;
                case "SE" when st != null:
                    if (s[1].Trim() != count.ToString(CultureInfo.InvariantCulture))
                        c.Add(E6, $"SE01 says {s[1].Trim()} segments; the transaction has {count}.", segment: s, element: "SE01", loop: "SE", fix: new Fix(s.Index, 1, 0, count.ToString(CultureInfo.InvariantCulture), $"Set SE01 to {count}"));
                    if (s[2].Trim() != st[2].Trim())
                        c.Add(E5, "SE02 isn't the same as ST02.", segment: s, element: "SE02", loop: "SE", fix: new Fix(s.Index, 2, 0, st[2].Trim(), "Set SE02 to ST02"));
                    st = null;
                    break;
                case "GE" when gs != null:
                    if (s[1].Trim() != transactions.ToString(CultureInfo.InvariantCulture))
                        c.Add(E4, $"GE01 says {s[1].Trim()} transactions; the group has {transactions}.", segment: s, element: "GE01", loop: "GE", fix: new Fix(s.Index, 1, 0, transactions.ToString(CultureInfo.InvariantCulture), $"Set GE01 to {transactions}"));
                    if (s[2].Trim() != gs[6].Trim())
                        c.Add(E3, "GE02 isn't the same as GS06.", segment: s, element: "GE02", loop: "GE", fix: new Fix(s.Index, 2, 0, gs[6].Trim(), "Set GE02 to GS06"));
                    gs = null;
                    break;
                case "IEA" when isa != null:
                    if (s[1].Trim() != groups.ToString(CultureInfo.InvariantCulture))
                        c.Add(E2, $"IEA01 says {s[1].Trim()} groups; the interchange has {groups}.", segment: s, element: "IEA01", loop: "IEA", fix: new Fix(s.Index, 1, 0, groups.ToString(CultureInfo.InvariantCulture), $"Set IEA01 to {groups}"));
                    if (s[2].Trim() != isa[13].Trim())
                        c.Add(E1, "IEA02 isn't the same as ISA13.", segment: s, element: "IEA02", loop: "IEA", fix: new Fix(s.Index, 2, 0, isa[13].Trim(), "Set IEA02 to ISA13"));
                    isa = null;
                    break;
            }
        }
    }

    private static void Syntax(ScrubContext c, Claim claim)
    {
        void Need(Segment? s, int element, string name, string what, ServiceLine? line = null)
        {
            if (s != null && s[element].Trim().Length > 0) return;
            c.Add(S1, $"{name} ({what}) is empty.", claim, line, s ?? claim.Clm, name);
        }
        Need(claim.Clm, 1, "CLM01", "the patient account number");
        Need(claim.Clm, 2, "CLM02", "the claim's total charge");
        Need(claim.Clm, 5, "CLM05", claim.Kind == ClaimKind.Professional ? "place of service and frequency" : "bill type");
        if (claim.BillingNpi is { } billing) Need(billing, 9, "NM109", "the billing provider's NPI");
        if (claim.Total == null && claim.Clm[2].Trim().Length > 0) c.Add(S3, "CLM02 isn't a number.", claim, segment: claim.Clm, element: "CLM02");
        else if (Decimals(claim.Clm[2]) > 2) c.Add(S3, "CLM02 has more than two decimal places.", claim, segment: claim.Clm, element: "CLM02");

        foreach (var line in claim.Lines)
        {
            var sv = line.Service;
            var p = sv.Id;
            if (claim.Kind == ClaimKind.Professional) Need(sv, 1, $"{p}-01", "the procedure code", line);
            else Need(sv, 1, $"{p}-01", "the revenue code", line);
            Need(sv, line.ChargeElement, $"{p}-{line.ChargeElement:00}", "the line charge", line);
            Need(sv, line.UnitsElement, $"{p}-{line.UnitsElement:00}", "the units", line);
            if (sv[line.ChargeElement].Trim().Length > 0 && (line.Charge == null || Decimals(sv[line.ChargeElement]) > 2))
                c.Add(S3, $"{p}-{line.ChargeElement:00} isn't an amount with at most two decimal places.", claim, line, sv, $"{p}-{line.ChargeElement:00}");
            if (line.DateSegment is { } d) Date(c, claim, line, d);
        }
        foreach (var d in claim.Loop.FindAll("DTP")) Date(c, claim, null, d);
        if (claim.Demographics is { } dmg && dmg[1].Trim() == "D8" && Parse.Date(dmg[2]) == null)
            c.Add(S2, "DMG02 (the birth date) isn't a real CCYYMMDD date.", claim, segment: dmg, element: "DMG02", loop: claim.Patient != null ? "2010CA" : "2010BA");

        foreach (var dx in claim.Diagnoses.Where(d => d.Code.Contains('.')))
        {
            var fixedCode = dx.Code.Replace(".", "", StringComparison.Ordinal);
            c.Add(S4, $"Diagnosis {dx.Number} ({dx.Code}) has a decimal point; X12 sends it as {fixedCode}.", claim, segment: dx.Segment, element: $"HI{dx.Element:00}-2",
                fix: new Fix(dx.Segment.Index, dx.Element, 2, fixedCode, $"Send {fixedCode}"));
        }
    }

    private static void Date(ScrubContext c, Claim claim, ServiceLine? line, Segment d)
    {
        var format = d[2].Trim();
        var value = d[3].Trim();
        var ok = format switch
        {
            "D8" => Parse.Date(value) != null,
            "RD8" => value.Split('-') is [var a, var b] && Parse.Date(a) is { } from && Parse.Date(b) is { } to && from <= to,
            _ => true,
        };
        if (!ok) c.Add(S2, $"DTP03 (DTP*{d[1].Trim()}) isn't a real {(format == "RD8" ? "CCYYMMDD-CCYYMMDD range" : "CCYYMMDD date")}.", claim, line, d, "DTP03");
    }

    private static int Decimals(string amount)
    {
        var dot = amount.Trim().IndexOf('.');
        return dot < 0 ? 0 : amount.Trim().Length - dot - 1;
    }
}
