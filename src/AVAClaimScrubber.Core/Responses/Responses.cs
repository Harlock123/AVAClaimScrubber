using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Responses;

/// <summary>A 999's error in one segment of an 837 (IK3), with the element errors under it (IK4).</summary>
public sealed record AckError(string SegmentId, int Position, string Loop, string Code, IReadOnlyList<(int Element, string Code)> Elements)
{
    public string Text => $"{SegmentId} (segment {Position} of the transaction{(Loop.Length > 0 ? ", loop " + Loop : "")}): {Ack999Codes.Segment(Code)}" +
                          string.Concat(Elements.Select(e => $"; {SegmentId}{e.Element:00}: {Ack999Codes.Element(e.Code)}"));
}

/// <summary>A 999's answer for one 837 transaction (AK2 to IK5).</summary>
public sealed class Ack
{
    public required string GroupControl { get; init; }
    public required string TransactionControl { get; init; }
    public required string Status { get; init; }
    /// <summary>IK5-02: why the transaction was rejected, when it says.</summary>
    public string Reason { get; init; } = "";
    public List<AckError> Errors { get; } = [];
    public bool Rejected => Status is "R" or "M" or "W" or "X";
    public string StatusText => Ack999Codes.Transaction(Status);
}

/// <summary>A status the 277CA gives (STC01, STC10, STC11): category, status and entity.</summary>
public sealed record StatusCode(string Category, string Code, string Entity)
{
    public bool Rejected => Category is "A3" or "A6" or "A7" or "A8";
    public string Text => $"{Category} {ResponseCodes.Describe(ResponseCodes.StatusCategory, Category)}" + (Code.Length > 0 ? $" / {Code} {ResponseCodes.Describe(ResponseCodes.Status, Code)}" : "");
}

/// <summary>A 277CA's answer for one claim, matched by the patient control number (TRN02, the 837's CLM01).</summary>
public sealed class ClaimStatus
{
    public required string Account { get; init; }
    public List<StatusCode> Statuses { get; } = [];
    public List<(string Procedure, string Date, List<StatusCode> Statuses)> Lines { get; } = [];
    public string PayerClaim { get; set; } = "";
    public bool Rejected => Statuses.Any(s => s.Rejected) || Lines.Any(l => l.Statuses.Any(s => s.Rejected));
}

/// <summary>An 835 adjustment (CAS): group, reason (a CARC), amount and quantity.</summary>
public sealed record Adjustment(string Group, string Reason, decimal Amount, decimal? Quantity)
{
    public string Text => $"{Group}-{Reason} {ResponseCodes.Describe(ResponseCodes.Carc, Reason)}";
}

/// <summary>An 835 service line (SVC) and its adjustments and remarks.</summary>
public sealed class RemitLine
{
    public int Number { get; init; }
    public required string Procedure { get; init; }
    public IReadOnlyList<string> Modifiers { get; init; } = [];
    public decimal Charge { get; init; }
    public decimal Paid { get; init; }
    public decimal? Units { get; init; }
    public decimal? OriginalUnits { get; init; }
    public string Date { get; set; } = "";
    public List<Adjustment> Adjustments { get; } = [];
    public List<string> Remarks { get; } = [];
}

/// <summary>An 835 claim payment (CLP), matched by the patient control number (CLP01, the 837's CLM01).</summary>
public sealed class RemitClaim
{
    public required string Account { get; init; }
    public required string Status { get; init; }
    public decimal Charge { get; init; }
    public decimal Paid { get; init; }
    public decimal PatientShare { get; init; }
    public string PayerClaim { get; init; } = "";
    public List<Adjustment> Adjustments { get; } = [];
    public List<string> Remarks { get; } = [];
    public List<RemitLine> Lines { get; } = [];
    public bool Denied => Status == "4";

    public string StatusText => Status switch
    {
        "1" => "Processed as primary", "2" => "Processed as secondary", "3" => "Processed as tertiary", "4" => "Denied",
        "19" => "Processed as primary, forwarded", "20" => "Processed as secondary, forwarded", "21" => "Processed as tertiary, forwarded",
        "22" => "Reversal of previous payment", "23" => "Not our claim, forwarded", "25" => "Predetermination pricing only", _ => "Status " + Status,
    };
}

/// <summary>A response file: a 999 (acknowledgment), 277CA (claim acknowledgment) or 835 (remittance).</summary>
public sealed class ResponseFile
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required X12File File { get; init; }
    public List<Ack> Acks { get; } = [];
    public List<ClaimStatus> Statuses { get; } = [];
    public List<RemitClaim> Remits { get; } = [];
    public string Payer { get; set; } = "";
    public DateOnly? Date { get; set; }

    public string KindName => Kind switch { "999" => "999 acknowledgment", "277" => "277CA claim acknowledgment", "835" => "835 remittance", _ => Kind };

    public string Summary => Kind switch
    {
        "999" => $"{Acks.Count} transaction{(Acks.Count == 1 ? "" : "s")} acknowledged, {Acks.Count(a => a.Rejected)} rejected",
        "277" => $"{Statuses.Count} claim{(Statuses.Count == 1 ? "" : "s")}, {Statuses.Count(s => s.Rejected)} rejected",
        _ => $"{Remits.Count} claim{(Remits.Count == 1 ? "" : "s")}, {Remits.Count(r => r.Denied)} denied, {Remits.Sum(r => r.Lines.Count(l => l.Paid == 0 && l.Adjustments.Any(a => a.Group != "PR")))} lines unpaid, {Remits.Sum(r => r.Paid):0.00} paid",
    };
}

/// <summary>Reads 999, 277CA and 835 files.</summary>
public static class ResponseReader
{
    public static readonly string[] Kinds = ["999", "277", "835"];

    public static ResponseFile? Load(string path) => Read(Path.GetFileName(path), X12File.Load(path));

    /// <summary>The file as a response, or null when its transactions aren't 999, 277 or 835.</summary>
    public static ResponseFile? Read(string name, X12File file)
    {
        if (file.Code is not { } code || !Kinds.Contains(code)) return null;
        var r = new ResponseFile { Name = name, Kind = code, File = file };
        foreach (var t in file.Transactions.Where(t => t.Code == code))
        {
            var body = t.Body.ToList();
            switch (code)
            {
                case "999": Read999(r, body); break;
                case "277": Read277(r, body, file.Delimiters.Component); break;
                default: Read835(r, body, file.Delimiters.Component); break;
            }
        }
        return r;
    }

    private static void Read999(ResponseFile r, List<Segment> body)
    {
        var group = "";
        Ack? ack = null;
        AckError? error = null;
        foreach (var s in body)
        {
            switch (s.Id)
            {
                case "AK1": group = s[2].Trim(); break;
                case "AK2":
                    ack = null;
                    error = null;
                    r.Acks.Add(ack = new Ack { GroupControl = group, TransactionControl = s[2].Trim(), Status = "" });
                    break;
                case "IK3" when ack != null:
                    error = new AckError(s[1].Trim(), int.TryParse(s[2].Trim(), out var p) ? p : 0, s[3].Trim(), s[4].Trim(), []);
                    ack.Errors.Add(error);
                    break;
                case "IK4" when ack != null && error != null:
                    // IK4-04 is the bad value as sent: it can be a name or an ID, so it's never kept.
                    var position = int.TryParse(Parse.Component(s, 1, 1).Trim(), out var e) ? e : 0;
                    var replaced = error with { Elements = [.. error.Elements, (position, s[3].Trim())] };
                    ack.Errors[^1] = replaced;
                    error = replaced;
                    break;
                case "IK5" when ack != null:
                    var i = r.Acks.IndexOf(ack);
                    var done = new Ack { GroupControl = ack.GroupControl, TransactionControl = ack.TransactionControl, Status = s[1].Trim(), Reason = s[2].Trim() };
                    done.Errors.AddRange(ack.Errors);
                    r.Acks[i] = ack = done;
                    break;
            }
        }
    }

    private static StatusCode? Status(Segment s, int element, char sep)
    {
        var parts = s[element].Split(sep);
        return parts[0].Trim().Length == 0 ? null : new StatusCode(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "", parts.Length > 2 ? parts[2].Trim() : "");
    }

    private static void Read277(ResponseFile r, List<Segment> body, char sep)
    {
        var level = "";
        ClaimStatus? claim = null;
        (string Procedure, string Date, List<StatusCode> Statuses)? line = null;
        foreach (var s in body)
        {
            switch (s.Id)
            {
                case "HL":
                    level = s[3].Trim();
                    claim = null;
                    line = null;
                    break;
                case "NM1" when s[1].Trim() == "PR" && r.Payer.Length == 0: r.Payer = s[3].Trim(); break;
                case "BHT": r.Date = Parse.Date(s[4]); break;
                case "TRN" when level == "PT" && s[1].Trim() == "2":
                    r.Statuses.Add(claim = new ClaimStatus { Account = s[2].Trim() });
                    break;
                case "REF" when claim != null && s[1].Trim() == "1K": claim.PayerClaim = s[2].Trim(); break;
                case "SVC" when claim != null:
                    var proc = s[1].Split(sep);
                    line = (proc.Length > 1 ? proc[1].Trim() : "", "", []);
                    claim.Lines.Add(line.Value);
                    break;
                case "DTP" when line != null && s[1].Trim() == "472":
                    var i = claim!.Lines.Count - 1;
                    claim.Lines[i] = line.Value with { Date = s[3].Trim().Split('-')[0] };
                    line = claim.Lines[i];
                    break;
                case "STC" when claim != null:
                    var into = line?.Statuses ?? claim.Statuses;
                    foreach (var e in new[] { 1, 10, 11 })
                        if (Status(s, e, sep) is { } st) into.Add(st);
                    break;
            }
        }
    }

    private static void Read835(ResponseFile r, List<Segment> body, char sep)
    {
        RemitClaim? claim = null;
        RemitLine? line = null;
        var inPayer = false;
        foreach (var s in body)
        {
            switch (s.Id)
            {
                case "N1":
                    inPayer = s[1].Trim() == "PR";
                    if (inPayer) r.Payer = s[2].Trim();
                    break;
                case "DTM" when claim == null && s[1].Trim() == "405": r.Date = Parse.Date(s[2]); break;
                case "CLP":
                    line = null;
                    r.Remits.Add(claim = new RemitClaim
                    {
                        Account = s[1].Trim(), Status = s[2].Trim(), Charge = Parse.Amount(s[3]) ?? 0, Paid = Parse.Amount(s[4]) ?? 0,
                        PatientShare = Parse.Amount(s[5]) ?? 0, PayerClaim = s[7].Trim(),
                    });
                    break;
                case "MOA" when claim != null:
                    for (var i = 3; i <= 7; i++) if (s[i].Trim().Length > 0) claim.Remarks.Add(s[i].Trim());
                    break;
                case "SVC" when claim != null:
                    var proc = s[1].Split(sep);
                    claim.Lines.Add(line = new RemitLine
                    {
                        Number = claim.Lines.Count + 1, Procedure = proc.Length > 1 ? proc[1].Trim() : "", Modifiers = [.. proc.Skip(2).Select(m => m.Trim()).Where(m => m.Length > 0)],
                        Charge = Parse.Amount(s[2]) ?? 0, Paid = Parse.Amount(s[3]) ?? 0, Units = Parse.Amount(s[5]), OriginalUnits = Parse.Amount(s[7]),
                    });
                    break;
                case "DTM" when line != null && s[1].Trim() is "472" or "150": line.Date = s[2].Trim(); break;
                case "CAS" when claim != null:
                    var into = line?.Adjustments ?? claim.Adjustments;
                    for (var i = 2; i + 1 <= 19; i += 3)
                    {
                        if (s[i].Trim().Length == 0) break;
                        into.Add(new Adjustment(s[1].Trim(), s[i].Trim(), Parse.Amount(s[i + 1]) ?? 0, Parse.Amount(s[i + 2])));
                    }
                    break;
                case "LQ" when line != null && s[1].Trim() == "HE": line.Remarks.Add(s[2].Trim()); break;
            }
        }
    }
}

/// <summary>The 999's own codes (ASC X12 999, 005010X231A1): IK3-04, IK4-03 and IK5-01.</summary>
public static class Ack999Codes
{
    public static string Segment(string code) => code switch
    {
        "1" => "Unrecognized segment ID", "2" => "Unexpected segment", "3" => "Required segment missing", "4" => "Loop occurs over maximum times",
        "5" => "Segment exceeds maximum use", "6" => "Segment not in defined transaction set", "7" => "Segment not in proper sequence",
        "8" => "Segment has data element errors", "I4" => "Implementation \"not used\" segment present", "I6" => "Implementation dependent segment missing",
        "I7" => "Implementation loop occurs under minimum times", "I8" => "Implementation segment below minimum use",
        "I9" => "Implementation dependent \"not used\" segment present", _ => "error " + code,
    };

    public static string Element(string code) => code switch
    {
        "1" => "Required data element missing", "2" => "Conditional required data element missing", "3" => "Too many data elements",
        "4" => "Data element too short", "5" => "Data element too long", "6" => "Invalid character in data element", "7" => "Invalid code value",
        "8" => "Invalid date", "9" => "Invalid time", "10" => "Exclusion condition violated", "12" => "Too many repetitions", "13" => "Too many components",
        "I6" => "Code value not used in implementation", "I9" => "Implementation dependent data element missing",
        "I10" => "Implementation \"not used\" data element present", "I11" => "Implementation too few repetitions",
        "I12" => "Implementation pattern match failure", "I13" => "Implementation dependent \"not used\" data element present", _ => "error " + code,
    };

    public static string TransactionError(string code) => code switch
    {
        "1" => "Transaction set not supported", "2" => "Transaction set trailer missing", "3" => "Transaction set control number in header and trailer do not match",
        "4" => "Number of included segments does not match actual count", "5" => "One or more segments in error", "6" => "Missing or invalid transaction set identifier",
        "7" => "Missing or invalid transaction set control number", "23" => "Transaction set control number not unique within the functional group",
        "I5" => "Implementation one or more segments in error", "I6" => "Implementation convention not supported", _ => "error " + code,
    };

    public static string Transaction(string code) => code switch
    {
        "A" => "Accepted", "E" => "Accepted, with errors noted", "R" => "Rejected", "M" => "Rejected: message authentication code failed",
        "W" => "Rejected: assurance failed validity tests", "X" => "Rejected: content after decryption could not be analyzed", "" => "No IK5", _ => code,
    };
}
