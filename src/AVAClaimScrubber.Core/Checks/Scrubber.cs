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
    public static IReadOnlyList<ICheck> Checks(ScrubSettings? settings = null) =>
        [new EnvelopeChecks(), new StructureChecks(), new ArithmeticChecks(), new CodeSetChecks(settings?.CodeSets), new EditChecks(settings?.CodeSets), new PayerRuleChecks(settings?.Packs ?? [])];

    public static ScrubResult Scrub(ClaimBatch batch, ScrubSettings? settings = null)
    {
        var context = new ScrubContext { Batch = batch };
        foreach (var check in Checks(settings)) check.Run(context);
        var findings = Overridden(batch, context.Findings, settings?.Packs ?? []);
        var ordered = findings.OrderBy(f => f.Claim).ThenByDescending(f => f.Severity).ThenBy(f => f.Line).ThenBy(f => f.Rule.Id, StringComparer.Ordinal).ToList();
        return new ScrubResult { Batch = batch, Findings = ordered };
    }

    /// <summary>
    /// Findings after the packs' overrides: a rule a pack turns off for a claim's payer is dropped; one it changes the
    /// severity of takes the new severity. The last pack that says something about a rule wins.
    /// </summary>
    private static List<Finding> Overridden(ClaimBatch batch, List<Finding> findings, IReadOnlyList<RulePack> packs)
    {
        if (packs.Count == 0) return findings;
        var result = new List<Finding>();
        foreach (var f in findings)
        {
            var payer = batch.Claims.FirstOrDefault(c => c.Number == f.Claim)?.Payer?[9] ?? "";
            var o = packs.Where(p => p.AppliesTo(payer)).SelectMany(p => p.Overrides).LastOrDefault(x => x.RuleId == f.Rule.Id);
            if (o == null) result.Add(f);
            else if (!o.Enabled) continue;
            else if (o.Severity is { } s)
                result.Add(new Finding { Rule = f.Rule with { Severity = s }, Message = f.Message, Claim = f.Claim, Line = f.Line, Loop = f.Loop, Element = f.Element, SegmentIndex = f.SegmentIndex, Fix = f.Fix });
            else result.Add(f);
        }
        return result;
    }

    /// <summary>Every rule the checks apply, in layer order.</summary>
    public static IEnumerable<Rule> AllRules(ScrubSettings? settings = null) => Checks(settings).SelectMany(c => c.Rules).OrderBy(r => r.Layer).ThenBy(r => r.Id, StringComparer.Ordinal);
}

/// <summary>What the checks work with beyond the claims: rule packs and code sets.</summary>
public sealed class ScrubSettings
{
    public IReadOnlyList<RulePack> Packs { get; init; } = [];

    /// <summary>The code sets loaded (null: none, and layer 4 says so).</summary>
    public CodeSets.CodeSets? CodeSets { get; init; }

    /// <summary>The program's settings folder (AVAClaimScrubber under the user's application data).</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } d ? d : Path.GetTempPath(), "AVAClaimScrubber");

    /// <summary>The rule packs (Rules/*.json) and code sets (CodeSets/) kept in a settings folder, as the window keeps them; what can't be read is left out.</summary>
    public static ScrubSettings Load(string folder)
    {
        CodeSets.CodeSets? sets = null;
        try
        {
            sets = AVAClaimScrubber.Core.CodeSets.CodeSets.Load(Path.Combine(folder, "CodeSets"));
        }
        catch (Exception e) when (e is FormatException or IOException) { }
        return new ScrubSettings { Packs = RulePack.LoadFolder(Path.Combine(folder, "Rules")).Packs, CodeSets = sets };
    }
}
