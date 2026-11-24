using System.Globalization;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 2: the claim's structure, as the 837 implementation guides lay it out. The HL hierarchy (numbered in order,
/// each parent there before its children, billing provider over subscriber over patient, HL04 saying whether children
/// follow), the loops a claim must have (the billing provider, the subscriber and the payer, a patient loop only when the
/// patient isn't the subscriber), at least one diagnosis and one line and no more than the guide allows, lines numbered
/// in order, and diagnosis pointers that point at a diagnosis.
/// </summary>
public sealed class StructureChecks : ICheck
{
    private const string Guide = "HIPAA 5010 837 implementation guides (loop structure; the guides are licensed from X12)";

    public static readonly Rule L1 = new("L1", 2, "HL segments are numbered in order", "HL01 counts from 1, one more each time.", Guide, Severity.Reject);
    public static readonly Rule L2 = new("L2", 2, "Each HL's parent is there before it", "HL02 names an HL already sent.", Guide, Severity.Reject);
    public static readonly Rule L3 = new("L3", 2, "Levels nest as the guide says", "A billing provider (HL03 20) has no parent, a subscriber (22) sits under a billing provider, a patient (23) under a subscriber.", Guide, Severity.Reject);
    public static readonly Rule L4 = new("L4", 2, "HL04 says whether children follow", "HL04 is 1 when lower levels follow and 0 when they don't.", Guide, Severity.Reject);
    public static readonly Rule L5 = new("L5", 2, "The billing provider is named", "Each billing provider level (2000A) has its 2010AA NM1*85 with an NPI (NM108 XX).", Guide, Severity.Reject);
    public static readonly Rule L6 = new("L6", 2, "The subscriber and payer are named", "Each subscriber level (2000B) has the subscriber (2010BA NM1*IL) and the payer (2010BB NM1*PR).", Guide, Severity.Reject);
    public static readonly Rule L7 = new("L7", 2, "A patient loop only when the patient isn't the subscriber", "When SBR02 is 18 (the patient is the subscriber) there's no 2000C; otherwise the claim sits under a 2000C patient.", Guide, Severity.Reject);
    public static readonly Rule L8 = new("L8", 2, "The claim has its diagnoses", "A claim has at least one diagnosis (HI), and a professional claim no more than 12.", Guide, Severity.Reject);
    public static readonly Rule L9 = new("L9", 2, "The claim has its lines", "A claim has at least one service line, and a professional claim no more than 50.", Guide, Severity.Reject);
    public static readonly Rule L10 = new("L10", 2, "Lines are numbered in order", "LX01 counts from 1, one more each line.", Guide, Severity.Reject);
    public static readonly Rule L11 = new("L11", 2, "Diagnosis pointers point at a diagnosis", "Each line points at one to four of the claim's diagnoses (SV1-07), by their order in HI.", Guide, Severity.Reject);
    public static readonly Rule L12 = new("L12", 2, "An institutional claim has its statement period", "An 837I claim has DTP*434 with the period the bill covers.", Guide, Severity.Reject);

    public IEnumerable<Rule> Rules => [L1, L2, L3, L4, L5, L6, L7, L8, L9, L10, L11, L12];

    public void Run(ScrubContext c)
    {
        foreach (var (t, root, _) in c.Batch.Transactions) Hierarchy(c, t, root);
        foreach (var claim in c.Batch.Claims) Claim(c, claim);
    }

    private static void Hierarchy(ScrubContext c, X12.Transaction t, Loop root)
    {
        var levels = new Dictionary<string, string>(StringComparer.Ordinal);
        var expected = 1;
        var hls = t.Body.Where(s => s.Id == "HL").ToList();
        foreach (var hl in hls)
        {
            var id = hl[1].Trim();
            var parent = hl[2].Trim();
            var level = hl[3].Trim();
            if (id != expected.ToString(CultureInfo.InvariantCulture))
                c.Add(L1, $"HL01 is {id} where {expected} comes next.", segment: hl, element: "HL01", loop: "HL",
                    fix: new Fix(hl.Index, 1, 0, expected.ToString(CultureInfo.InvariantCulture), $"Number it {expected}"));
            expected++;
            if (parent.Length > 0 && !levels.ContainsKey(parent))
                c.Add(L2, $"HL {id} names HL {parent} as its parent, and there's no HL {parent} before it.", ClaimUnder(c, hl), segment: hl, element: "HL02", loop: "HL");
            var parentLevel = levels.GetValueOrDefault(parent);
            var ok = level switch
            {
                "20" => parent.Length == 0,
                "22" => parentLevel is null or "20",
                "23" => parentLevel is null or "22",
                _ => true,
            };
            if (!ok) c.Add(L3, $"HL {id} is level {level} under a level {parentLevel ?? "none"}.", ClaimUnder(c, hl), segment: hl, element: "HL03", loop: "HL");
            levels[id] = level;
        }
        // HL04: 1 if some later HL names this one as its parent.
        foreach (var hl in hls)
        {
            var has = hls.Any(o => o[2].Trim() == hl[1].Trim());
            var says = hl[4].Trim();
            if (says.Length > 0 && (says == "1") != has)
                c.Add(L4, $"HL {hl[1].Trim()} says {(says == "1" ? "children follow, and none do" : "no children follow, and some do")}.", segment: hl, element: "HL04", loop: "HL",
                    fix: new Fix(hl.Index, 4, 0, has ? "1" : "0", $"Set HL04 to {(has ? "1" : "0")}"));
        }
        foreach (var provider in root.All().Where(l => l.Id == "2000A"))
        {
            var nm1 = provider.Child("2010AA")?.Find("NM1", "85");
            if (nm1 == null) c.Add(L5, "The billing provider level has no 2010AA (NM1*85).", segment: provider.Segments[0], loop: "2000A");
            else if (nm1[8].Trim() != "XX") c.Add(L5, $"The billing provider's NM108 is \"{nm1[8].Trim()}\", not XX (an NPI).", segment: nm1, element: "NM108", loop: "2010AA");
        }
    }

    /// <summary>The first claim under an HL (its subscriber or patient level), so a hierarchy problem shows on the claim it breaks.</summary>
    private static Claim? ClaimUnder(ScrubContext c, X12.Segment hl) =>
        c.Batch.Claims.FirstOrDefault(x => x.Subscriber?.Segments[0] == hl || x.Patient?.Segments[0] == hl);

    private static void Claim(ScrubContext c, Claim claim)
    {
        var sub = claim.Subscriber;
        if (sub != null)
        {
            if (sub.Child("2010BA")?.Find("NM1", "IL") == null) c.Add(L6, "The subscriber level has no subscriber (2010BA NM1*IL).", claim, segment: sub.Segments[0], loop: "2000B");
            if (claim.Payer == null) c.Add(L6, "The subscriber level has no payer (2010BB NM1*PR).", claim, segment: sub.Segments[0], loop: "2000B");
            var sbr = sub.Find("SBR");
            var self = sbr?[2].Trim() == "18";
            if (self && claim.Patient != null) c.Add(L7, "SBR02 says the patient is the subscriber (18), but the claim sits under a patient loop (2000C).", claim, segment: sbr, element: "SBR02", loop: "2000B");
            if (sbr != null && !self && claim.Patient == null) c.Add(L7, "SBR02 says the patient isn't the subscriber, but there's no patient loop (2000C).", claim, segment: sbr, element: "SBR02", loop: "2000B");
        }
        var maxDx = claim.Kind == ClaimKind.Professional ? 12 : 25;
        if (claim.Diagnoses.Count == 0) c.Add(L8, "The claim has no diagnosis (no HI segment with a diagnosis).", claim, segment: claim.Clm, loop: "2300");
        else if (claim.Diagnoses.Count > maxDx) c.Add(L8, $"The claim has {claim.Diagnoses.Count} diagnoses; the guide allows {maxDx}.", claim, segment: claim.Diagnoses[maxDx].Segment, loop: "2300");
        var maxLines = claim.Kind == ClaimKind.Professional ? 50 : 999;
        if (claim.Lines.Count == 0) c.Add(L9, "The claim has no service lines (no LX).", claim, segment: claim.Clm, loop: "2300");
        else if (claim.Lines.Count > maxLines) c.Add(L9, $"The claim has {claim.Lines.Count} lines; the guide allows {maxLines}.", claim, segment: claim.Clm, loop: "2300");
        var n = 0;
        foreach (var line in claim.Lines)
        {
            n++;
            var lx = line.Loop.Segments[0];
            if (lx[1].Trim() != n.ToString(CultureInfo.InvariantCulture))
                c.Add(L10, $"Line {n} is numbered {lx[1].Trim()}.", claim, line, lx, "LX01", new Fix(lx.Index, 1, 0, n.ToString(CultureInfo.InvariantCulture), $"Number it {n}"));
            if (claim.Kind != ClaimKind.Professional || claim.Diagnoses.Count == 0) continue;
            if (line.Pointers.Count == 0)
                c.Add(L11, $"Line {line.Number} points at no diagnosis (SV1-07 is empty).", claim, line, line.Service, "SV1-07");
            else if (line.Pointers.Count > 4)
                c.Add(L11, $"Line {line.Number} points at {line.Pointers.Count} diagnoses; four is the most.", claim, line, line.Service, "SV1-07");
            foreach (var p in line.Pointers.Where(p => p > claim.Diagnoses.Count).Distinct())
                c.Add(L11, $"Line {line.Number} points at diagnosis {p}, and the claim has {claim.Diagnoses.Count}.", claim, line, line.Service, "SV1-07");
        }
        if (claim.Kind == ClaimKind.Institutional && claim.Loop.Find("DTP", "434") == null)
            c.Add(L12, "The institutional claim has no statement period (DTP*434).", claim, segment: claim.Clm, loop: "2300");
    }
}
