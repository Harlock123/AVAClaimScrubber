using System.Text.Json.Nodes;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>
/// Layer 6: payer rules, from the rule packs. Each pack's "rules" are kinds of rule a person (or an agent, with their
/// approval) writes for a payer: <c>timely-filing</c> (days from service to submission), <c>prior-auth</c> (procedures
/// that need an authorization number, REF*G1), <c>require-modifier</c> (a modifier lines in some places of service
/// need, such as 95 for telehealth), <c>max-units</c> (a procedure's units on a line), <c>not-covered</c>
/// (procedures the payer doesn't pay). Rules apply to the claims whose payer the pack names (or every claim).
/// </summary>
public sealed class PayerRuleChecks(IReadOnlyList<RulePack> packs) : ICheck
{
    public static readonly string[] Kinds = ["timely-filing", "prior-auth", "require-modifier", "max-units", "not-covered"];

    private List<(RulePack Pack, JsonObject Json, Rule Rule)> Read() =>
        [.. packs.SelectMany(p => p.Rules.OfType<JsonObject>().Select((j, i) => (p, j, RuleOf(p, j, i))))];

    /// <summary>A pack's rule as a <see cref="Rule"/>: id P-pack-id, its text, the pack as its source.</summary>
    public static Rule RuleOf(RulePack pack, JsonObject j, int index)
    {
        var id = (string?)j["id"] ?? $"{index + 1}";
        var severity = Enum.TryParse<Severity>((string?)j["severity"], true, out var s) ? s : Severity.Deny;
        var kind = (string?)j["kind"] ?? "";
        var title = (string?)j["text"] ?? kind switch
        {
            "timely-filing" => $"{pack.Name} takes claims within {(int?)j["days"]} days of service",
            "prior-auth" => $"{pack.Name} needs prior authorization for some procedures",
            "require-modifier" => $"{pack.Name} needs modifier {(string?)j["modifier"]} in some places of service",
            "max-units" => $"{pack.Name} limits the units of {(string?)j["procedure"]}",
            "not-covered" => $"{pack.Name} doesn't cover some procedures",
            _ => kind,
        };
        return new Rule($"P-{Slug(pack.Name)}-{id}", 6, title, (string?)j["why"] ?? title + ".", $"Rule pack \"{pack.Name}\" version {pack.Version}", severity, kind);
    }

    private static string Slug(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>Checks a pack's rules are kinds this layer knows, with what each needs; throws <see cref="FormatException"/> saying what's wrong.</summary>
    public static void Validate(RulePack pack)
    {
        foreach (var (j, i) in pack.Rules.Select((r, i) => (r as JsonObject ?? throw new FormatException($"Rule {i + 1} isn't an object."), i)))
        {
            var kind = (string?)j["kind"];
            if (kind == null || !Kinds.Contains(kind)) throw new FormatException($"Rule {i + 1}'s kind is \"{kind}\": it's one of {string.Join(", ", Kinds)}.");
            string[] needs = kind switch
            {
                "timely-filing" => ["days"],
                "prior-auth" or "not-covered" => ["procedures"],
                "require-modifier" => ["places", "modifier"],
                _ => ["procedure", "units"],
            };
            foreach (var n in needs) if (j[n] == null) throw new FormatException($"Rule {i + 1} ({kind}) needs \"{n}\".");
            if ((string?)j["severity"] is { } s && !Enum.TryParse<Severity>(s, true, out _)) throw new FormatException($"Rule {i + 1}'s severity is \"{s}\": it's info, warn, deny or reject.");
        }
    }

    public IEnumerable<Rule> Rules => Read().Select(r => r.Rule);

    public void Run(ScrubContext c)
    {
        var rules = Read();
        if (rules.Count == 0) return;
        foreach (var claim in c.Batch.Claims)
        {
            var payer = claim.Payer?[9].Trim() ?? "";
            foreach (var (pack, j, rule) in rules.Where(r => r.Pack.AppliesTo(payer)))
                Apply(c, claim, pack, j, rule);
        }
    }

    private static HashSet<string> Strings(JsonNode? node) => new((node as JsonArray ?? []).Select(x => (string?)x ?? ""), StringComparer.OrdinalIgnoreCase);

    private static void Apply(ScrubContext c, Claim claim, RulePack pack, JsonObject j, Rule rule)
    {
        switch ((string?)j["kind"])
        {
            case "timely-filing" when (int?)j["days"] is { } days && claim.Created is { } made:
                var first = claim.Lines.Select(l => Parse.Date(l.DateText)).Where(d => d != null).Min();
                if (first is { } d && made.DayNumber - d.DayNumber > days)
                    c.Add(rule, $"The first service was {made.DayNumber - d.DayNumber} days before the batch was made; {pack.Name} takes claims within {days} days.", claim, segment: claim.Clm, element: "DTP*472");
                break;
            case "prior-auth":
                var needs = Strings(j["procedures"]);
                var authorized = claim.Loop.Find("REF", "G1") != null;
                foreach (var line in claim.Lines.Where(l => needs.Contains(l.Procedure) && !authorized && l.Loop.Find("REF", "G1") == null))
                    c.Add(rule, $"Line {line.Number}'s {line.Procedure} needs prior authorization with {pack.Name}, and the claim has no authorization number (REF*G1).", claim, line, line.Service, "REF*G1");
                break;
            case "require-modifier":
                var places = Strings(j["places"]);
                var modifier = (string?)j["modifier"] ?? "";
                foreach (var line in claim.Lines)
                {
                    var place = line.PlaceOfService.Length > 0 ? line.PlaceOfService : claim.Facility;
                    if (places.Contains(place) && !line.Modifiers.Contains(modifier, StringComparer.OrdinalIgnoreCase))
                        c.Add(rule, $"Line {line.Number} is in place of service {place}, and {pack.Name} needs modifier {modifier} there.", claim, line, line.Service, "SV1-01");
                }
                break;
            case "max-units":
                var procedure = (string?)j["procedure"] ?? "";
                var max = (decimal?)j["units"] ?? 0;
                foreach (var line in claim.Lines.Where(l => l.Procedure.Equals(procedure, StringComparison.OrdinalIgnoreCase) && l.Units > max))
                    c.Add(rule, $"Line {line.Number} bills {line.Units:0.##} units of {procedure}; {pack.Name} pays {max:0.##}.", claim, line, line.Service, $"{line.Service.Id}-{line.UnitsElement:00}");
                break;
            case "not-covered":
                var codes = Strings(j["procedures"]);
                foreach (var line in claim.Lines.Where(l => codes.Contains(l.Procedure)))
                    c.Add(rule, $"Line {line.Number}'s {line.Procedure} isn't covered by {pack.Name}.", claim, line, line.Service, "SV1-01");
                break;
        }
    }
}
