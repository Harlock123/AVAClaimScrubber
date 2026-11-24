using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>A batch's claims with their findings, worst first within each claim.</summary>
public sealed class ScrubResult
{
    public required ClaimBatch Batch { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }

    /// <summary>A claim's worst finding's severity, or null when it's clean.</summary>
    public Severity? Status(Claim claim) => Findings.Where(f => f.Claim == claim.Number).Select(f => (Severity?)f.Severity).Max();

    public IEnumerable<Finding> Of(Claim claim) => Findings.Where(f => f.Claim == claim.Number);

    public IEnumerable<Finding> Envelope => Findings.Where(f => f.Claim == 0);
}

/// <summary>Runs the layers of checks over a batch, in order: envelope and syntax, structure, arithmetic, code sets, edits, payer rules.</summary>
public static class Scrubber
{
    public static IReadOnlyList<ICheck> Checks(ScrubSettings? settings = null) => [new EnvelopeChecks(), new ArithmeticChecks()];

    public static ScrubResult Scrub(ClaimBatch batch, ScrubSettings? settings = null)
    {
        var context = new ScrubContext { Batch = batch };
        foreach (var check in Checks(settings)) check.Run(context);
        var ordered = context.Findings.OrderBy(f => f.Claim).ThenByDescending(f => f.Severity).ThenBy(f => f.Line).ThenBy(f => f.Rule.Id, StringComparer.Ordinal).ToList();
        return new ScrubResult { Batch = batch, Findings = ordered };
    }

    /// <summary>Every rule the checks apply, in layer order.</summary>
    public static IEnumerable<Rule> AllRules(ScrubSettings? settings = null) => Checks(settings).SelectMany(c => c.Rules).OrderBy(r => r.Layer).ThenBy(r => r.Id, StringComparer.Ordinal);
}

/// <summary>What the checks work with beyond the claims (code sets, payer rules): filled in by later layers.</summary>
public sealed class ScrubSettings
{
}
