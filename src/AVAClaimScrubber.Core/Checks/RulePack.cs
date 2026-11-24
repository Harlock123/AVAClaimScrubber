using System.Text.Json;
using System.Text.Json.Nodes;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>A change to a built-in rule in a pack: turned off, or a different severity.</summary>
public sealed record RuleOverride(string RuleId, bool Enabled, Severity? Severity, string? Why);

/// <summary>
/// A rule pack: a readable JSON file (format "avaclaimscrubber.rules") a person or an agent edits, for every payer or
/// the payers it names (by their 2010BB NM109 ID). It can turn a built-in rule off or change its severity, with the
/// reason; later layers add payer rules of their own. A pack that doesn't read is reported, not ignored.
/// </summary>
public sealed class RulePack
{
    public const string Format = "avaclaimscrubber.rules";

    public required string Name { get; init; }
    public string? Path { get; init; }
    public string Version { get; init; } = "1";
    public string? Description { get; init; }
    /// <summary>The payer IDs it applies to; empty for every payer.</summary>
    public IReadOnlyList<string> Payers { get; init; } = [];
    public IReadOnlyList<RuleOverride> Overrides { get; init; } = [];
    /// <summary>The pack's own rules, as JSON: read by the payer-rule layer.</summary>
    public JsonArray Rules { get; init; } = [];

    public bool AppliesTo(string payer) => Payers.Count == 0 || Payers.Contains(payer.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a pack; a pack that isn't right throws <see cref="FormatException"/> saying what's wrong.</summary>
    public static RulePack Read(string json, string? path = null)
    {
        JsonObject o;
        try
        {
            o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new FormatException("A rule pack is a JSON object.");
        }
        catch (JsonException e)
        {
            throw new FormatException($"It isn't JSON: {e.Message}");
        }
        if ((string?)o["format"] != Format) throw new FormatException($"\"format\" must be \"{Format}\".");
        var name = (string?)o["name"] is { Length: > 0 } n ? n : throw new FormatException("A rule pack needs a \"name\".");
        var known = Scrubber.AllRules().Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = new List<RuleOverride>();
        foreach (var (id, value) in o["overrides"] as JsonObject ?? [])
        {
            if (!known.Contains(id)) throw new FormatException($"\"overrides\" names a rule \"{id}\" that doesn't exist.");
            if (value is not JsonObject v) throw new FormatException($"The override for {id} must be an object, such as {{\"enabled\": false, \"why\": \"…\"}}.");
            Severity? severity = null;
            if ((string?)v["severity"] is { } s)
                severity = Enum.TryParse<Severity>(s, true, out var parsed) ? parsed : throw new FormatException($"The override for {id} has severity \"{s}\": it's info, warn, deny or reject.");
            overrides.Add(new RuleOverride(known.First(k => k.Equals(id, StringComparison.OrdinalIgnoreCase)), (bool?)v["enabled"] ?? true, severity, (string?)v["why"]));
        }
        var pack = new RulePack
        {
            Name = name,
            Path = path,
            Version = (string?)o["version"] ?? "1",
            Description = (string?)o["description"],
            Payers = (o["payers"] as JsonArray ?? []).Select(p => (string?)p ?? "").Where(p => p.Length > 0).ToList(),
            Overrides = overrides,
            Rules = o["rules"]?.DeepClone() as JsonArray ?? [],
        };
        PayerRuleChecks.Validate(pack);
        return pack;
    }

    public static RulePack Load(string path) => Read(File.ReadAllText(path), path);

    /// <summary>The packs in a folder (*.json), each read or its problem noted.</summary>
    public static (List<RulePack> Packs, List<string> Problems) LoadFolder(string folder)
    {
        var packs = new List<RulePack>();
        var problems = new List<string>();
        if (!Directory.Exists(folder)) return (packs, problems);
        foreach (var f in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                packs.Add(Load(f));
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                problems.Add($"{System.IO.Path.GetFileName(f)}: {e.Message}");
            }
        }
        return (packs, problems);
    }
}
