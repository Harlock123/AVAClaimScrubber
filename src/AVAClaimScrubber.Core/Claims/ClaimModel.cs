using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Claims;

/// <summary>Professional (837P, 005010X222A1) or institutional (837I, 005010X223A2).</summary>
public enum ClaimKind
{
    Professional,
    Institutional,
}

/// <summary>
/// A loop of an 837: its id from the implementation guide (2000A, 2010AA, 2300, 2400…), what it is, its segments and
/// the loops inside it. The tree a claim is shown as, with each segment where the guide puts it.
/// </summary>
public sealed class Loop
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<Segment> Segments { get; } = [];
    public List<Loop> Children { get; } = [];
    public Loop? Parent { get; set; }

    /// <summary>The first segment with this id (and, if given, this first element), in this loop only.</summary>
    public Segment? Find(string id, string? first = null) => Segments.FirstOrDefault(s => s.Id == id && (first == null || s[1] == first));

    public IEnumerable<Segment> FindAll(string id, string? first = null) => Segments.Where(s => s.Id == id && (first == null || s[1] == first));

    /// <summary>The nearest loop inside this one (at any depth) with this id.</summary>
    public Loop? Child(string id) => Children.FirstOrDefault(c => c.Id == id) ?? Children.Select(c => c.Child(id)).FirstOrDefault(c => c != null);

    public IEnumerable<Loop> All()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var l in c.All()) yield return l;
    }

    public override string ToString() => $"{Id} {Name}";
}

/// <summary>A diagnosis on a claim: its qualifier (ABK principal, ABF other, ABJ admitting…) and code, as sent (no decimal point in X12).</summary>
public sealed record Diagnosis(int Number, string Qualifier, string Code, Segment Segment, int Element);

/// <summary>A service line (2400): its procedure or revenue code, modifiers, charge, units, date and diagnosis pointers.</summary>
public sealed class ServiceLine
{
    public required int Number { get; init; }
    public required Loop Loop { get; init; }
    /// <summary>SV1 (professional) or SV2 (institutional).</summary>
    public required Segment Service { get; init; }
    public string ProcedureQualifier { get; init; } = "";
    public string Procedure { get; init; } = "";
    public IReadOnlyList<string> Modifiers { get; init; } = [];
    public string RevenueCode { get; init; } = "";
    public decimal? Charge { get; init; }
    public string UnitQualifier { get; init; } = "";
    public decimal? Units { get; init; }
    public string PlaceOfService { get; init; } = "";
    public IReadOnlyList<int> Pointers { get; init; } = [];
    public string DateText { get; init; } = "";
    public Segment? DateSegment { get; init; }
    /// <summary>The element of <see cref="Service"/> holding the charge (SV1-02, SV2-03).</summary>
    public int ChargeElement => Service.Id == "SV2" ? 3 : 2;
    public int UnitsElement => Service.Id == "SV2" ? 5 : 4;
}

/// <summary>
/// One claim (a 2300 loop) with what it needs from the loops round it: the billing provider (2000A/2010AA), the
/// subscriber and payer (2000B/2010BA/2010BB) and the patient (2000C, when not the subscriber). Values are as the
/// file has them; checks read them, and findings point back at their segments.
/// </summary>
public sealed class Claim
{
    public required int Number { get; init; }
    public required ClaimKind Kind { get; init; }
    public required Loop Loop { get; init; }
    public required Segment Clm { get; init; }
    public required Transaction Transaction { get; init; }
    public Loop? BillingProvider { get; init; }
    public Loop? Subscriber { get; init; }
    public Loop? Patient { get; init; }

    /// <summary>CLM01: the provider's patient account number, which the 277CA and 835 send back.</summary>
    public string Account => Clm[1].Trim();
    public decimal? Total => Parse.Amount(Clm[2]);
    /// <summary>CLM05-1: place of service (837P) or the first two digits of the bill type (837I).</summary>
    public string Facility => Parse.Component(Clm, 5, 1);
    /// <summary>CLM05-3: 1 original, 7 replacement, 8 void.</summary>
    public string Frequency => Parse.Component(Clm, 5, 3);
    public List<Diagnosis> Diagnoses { get; } = [];
    public List<ServiceLine> Lines { get; } = [];

    public Segment? BillingNpi => BillingProvider?.Child("2010AA")?.Find("NM1", "85");
    public Segment? Payer => Subscriber?.Child("2010BB")?.Find("NM1", "PR");
    public Segment? Member => Subscriber?.Child("2010BA")?.Find("NM1", "IL");

    /// <summary>The patient's DMG (birth date, sex): the 2010CA's, else the subscriber's.</summary>
    public Segment? Demographics => Patient?.Child("2010CA")?.Find("DMG") ?? Subscriber?.Child("2010BA")?.Find("DMG");

    /// <summary>The batch's creation date (BHT04): the day the claim is checked as of, for "future" and timely filing.</summary>
    public DateOnly? Created { get; init; }

    public override string ToString() => $"claim {Number} ({Account})";
}

/// <summary>A file's 837 claims, with the transactions they're in and anything that couldn't be read as a claim.</summary>
public sealed class ClaimBatch
{
    public required string Name { get; init; }
    public required X12File File { get; init; }
    public List<Claim> Claims { get; } = [];
    /// <summary>Each 837 transaction's loop tree (the header loops, and 2000A down).</summary>
    public List<(Transaction Transaction, Loop Root, ClaimKind Kind)> Transactions { get; } = [];
}

/// <summary>Reading values out of elements: amounts, dates, composites.</summary>
public static class Parse
{
    public static decimal? Amount(string text) =>
        decimal.TryParse(text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>A CCYYMMDD date, or null if it isn't one.</summary>
    public static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Component <paramref name="part"/> (from 1) of element <paramref name="element"/>.</summary>
    public static string Component(Segment s, int element, int part, char separator = ':')
    {
        var parts = s[element].Split(separator);
        return part <= parts.Length ? parts[part - 1].Trim() : "";
    }
}
