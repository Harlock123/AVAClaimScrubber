using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Claims;

/// <summary>
/// Reads an 837's segments into the loops of its implementation guide: the submitter and receiver (1000A, 1000B), and
/// under each HL the billing provider (2000A, its 2010AA and 2010AB), subscriber (2000B, 2010BA member, 2010BB payer)
/// and patient (2000C, 2010CA), each claim (2300, its provider and other-payer loops 2310x, 2320 and 2330x) and its
/// lines (2400, 2410, 2420x, 2430). A name loop (an NM1 and its address, references and demographics) ends at the
/// first segment that isn't one of those, which goes back to the loop round it.
/// </summary>
public static class ClaimReader
{
    public static ClaimBatch Read(string name, X12File file)
    {
        var batch = new ClaimBatch { Name = name, File = file };
        var number = 0;
        foreach (var t in file.Transactions.Where(t => t.Code == "837"))
        {
            var version = (t.Start[3].Length > 0 ? t.Start[3] : t.Group?[8] ?? "").Trim();
            var kind = version.Contains("X223", StringComparison.Ordinal) || t.Body.Any(s => s.Id == "SV2") && !t.Body.Any(s => s.Id == "SV1")
                ? ClaimKind.Institutional : ClaimKind.Professional;
            var root = BuildLoops(t, kind);
            batch.Transactions.Add((t, root, kind));
            var created = t.Body.FirstOrDefault(s => s.Id == "BHT") is { } bht ? Parse.Date(bht[4]) : null;
            foreach (var clm in root.All().Where(l => l.Id == "2300").OrderBy(l => l.Segments[0].Index))
            {
                var hl = clm.Parent;
                var subscriber = hl?.Id == "2000C" ? hl.Parent : hl;
                var claim = new Claim
                {
                    Number = ++number,
                    Kind = kind,
                    Loop = clm,
                    Clm = clm.Segments[0],
                    Transaction = t,
                    Subscriber = subscriber,
                    Patient = hl?.Id == "2000C" ? hl : null,
                    BillingProvider = subscriber?.Parent?.Id == "2000A" ? subscriber.Parent : root.All().FirstOrDefault(l => l.Id == "2000A"),
                    Created = created,
                };
                ReadDiagnoses(claim, file.Delimiters.Component);
                ReadLines(claim, file.Delimiters.Component);
                batch.Claims.Add(claim);
            }
        }
        return batch;
    }

    public static ClaimBatch Load(string path) => Read(Path.GetFileName(path), X12File.Load(path));

    private static readonly HashSet<string> NameLoopSegments = ["N2", "N3", "N4", "REF", "PER", "DMG", "PRV"];

    /// <summary>Loop ids and names for an NM1's qualifier, by the loop it's found in.</summary>
    private static (string Id, string Name)? NameLoop(string inside, string qualifier, ClaimKind kind) => (inside, qualifier) switch
    {
        ("ROOT", "41") => ("1000A", "Submitter"),
        ("ROOT", "40") => ("1000B", "Receiver"),
        ("2000A", "85") => ("2010AA", "Billing provider"),
        ("2000A", "87") => ("2010AB", "Pay-to address"),
        ("2000A", "PE") => ("2010AC", "Pay-to plan"),
        ("2000B", "IL") => ("2010BA", "Subscriber"),
        ("2000B", "PR") => ("2010BB", "Payer"),
        ("2000C", "QC") => ("2010CA", "Patient"),
        ("2300", "DN") => ("2310A", "Referring provider"),
        ("2300", "P3") => ("2310A", "Primary care provider"),
        ("2300", "71") => ("2310A", "Attending provider"),
        ("2300", "72") => ("2310B", "Operating physician"),
        ("2300", "ZZ") => ("2310C", "Other operating physician"),
        ("2300", "82") => (kind == ClaimKind.Professional ? "2310B" : "2310D", "Rendering provider"),
        ("2300", "77") => (kind == ClaimKind.Professional ? "2310C" : "2310E", "Service facility"),
        ("2300", "DQ") => ("2310D", "Supervising provider"),
        ("2300", "PW") => ("2310E", "Ambulance pick-up"),
        ("2300", "45") => ("2310F", "Ambulance drop-off"),
        ("2320", "IL") => ("2330A", "Other subscriber"),
        ("2320", "PR") => ("2330B", "Other payer"),
        ("2320", _) => ("2330C", "Other payer provider"),
        ("2400", "82") => ("2420A", "Rendering provider"),
        ("2400", "QB") => ("2420B", "Purchased service provider"),
        ("2400", "77") => ("2420C", "Service facility"),
        ("2400", "DQ") => ("2420D", "Supervising provider"),
        ("2400", "DK") => ("2420E", "Ordering provider"),
        ("2400", "DN") => ("2420F", "Referring provider"),
        ("2400", "71") => ("2420A", "Attending provider"),
        ("2400", _) => ("2420", "Line provider"),
        (_, _) => null,
    };

    private static Loop BuildLoops(Transaction t, ClaimKind kind)
    {
        var root = new Loop { Id = "ROOT", Name = kind == ClaimKind.Professional ? "837P" : "837I" };
        root.Segments.Add(t.Start);
        var hls = new Dictionary<string, Loop>(StringComparer.Ordinal);
        Loop main = root;          // the loop new segments go to: the root, an HL loop, 2300, 2320 or 2400
        Loop? name = null;         // an open name loop (2010xx, 2310x…), until a segment that isn't one of its own
        Loop? claim = null;
        foreach (var s in t.Body)
        {
            if (name != null && !NameLoopSegments.Contains(s.Id)) name = null;
            switch (s.Id)
            {
                case "HL":
                    {
                        var level = s[3].Trim();
                        var (id, title) = level switch { "20" => ("2000A", "Billing provider"), "22" => ("2000B", "Subscriber"), "23" => ("2000C", "Patient"), _ => ("2000", "Hierarchical level " + level) };
                        var loop = new Loop { Id = id, Name = title };
                        var parent = hls.GetValueOrDefault(s[2].Trim()) ?? root;
                        loop.Parent = parent;
                        parent.Children.Add(loop);
                        hls[s[1].Trim()] = loop;
                        loop.Segments.Add(s);
                        main = loop;
                        claim = null;
                        continue;
                    }
                case "CLM":
                    {
                        var hl = main.Id.StartsWith("2000", StringComparison.Ordinal) ? main : main.Parent ?? root;
                        while (hl.Id is "2300" or "2320" or "2400") hl = hl.Parent!;
                        claim = Add(hl, "2300", "Claim", s);
                        main = claim;
                        continue;
                    }
                case "SBR" when claim != null:
                    main = Add(claim, "2320", "Other subscriber", s);
                    continue;
                case "LX" when claim != null:
                    main = Add(claim, "2400", "Service line", s);
                    continue;
                case "LIN" when main.Id == "2400":
                    main = Add(main, "2410", "Drug", s);
                    continue;
                case "SVD" when claim != null:
                    {
                        var line = main.Id == "2400" ? main : main.Parent?.Id == "2400" ? main.Parent : null;
                        if (line != null)
                        {
                            Add(line, "2430", "Line adjudication", s);
                            continue;
                        }
                        break;
                    }
                case "NM1":
                    {
                        var inside = main.Id == "2410" ? "2400" : main.Id == "2430" ? "2400" : main.Id;
                        if (NameLoop(inside, s[1].Trim(), kind) is var (id, title))
                        {
                            var owner = main.Id is "2410" ? main.Parent! : main;
                            name = Add(owner, id, title, s);
                            continue;
                        }
                        break;
                    }
            }
            (name ?? (main.Id == "2430" ? main.Parent! : main)).Segments.Add(s);
        }
        if (t.Trailer != null) root.Segments.Add(t.Trailer);
        return root;
    }

    private static Loop Add(Loop parent, string id, string title, Segment first)
    {
        var loop = new Loop { Id = id, Name = title, Parent = parent };
        loop.Segments.Add(first);
        parent.Children.Add(loop);
        return loop;
    }

    /// <summary>HI qualifiers for diagnoses (not procedures, occurrences or values).</summary>
    public static readonly HashSet<string> DiagnosisQualifiers = ["ABK", "ABF", "ABJ", "ABN", "APR", "BK", "BF", "BJ", "BN", "PR", "DR"];

    private static void ReadDiagnoses(Claim claim, char component)
    {
        var n = 0;
        foreach (var hi in claim.Loop.FindAll("HI"))
            for (var e = 1; e < hi.Elements.Length; e++)
            {
                var parts = hi[e].Split(component);
                if (parts.Length < 2 || !DiagnosisQualifiers.Contains(parts[0].Trim())) continue;
                claim.Diagnoses.Add(new Diagnosis(++n, parts[0].Trim(), parts[1].Trim(), hi, e));
            }
    }

    private static void ReadLines(Claim claim, char component)
    {
        foreach (var loop in claim.Loop.Children.Where(c => c.Id == "2400"))
        {
            var number = int.TryParse(loop.Segments[0][1].Trim(), out var lx) ? lx : claim.Lines.Count + 1;
            var date = loop.Find("DTP", "472");
            var dateText = date == null ? "" : date[3].Split('-')[0].Trim();
            if (loop.Find("SV1") is { } sv1)
            {
                var proc = sv1[1].Split(component);
                claim.Lines.Add(new ServiceLine
                {
                    Number = number,
                    Loop = loop,
                    Service = sv1,
                    ProcedureQualifier = proc[0].Trim(),
                    Procedure = proc.Length > 1 ? proc[1].Trim() : "",
                    Modifiers = proc.Skip(2).Take(4).Select(m => m.Trim()).Where(m => m.Length > 0).ToList(),
                    Charge = Parse.Amount(sv1[2]),
                    UnitQualifier = sv1[3].Trim(),
                    Units = Parse.Amount(sv1[4]),
                    PlaceOfService = sv1[5].Trim(),
                    Pointers = sv1[7].Split(component).Select(p => int.TryParse(p.Trim(), out var v) ? v : 0).Where(v => v > 0).ToList(),
                    DateText = dateText,
                    DateSegment = date,
                });
            }
            else if (loop.Find("SV2") is { } sv2)
            {
                var proc = sv2[2].Split(component);
                claim.Lines.Add(new ServiceLine
                {
                    Number = number,
                    Loop = loop,
                    Service = sv2,
                    RevenueCode = sv2[1].Trim(),
                    ProcedureQualifier = proc[0].Trim(),
                    Procedure = proc.Length > 1 ? proc[1].Trim() : "",
                    Modifiers = proc.Skip(2).Take(4).Select(m => m.Trim()).Where(m => m.Length > 0).ToList(),
                    Charge = Parse.Amount(sv2[3]),
                    UnitQualifier = sv2[4].Trim(),
                    Units = Parse.Amount(sv2[5]),
                    DateText = dateText,
                    DateSegment = date,
                });
            }
        }
    }
}
