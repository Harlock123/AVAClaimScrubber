using System.Text.Json.Serialization;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>What a finding means for the claim.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Info,
    /// <summary>Worth a look; unlikely to stop payment.</summary>
    Warn,
    /// <summary>The payer would likely deny it (or a line of it).</summary>
    Deny,
    /// <summary>The clearinghouse or payer would bounce it before adjudication (a 999 or 277CA rejection).</summary>
    Reject,
}

/// <summary>
/// A rule a check applies: its id (the layer's letter and a number: E envelope, S syntax, L loops, A arithmetic and
/// dates, C code sets, N NCCI and MUE, P payer), what it checks, why it matters, where it comes from, and how bad
/// breaking it is.
/// </summary>
public sealed record Rule(string Id, int Layer, string Title, string Why, string Source, Severity Severity, string Kind = "");

/// <summary>A change that would fix a finding: element <see cref="Element"/> (component <see cref="Component"/>, 0 for all of it) of the file's segment <see cref="SegmentIndex"/>.</summary>
public sealed record Fix(int SegmentIndex, int Element, int Component, string NewValue, string Description);

/// <summary>
/// Something wrong with a claim (or the file round it): the rule, the claim (by its number in the batch), the loop and
/// element (2300 CLM02, 2400 SV1-04), what's wrong in words, and the fix where one is clear. Messages describe values
/// without quoting people's details (a date is "27 days after the batch was made", never the date), so a finding's
/// text is as safe to show an AI agent as its rule.
/// </summary>
public sealed class Finding
{
    public required Rule Rule { get; init; }
    public required string Message { get; init; }
    /// <summary>The claim's number in the batch (from 1), or 0 for the file's envelope.</summary>
    public int Claim { get; init; }
    /// <summary>The service line (LX01), or 0.</summary>
    public int Line { get; init; }
    public string Loop { get; init; } = "";
    /// <summary>The element, as people write it: CLM02, SV1-04, HI01-2.</summary>
    public string Element { get; init; } = "";
    public int SegmentIndex { get; init; } = -1;
    public Fix? Fix { get; init; }
    public Severity Severity => Rule.Severity;

    /// <summary>Stable across runs on the same file: rule, claim, line, place.</summary>
    public string Id => $"{Rule.Id}|{Claim}|{Line}|{Element}|{SegmentIndex}";

    public string Place => string.Join(" ", new[] { Claim > 0 ? $"claim {Claim}" : "envelope", Line > 0 ? $"line {Line}" : null, Loop.Length > 0 ? Loop : null, Element.Length > 0 ? Element : null }.Where(p => p != null));

    public override string ToString() => $"{Severity} {Rule.Id} {Place}: {Message}";
}

/// <summary>What a run of the checks works from: the claims, and when "now" is.</summary>
public sealed class ScrubContext
{
    public required ClaimBatch Batch { get; init; }
    public List<Finding> Findings { get; } = [];
    public char Component => Batch.File.Delimiters.Component;

    public void Add(Rule rule, string message, Claim? claim = null, ServiceLine? line = null, Segment? segment = null, string element = "", Fix? fix = null, string? loop = null) =>
        Findings.Add(new Finding
        {
            Rule = rule,
            Message = message,
            Claim = claim?.Number ?? 0,
            Line = line?.Number ?? 0,
            Loop = loop ?? line?.Loop.Id ?? (claim != null ? "2300" : ""),
            Element = element,
            SegmentIndex = segment?.Index ?? -1,
            Fix = fix,
        });
}

/// <summary>One layer of checks.</summary>
public interface ICheck
{
    IEnumerable<Rule> Rules { get; }
    void Run(ScrubContext context);
}
