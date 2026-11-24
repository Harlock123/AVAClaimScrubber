using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.CodeSets;
using AVAClaimScrubber.Core.Reports;
using AVAClaimScrubber.Core.Responses;
using AVAClaimScrubber.Core.X12;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAClaimScrubber.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with claims. The checks run on the real files (dates and amounts as they are);
/// what an agent reads of a file (accounts, names, member IDs, dates) comes from its de-identified copy unless the person
/// lets agents see real values, and every answer says which ("deidentified"). Findings' messages carry no names,
/// identifiers or dates, so they're the same either way. The only files tools write are new ones in the working folder;
/// they never change a rule pack or a code set the person uses. Failures come back as MCP tool errors.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case. Claims, lines and segments count from 1.</remarks>
public sealed partial class ClaimsTools(IExplorerHost host)
{
    private readonly Workspace _files = new(() => host.Root);

    public const int MaxResults = 300;

    private const string FileHelp = "An open file: its name (clinic-837p.x12) or its path relative to the working folder.";
    private const string ClaimHelp = "The claim: its number in the file (1 for the first), or its account number (CLM01) as these tools show it.";

    // ───────────────────────────── files ─────────────────────────────

    [McpServerTool(Name = "list_files", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the X12 files in the working folder (and its subfolders), with what each is: 837P or 837I claims, a 999, a 277CA or an 835. Start here, then open_file.")]
    public Task<string> ListFiles(
        [Description("A folder inside the working folder to list instead. Optional.")] string? folder = null,
        CancellationToken cancel = default) =>
        Session("listed the files", s =>
        {
            var (list, more) = _files.List(folder);
            var open = s.Files.Select(f => f.Path).Concat(s.Responses.Select(r => r.Path)).ToHashSet();
            var result = new JsonObject
            {
                ["folder"] = _files.Root,
                ["files"] = new JsonArray([.. list.Select(f => (JsonNode)new JsonObject { ["file"] = f, ["kind"] = KindOf(_files.Resolve(f)), ["open"] = open.Contains(_files.Resolve(f)) })]),
            };
            if (more) result["note"] = $"Only the first {Workspace.MaxListed} are listed. Name a folder to see the rest.";
            return result;
        }, cancel);

    private static string KindOf(string path)
    {
        try
        {
            var f = X12File.Load(path);
            return f.Code switch
            {
                "837" => f.Transactions.FirstOrDefault()?.Start[3].Trim() is { } v && v.Contains("X223", StringComparison.Ordinal) ? "837I claims" : "837P claims",
                "999" => "999 acknowledgment", "277" => "277CA claim acknowledgment", "835" => "835 remittance", "271" => "271 eligibility answer", null => "X12 (no transactions)", var c => c,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return "unreadable";
        }
    }

    [McpServerTool(Name = "open_file", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Opens an 837 (and checks every claim in it) or a response (999, 277CA, 835) from the working folder, as File › Open does; in the open window it opens there too. Answers with the file's summary.")]
    public Task<string> OpenFile(
        [Description("The file, relative to the working folder.")] string file,
        CancellationToken cancel = default) =>
        Session("opened " + file, s =>
        {
            var full = _files.Existing(file);
            s.Open(full);
            if (s.Files.FirstOrDefault(f => f.Path == full) is { } claims) return BatchSummary(s, claims);
            var r = s.Responses.First(x => x.Path == full);
            return new JsonObject { ["file"] = r.File.Name, ["kind"] = r.File.KindName, ["summary"] = r.File.Summary, ["next"] = "replay sets its denials against the open claims' findings." };
        }, cancel);

    [McpServerTool(Name = "close_file", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Closes an open 837 or response (the file itself is left as it is).")]
    public Task<string> CloseFile(
        [Description(FileHelp)] string file,
        CancellationToken cancel = default) =>
        Session("closed " + file, s =>
        {
            var path = PathOf(s, file);
            s.Close(path);
            return new JsonObject { ["closed"] = Path.GetFileName(path) };
        }, cancel);

    [McpServerTool(Name = "list_open", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The files open now: each 837 with its claims' counts (clean, rejected or denied, findings), each response with its summary, and the rule packs and code sets the checks use.")]
    public Task<string> ListOpen(CancellationToken cancel = default) =>
        Session("listed the open files", s => new JsonObject
        {
            ["claims"] = new JsonArray([.. s.Files.Select(f => (JsonNode)BatchSummary(s, f))]),
            ["responses"] = new JsonArray([.. s.Responses.Select(r => (JsonNode)new JsonObject { ["file"] = r.File.Name, ["kind"] = r.File.KindName, ["summary"] = r.File.Summary })]),
            ["rule_packs"] = new JsonArray([.. s.Settings.Packs.Select(p => (JsonNode)$"{p.Name} {p.Version}")]),
            ["code_sets"] = new JsonArray([.. (s.Settings.CodeSets?.Info ?? []).Select(i => (JsonNode)$"{i.Name} {i.Version}")]),
        }, cancel);

    // ───────────────────────────── claims ─────────────────────────────

    [McpServerTool(Name = "get_batch", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("An open 837's claims: each with its number, account, kind, charge, lines, status (Clean, Info, Warn, Deny or Reject: the worst finding) and how many findings; and the envelope's own findings.")]
    public Task<string> GetBatch(
        [Description(FileHelp + " Optional when one 837 is open.")] string? file = null,
        CancellationToken cancel = default) =>
        Session("read a batch", s =>
        {
            var f = FileOf(s, file);
            var (batch, lens) = Lensed(s, f);
            var o = BatchSummary(s, f);
            o["claims"] = new JsonArray([.. batch.Claims.Select(c => (JsonNode)new JsonObject
            {
                ["claim"] = c.Number, ["account"] = c.Account, ["kind"] = c.Kind.ToString(), ["charge"] = c.Total, ["lines"] = c.Lines.Count,
                ["status"] = Status(f.Result, c), ["findings"] = f.Result.Findings.Count(x => x.Claim == c.Number),
            })]);
            o["envelope_findings"] = new JsonArray([.. f.Result.Envelope.Select(x => (JsonNode)FindingJson(x, batch))]);
            return o;
        }, cancel);

    [McpServerTool(Name = "get_claim", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("One claim in full: kind, frequency, place of service, charge, payer ID, billing NPI, diagnoses (with their pointers' numbers), each line (procedure, modifiers, charge, units, place, pointers, date), and its findings (rule, severity, where, message, the fix if there's a clear one). get_segments gives its raw segments.")]
    public Task<string> GetClaim(
        [Description(ClaimHelp)] string claim,
        [Description(FileHelp + " Optional when one 837 is open.")] string? file = null,
        CancellationToken cancel = default) =>
        Session("read a claim", s =>
        {
            var f = FileOf(s, file);
            var (batch, _) = Lensed(s, f);
            var c = ClaimOf(batch, claim);
            return new JsonObject
            {
                ["claim"] = c.Number, ["account"] = c.Account, ["kind"] = c.Kind.ToString(), ["frequency"] = c.Frequency, ["place_of_service"] = c.Facility,
                ["charge"] = c.Total, ["payer_id"] = c.Payer?[9].Trim(), ["billing_npi"] = c.BillingNpi?[9].Trim(), ["status"] = Status(f.Result, c),
                ["diagnoses"] = new JsonArray([.. c.Diagnoses.Select(d => (JsonNode)new JsonObject { ["number"] = d.Number, ["qualifier"] = d.Qualifier, ["code"] = d.Code })]),
                ["lines"] = new JsonArray([.. c.Lines.Select(l => (JsonNode)new JsonObject
                {
                    ["line"] = l.Number, ["procedure"] = l.Procedure, ["modifiers"] = new JsonArray([.. l.Modifiers.Select(m => (JsonNode)m)]), ["revenue_code"] = l.RevenueCode.Length > 0 ? l.RevenueCode : null,
                    ["charge"] = l.Charge, ["units"] = l.Units, ["place_of_service"] = l.PlaceOfService.Length > 0 ? l.PlaceOfService : null,
                    ["pointers"] = new JsonArray([.. l.Pointers.Select(p => (JsonNode)p)]), ["date"] = l.DateText,
                })]),
                ["findings"] = new JsonArray([.. f.Result.Findings.Where(x => x.Claim == c.Number).Select(x => (JsonNode)FindingJson(x, batch))]),
            };
        }, cancel);

    [McpServerTool(Name = "get_segments", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The raw segments of a claim (its loops, from CLM; with include_parents, the billing provider's and subscriber's too), or of the whole file, each with its number in the file; segments a finding points at are marked.")]
    public Task<string> GetSegments(
        [Description(ClaimHelp + " Optional: the whole file (up to 300 segments from first) when not given.")] string? claim = null,
        [Description(FileHelp + " Optional when one 837 is open.")] string? file = null,
        [Description("With a claim: include its billing provider's and subscriber's segments. Default false.")] bool include_parents = false,
        [Description("Without a claim: the first segment to give (from 1). Default 1.")] int first = 1,
        CancellationToken cancel = default) =>
        Session("read segments", s =>
        {
            var f = FileOf(s, file);
            var (batch, _) = Lensed(s, f);
            IEnumerable<Segment> segs;
            if (string.IsNullOrWhiteSpace(claim)) segs = batch.File.Segments.Skip(Math.Max(first, 1) - 1).Take(MaxResults);
            else
            {
                var c = ClaimOf(batch, claim);
                var own = c.Loop.All().SelectMany(l => l.Segments);
                if (include_parents)
                    own = new[] { c.BillingProvider, c.Subscriber, c.Patient }.Where(l => l != null)
                        .SelectMany(l => l!.Segments.Concat(l.Children.Where(x => x.Id.StartsWith("2010", StringComparison.Ordinal)).SelectMany(x => x.Segments))).Concat(own);
                segs = own.DistinctBy(x => x.Index).OrderBy(x => x.Index);
            }
            var marked = f.Result.Findings.Where(x => x.SegmentIndex >= 0).GroupBy(x => x.SegmentIndex).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Rule.Id).Distinct()));
            return new JsonObject
            {
                ["separators"] = $"segment {(batch.File.Delimiters.LineTerminated ? "line break" : batch.File.Delimiters.Segment)}, element {batch.File.Delimiters.Element}, component {batch.File.Delimiters.Component}",
                ["segments"] = new JsonArray([.. segs.Select(x => (JsonNode)new JsonObject { ["n"] = x.Index + 1, ["text"] = x.Text, ["findings"] = marked.TryGetValue(x.Index, out var r) ? r : null })]),
            };
        }, cancel);

    [McpServerTool(Name = "get_findings", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Findings across the open 837s (or one), worst first, filtered by severity, rule, layer or claim. Each says its rule, where it is (claim, line, loop, element), what's wrong, and the fix when there's a clear one. Messages carry no names, IDs or dates.")]
    public Task<string> GetFindings(
        [Description(FileHelp + " Optional: every open 837.")] string? file = null,
        [Description("Only this severity and worse: info, warn, deny or reject. Optional.")] string? severity = null,
        [Description("Only this rule (A1, C3, P-sunplan-auth…). Optional.")] string? rule = null,
        [Description("Only this layer, 1 to 6: envelope and syntax, structure, arithmetic and dates, code sets, Medicare edits, payer rules. Optional.")] int layer = 0,
        [Description(ClaimHelp + " Optional; needs file.")] string? claim = null,
        CancellationToken cancel = default) =>
        Session("read the findings", s =>
        {
            var min = severity is { Length: > 0 } sv ? Enum.TryParse<Severity>(sv, true, out var m) ? m : throw new ArgumentException("severity is info, warn, deny or reject.") : Severity.Info;
            var files = string.IsNullOrWhiteSpace(file) ? s.Files : [FileOf(s, file)];
            var list = new JsonArray();
            var total = 0;
            foreach (var f in files)
            {
                var (batch, _) = Lensed(s, f);
                var number = string.IsNullOrWhiteSpace(claim) ? 0 : ClaimOf(batch, claim).Number;
                foreach (var x in f.Result.Findings.Where(x => x.Severity >= min && (rule == null || x.Rule.Id.Equals(rule.Trim(), StringComparison.OrdinalIgnoreCase)) && (layer == 0 || x.Rule.Layer == layer) && (number == 0 || x.Claim == number))
                             .OrderByDescending(x => x.Severity).ThenBy(x => x.Claim).ThenBy(x => x.Line))
                {
                    total++;
                    if (list.Count < MaxResults) { var o = FindingJson(x, batch); o["file"] = batch.Name; list.Add(o); }
                }
            }
            return new JsonObject { ["count"] = total, ["findings"] = list, ["more"] = total > list.Count ? total - list.Count : null };
        }, cancel);

    [McpServerTool(Name = "check_text", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Checks an 837 given as text (one you've drafted or changed) with the same checks, rule packs and code sets, without saving it. Answers with every claim's status and every finding.")]
    public Task<string> CheckText(
        [Description("The 837, as X12 text: ISA to IEA, or ST to SE.")] string text,
        CancellationToken cancel = default) =>
        Session("checked a claim it wrote", s =>
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is an 837 as X12 text.");
            var batch = ClaimReader.Read("text", X12File.Parse(text));
            if (batch.Claims.Count == 0) throw new FormatException("The text has no claims (no CLM segment in an 837 transaction).");
            var r = Scrubber.Scrub(batch, s.Settings);
            return new JsonObject
            {
                ["summary"] = Summary(r),
                ["claims"] = new JsonArray([.. batch.Claims.Select(c => (JsonNode)new JsonObject { ["claim"] = c.Number, ["account"] = c.Account, ["status"] = Status(r, c) })]),
                ["findings"] = new JsonArray([.. r.Findings.Select(x => (JsonNode)FindingJson(x, batch))]),
            };
        }, cancel);

    // ───────────────────────────── rules ─────────────────────────────

    [McpServerTool(Name = "list_rules", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The rules the checks apply, by layer (1 envelope and syntax, 2 structure, 3 arithmetic and dates, 4 code sets, 5 Medicare edits, 6 payer rules from the packs): id, title and severity, after the packs' overrides.")]
    public Task<string> ListRules(
        [Description("Only this layer, 1 to 6. Optional.")] int layer = 0,
        CancellationToken cancel = default) =>
        Session("listed the rules", s => new JsonObject
        {
            ["rules"] = new JsonArray([.. Scrubber.AllRules(s.Settings).Where(r => layer == 0 || r.Layer == layer).Select(r => (JsonNode)new JsonObject { ["rule"] = r.Id, ["layer"] = r.Layer, ["title"] = r.Title, ["severity"] = r.Severity.ToString() })]),
        }, cancel);

    [McpServerTool(Name = "explain_rule", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("One rule: what it checks, why, its source (the implementation guide, CMS file or rule pack), its severity, and the payer reason codes (CARCs, 277 statuses) a claim it catches would come back with.")]
    public Task<string> ExplainRule(
        [Description("The rule's id: A1, C3, N1, P-sunplan-auth…")] string rule,
        CancellationToken cancel = default) =>
        Session("explained a rule", s =>
        {
            var r = Scrubber.AllRules(s.Settings).FirstOrDefault(x => x.Id.Equals(rule.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"There's no rule \"{rule}\". list_rules gives them.");
            var carcs = Replay.Carc.Where(kv => kv.Value.Rules.Contains(r.Id) || (r.Kind.Length > 0 && kv.Value.Kinds.Contains(r.Kind))).Select(kv => (JsonNode)$"CARC {kv.Key}: {ResponseCodes.Describe(ResponseCodes.Carc, kv.Key)}");
            var statuses = Replay.Status.Where(kv => kv.Value.Rules.Contains(r.Id)).Select(kv => (JsonNode)$"277 status {kv.Key}: {ResponseCodes.Describe(ResponseCodes.Status, kv.Key)}");
            return new JsonObject
            {
                ["rule"] = r.Id, ["layer"] = r.Layer, ["title"] = r.Title, ["why"] = r.Why, ["source"] = r.Source, ["severity"] = r.Severity.ToString(), ["kind"] = r.Kind.Length > 0 ? r.Kind : null,
                ["answers_to"] = new JsonArray([.. carcs, .. statuses]),
            };
        }, cancel);

    [McpServerTool(Name = "list_rule_packs", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The rule packs in use: each one's name, version, the payers it's for, its overrides (rules turned off or changed in severity) and its payer rules, as JSON.")]
    public Task<string> ListRulePacks(CancellationToken cancel = default) =>
        Session("listed the rule packs", s => new JsonObject
        {
            ["packs"] = new JsonArray([.. s.Settings.Packs.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["version"] = p.Version, ["payers"] = new JsonArray([.. p.Payers.Select(x => (JsonNode)x)]),
                ["overrides"] = new JsonArray([.. p.Overrides.Select(o => (JsonNode)$"{o.RuleId}: {(o.Enabled ? o.Severity?.ToString() ?? "on" : "off")}")]),
                ["rules"] = p.Rules.DeepClone(),
            })]),
            ["kinds"] = "Payer rule kinds: " + string.Join(", ", PayerRuleChecks.Kinds) + ".",
        }, cancel);

    [McpServerTool(Name = "test_rule_pack", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What-if: checks the open 837s (or one) with a rule pack added (given as JSON), without adding it, and says which findings it adds or takes away. The pack is checked first; a problem in it comes back as an error.")]
    public Task<string> TestRulePack(
        [Description("The rule pack, as JSON (format \"avaclaimscrubber.rules\"): name, payers, overrides, rules.")] string pack,
        [Description(FileHelp + " Optional: every open 837.")] string? file = null,
        CancellationToken cancel = default) =>
        Session("tried a rule pack", s =>
        {
            var p = RulePack.Read(pack);
            var settings = new ScrubSettings { CodeSets = s.Settings.CodeSets, Packs = [.. s.Settings.Packs, p] };
            var files = string.IsNullOrWhiteSpace(file) ? s.Files : [FileOf(s, file)];
            var changes = new JsonArray();
            foreach (var f in files)
            {
                var (batch, _) = Lensed(s, f);
                var after = Scrubber.Scrub(f.Result.Batch, settings);
                var before = f.Result.Findings.Select(x => x.Id).ToHashSet();
                var now = after.Findings.Select(x => x.Id).ToHashSet();
                foreach (var x in after.Findings.Where(x => !before.Contains(x.Id))) changes.Add(Change("added", x, batch));
                foreach (var x in f.Result.Findings.Where(x => !now.Contains(x.Id))) changes.Add(Change("removed", x, batch));
            }
            return new JsonObject { ["pack"] = p.Name, ["changes"] = changes, ["note"] = "Nothing was added: write_rule_pack saves it for the person to add (File › Add Rule Pack…)." };
        }, cancel);

    private static JsonNode Change(string what, Finding f, ClaimBatch batch)
    {
        var o = FindingJson(f, batch);
        o["change"] = what;
        o["file"] = batch.Name;
        return o;
    }

    [McpServerTool(Name = "write_rule_pack", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Saves a rule pack (JSON) as a new .json file in the working folder, after checking it reads. It isn't added to the checks: the person reads it and adds it (File › Add Rule Pack…).")]
    public Task<string> WriteRulePack(
        [Description("The rule pack, as JSON.")] string pack,
        [Description("The new file, relative to the working folder (.json).")] string output,
        CancellationToken cancel = default) =>
        Write("wrote a rule pack", output, "A rule pack", [".json"], (_, full) =>
        {
            var p = RulePack.Read(pack);
            File.WriteAllText(full, Replay.Text((JsonObject)JsonNode.Parse(pack)!));
            return new JsonObject { ["pack"] = p.Name, ["rules"] = p.Rules.Count, ["overrides"] = p.Overrides.Count };
        }, cancel);

    // ───────────────────────────── codes ─────────────────────────────

    [McpServerTool(Name = "get_code_sets", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("The code sets the checks use: each one's version, how many codes, when it was loaded and where from (ICD-10-CM, HCPCS Level II, NUCC taxonomy, NCCI PTP, MUE, a licensed CPT file), and the built-in places of service. A set not loaded isn't checked against.")]
    public Task<string> GetCodeSets(CancellationToken cancel = default) =>
        Session("read the code sets", s => new JsonObject
        {
            ["loaded"] = new JsonArray([.. (s.Settings.CodeSets?.Info ?? []).Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["version"] = i.Version, ["codes"] = i.Count, ["source"] = i.Source })]),
            ["places_of_service"] = PlaceOfService.Codes.Count,
            ["cpt"] = s.Settings.CodeSets?.Cpt != null ? "a licensed CPT file is loaded" : "not loaded: CPT codes get a shape check only (CPT belongs to the AMA)",
        }, cancel);

    [McpServerTool(Name = "lookup_code", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Looks a code up: an ICD-10-CM diagnosis (and whether it's billable, or the billable codes under a header), a HCPCS code (dates it was in effect) or modifier, a CPT code's shape, a provider taxonomy, a place of service, an NPI's check digit, a CARC, RARC or 277 status code (as X12 words it).")]
    public Task<string> LookupCode(
        [Description("The code: E119, E11.9, G0439, 25, 207Q00000X, 11, 1234567893, CO-197, N54…")] string code,
        [Description("Which set: icd, hcpcs, modifier, cpt, taxonomy, pos, npi, carc, rarc or status. Optional: worked out from the code's shape.")] string? set = null,
        CancellationToken cancel = default) =>
        Session("looked up a code", s => Lookup(s.Settings.CodeSets, code, set), cancel);

    [McpServerTool(Name = "search_codes", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Searches the code sets' descriptions for words (all of them, any case): ICD-10-CM diagnoses, HCPCS codes, taxonomies or CARCs. Billable ICD codes first.")]
    public Task<string> SearchCodes(
        [Description("Words to look for: \"type 2 diabetes kidney\", \"pneumococcal\", \"family medicine\".")] string text,
        [Description("icd (default), hcpcs, taxonomy or carc.")] string set = "icd",
        [Description("The most to return, up to 300. Default 25.")] int limit = 25,
        CancellationToken cancel = default) =>
        Session("searched the codes", s =>
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) throw new ArgumentException("text is words to look for.");
            bool Hit(string t) => words.All(w => t.Contains(w, StringComparison.OrdinalIgnoreCase));
            var sets = s.Settings.CodeSets;
            IEnumerable<(string Code, string Text)> found = set.Trim().ToLowerInvariant() switch
            {
                "icd" => (sets?.Icd ?? throw NotLoaded("ICD-10-CM")).Values.Where(c => Hit(c.Title)).OrderBy(c => !c.Billable).ThenBy(c => c.Code, StringComparer.Ordinal).Select(c => (c.Code, c.Title + (c.Billable ? "" : " (header)"))),
                "hcpcs" => (sets?.Hcpcs ?? throw NotLoaded("HCPCS Level II")).Values.Where(c => Hit(c.Title)).OrderBy(c => c.Code, StringComparer.Ordinal).Select(c => (c.Code, c.Title + (c.Terminated is { } t ? $" (ended {t:yyyy-MM-dd})" : ""))),
                "taxonomy" => (sets?.Taxonomy ?? throw NotLoaded("NUCC taxonomy")).Where(kv => Hit(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)),
                "carc" => ResponseCodes.Carc.Where(kv => Hit(kv.Value)).Select(kv => (kv.Key, kv.Value)),
                _ => throw new ArgumentException("set is icd, hcpcs, taxonomy or carc."),
            };
            var all = found.ToList();
            var take = Math.Clamp(limit, 1, MaxResults);
            return new JsonObject { ["count"] = all.Count, ["codes"] = new JsonArray([.. all.Take(take).Select(c => (JsonNode)new JsonObject { ["code"] = c.Code, ["description"] = c.Text })]) };
        }, cancel);

    private static InvalidOperationException NotLoaded(string name) => new($"{name} isn't loaded: the person loads it with Update on the Code Sets tab.");

    internal static JsonObject Lookup(CodeSets? sets, string code, string? set)
    {
        var c = code.Trim().ToUpperInvariant();
        if (c.Length == 0) throw new ArgumentException("code is the code to look up.");
        var kind = set?.Trim().ToLowerInvariant() is { Length: > 0 } k ? k
            : c.StartsWith("CO-") || c.StartsWith("PR-") || c.StartsWith("OA-") || c.StartsWith("PI-") || c.StartsWith("CR-") ? "carc"
            : c.Length == 10 && c.All(char.IsDigit) ? "npi"
            : c.Length == 10 && c.EndsWith('X') ? "taxonomy"
            : c.Length == 2 && c.All(char.IsDigit) && PlaceOfService.Codes.ContainsKey(c) && set == null ? "pos"
            : c.Length == 2 ? "modifier"
            : c.Length == 5 && char.IsLetter(c[0]) && c[1..].All(char.IsDigit) ? "hcpcs"
            : c.Length == 5 && c[..4].All(char.IsDigit) ? "cpt"
            : char.IsLetter(c[0]) && c.Length is >= 3 and <= 8 && char.IsDigit(c[1]) ? "icd"
            : (c.StartsWith('M') || c.StartsWith('N')) && ResponseCodes.Rarc.ContainsKey(c) ? "rarc"
            : throw new ArgumentException($"Can't tell what kind of code {code} is: give set.");
        var o = new JsonObject { ["code"] = c, ["set"] = kind };
        switch (kind)
        {
            case "icd":
                var icd = sets?.Icd ?? throw NotLoaded("ICD-10-CM");
                var bare = c.Replace(".", "", StringComparison.Ordinal);
                if (icd.TryGetValue(bare, out var d))
                {
                    o["description"] = d.Title;
                    o["billable"] = d.Billable;
                    if (!d.Billable) o["billable_codes_under_it"] = new JsonArray([.. icd.Values.Where(x => x.Billable && x.Code.StartsWith(bare, StringComparison.Ordinal)).Select(x => x.Code).Order(StringComparer.Ordinal).Take(30).Select(x => (JsonNode)x)]);
                }
                else o["found"] = false;
                if (c.Contains('.')) o["note"] = $"X12 sends ICD codes without the decimal point: {bare}.";
                break;
            case "hcpcs":
                var h = sets?.Hcpcs ?? throw NotLoaded("HCPCS Level II");
                if (h.TryGetValue(c, out var hc))
                {
                    o["description"] = hc.Title;
                    o["added"] = hc.Added?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    o["terminated"] = hc.Terminated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                else o["found"] = false;
                break;
            case "modifier":
                o["kind"] = c.All(char.IsDigit) ? "CPT modifier (shape only: CPT belongs to the AMA)" : "HCPCS Level II modifier";
                if (!c.All(char.IsDigit)) o["found"] = sets?.HcpcsModifiers?.Contains(c) ?? throw NotLoaded("HCPCS Level II");
                o["ncci_bypass"] = EditChecks.NcciModifiers.Contains(c);
                break;
            case "cpt":
                o["shape_ok"] = CodeSetChecks.IsCptShape(c);
                if (sets?.Cpt is { } cpt) o["found"] = cpt.ContainsKey(c);
                else o["note"] = "No licensed CPT file is loaded: only its shape is checked.";
                if (sets?.MuePractitioner?.TryGetValue(c, out var mue) == true) o["mue_practitioner"] = $"{mue.Units} ({(mue.Mai == 1 ? "a line" : "a day")})";
                break;
            case "taxonomy":
                o["shape_ok"] = c.Length == 10 && c.EndsWith('X');
                o["description"] = sets?.Taxonomy is { } tax ? tax.TryGetValue(c, out var t) ? t : null : throw NotLoaded("NUCC taxonomy");
                break;
            case "pos":
                o["description"] = PlaceOfService.Codes.TryGetValue(c, out var p) ? p : null;
                break;
            case "npi":
                o["check_digit_ok"] = CodeSetChecks.IsNpi(c);
                o["note"] = "Only the check digit is checked: whether the NPI belongs to the provider is NPPES's record, which isn't looked up.";
                break;
            case "carc":
                var reason = c.Contains('-') ? c[(c.IndexOf('-') + 1)..] : c;
                o["code"] = reason;
                if (c.Contains('-')) o["group"] = ResponseCodes.Describe(ResponseCodes.Group, c[..2]);
                o["description"] = ResponseCodes.Describe(ResponseCodes.Carc, reason);
                Answers(o, Replay.Carc.TryGetValue(reason, out var m) ? m : null);
                break;
            case "rarc":
                o["description"] = ResponseCodes.Describe(ResponseCodes.Rarc, c);
                if (Replay.Rarc.TryGetValue(c, out var rules)) o["checks"] = new JsonArray([.. rules.Select(x => (JsonNode)x)]);
                break;
            case "status":
                o["description"] = ResponseCodes.StatusCategory.TryGetValue(c, out var cat) ? cat : ResponseCodes.Describe(ResponseCodes.Status, c);
                Answers(o, Replay.Status.TryGetValue(c, out var sm) ? sm : null);
                break;
            default:
                throw new ArgumentException("set is icd, hcpcs, modifier, cpt, taxonomy, pos, npi, carc, rarc or status.");
        }
        return o;
    }

    private static void Answers(JsonObject o, Replay.Mapping? m)
    {
        if (m == null) return;
        if (m.Rules.Length > 0) o["checks"] = new JsonArray([.. m.Rules.Select(x => (JsonNode)x)]);
        if (m.Kinds.Length > 0) o["payer_rule_kinds"] = new JsonArray([.. m.Kinds.Select(x => (JsonNode)x)]);
        if (m.NotCheckable != null) o["not_checkable"] = m.NotCheckable;
    }

    // ───────────────────────────── responses ─────────────────────────────

    [McpServerTool(Name = "get_response", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("An open 999, 277CA, 835 or 271 in full: a 999's answer for each transaction and its errors; a 277CA's status for each claim; an 835's payment for each claim, its lines, adjustments (group, CARC, amount) and remarks (RARCs), each code with X12's words; a 271's answer about each person (found or not, coverage, another payer primary, care needing authorization), which the eligibility checks (V1 to V4) use.")]
    public Task<string> GetResponse(
        [Description("The open response: its name or path. Optional when one is open.")] string? file = null,
        CancellationToken cancel = default) =>
        Session("read a response", s =>
        {
            var r = LensResponse(s, ResponseOf(s, file));
            var o = new JsonObject { ["file"] = r.Name, ["kind"] = r.KindName, ["payer"] = r.Payer.Length > 0 ? r.Payer : null, ["summary"] = r.Summary };
            if (r.Kind == "999")
                o["transactions"] = new JsonArray([.. r.Acks.Select(a => (JsonNode)new JsonObject
                {
                    ["group_control"] = a.GroupControl, ["transaction_control"] = a.TransactionControl, ["status"] = a.StatusText,
                    ["reason"] = a.Reason.Length > 0 ? Ack999Codes.TransactionError(a.Reason) : null, ["errors"] = new JsonArray([.. a.Errors.Select(e => (JsonNode)e.Text)]),
                })]);
            else if (r.Kind == "277")
                o["claims"] = new JsonArray([.. r.Statuses.Select(c => (JsonNode)new JsonObject
                {
                    ["account"] = c.Account, ["payer_claim"] = c.PayerClaim, ["rejected"] = c.Rejected, ["statuses"] = new JsonArray([.. c.Statuses.Select(x => (JsonNode)x.Text)]),
                    ["lines"] = c.Lines.Count == 0 ? null : new JsonArray([.. c.Lines.Select(l => (JsonNode)new JsonObject { ["procedure"] = l.Procedure, ["statuses"] = new JsonArray([.. l.Statuses.Select(x => (JsonNode)x.Text)]) })]),
                })]);
            else if (r.Kind == "271")
                o["people"] = new JsonArray([.. r.Eligibility.Take(MaxResults).Select(a => (JsonNode)new JsonObject
                {
                    ["member"] = a.MemberId.Length > 0 ? a.MemberId : a.SubscriberId, ["name"] = $"{a.Last}, {a.First}", ["dependent"] = a.Dependent ? true : null,
                    ["found"] = a.Rejection.Length == 0, ["not_found"] = a.Rejection.Length > 0 ? $"AAA {a.Rejection} {Core.Eligibility.EligibilityCodes.Say(Core.Eligibility.EligibilityCodes.Reject, a.Rejection)}" : null,
                    ["active"] = a.Active, ["begins"] = a.Begins?.ToString("yyyy-MM-dd"), ["ends"] = a.Ends?.ToString("yyyy-MM-dd"),
                    ["primary_elsewhere"] = a.PrimaryElsewhere.Length > 0 ? a.PrimaryElsewhere : null,
                    ["needs_authorization"] = a.NeedsAuthorization.Count > 0 ? new JsonArray([.. a.NeedsAuthorization.Select(t => (JsonNode)$"{t} {Core.Eligibility.EligibilityCodes.Say(Core.Eligibility.EligibilityCodes.ServiceType, t)}")]) : null,
                })]);
            else
                o["claims"] = new JsonArray([.. r.Remits.Take(MaxResults).Select(c => (JsonNode)new JsonObject
                {
                    ["account"] = c.Account, ["status"] = c.StatusText, ["charge"] = c.Charge, ["paid"] = c.Paid, ["patient_share"] = c.PatientShare, ["payer_claim"] = c.PayerClaim,
                    ["adjustments"] = new JsonArray([.. c.Adjustments.Select(a => (JsonNode)$"{a.Text}: {a.Amount:0.00}")]),
                    ["remarks"] = new JsonArray([.. c.Remarks.Select(x => (JsonNode)$"{x} {ResponseCodes.Describe(ResponseCodes.Rarc, x)}")]),
                    ["lines"] = new JsonArray([.. c.Lines.Select(l => (JsonNode)new JsonObject
                    {
                        ["line"] = l.Number, ["procedure"] = l.Procedure, ["modifiers"] = new JsonArray([.. l.Modifiers.Select(m => (JsonNode)m)]), ["charge"] = l.Charge, ["paid"] = l.Paid,
                        ["units"] = l.Units, ["units_billed"] = l.OriginalUnits, ["date"] = l.Date.Length > 0 ? l.Date : null,
                        ["adjustments"] = new JsonArray([.. l.Adjustments.Select(a => (JsonNode)$"{a.Text}: {a.Amount:0.00}")]),
                        ["remarks"] = new JsonArray([.. l.Remarks.Select(x => (JsonNode)$"{x} {ResponseCodes.Describe(ResponseCodes.Rarc, x)}")]),
                    })]),
                })]);
            return o;
        }, cancel);

    [McpServerTool(Name = "replay", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Denial replay: each denial or rejection in an open response, matched to its claim in the open 837s and set against that claim's findings. Verdicts: caught (a check flagged it for the payer's reason), missed (a check or a payer rule could have, and didn't), not checkable (eligibility, medical necessity: nothing in a claim shows it), no claim (the claim isn't open).")]
    public Task<string> ReplayResponse(
        [Description("The open response: its name or path. Optional when one is open.")] string? file = null,
        CancellationToken cancel = default) =>
        Session("replayed denials", s =>
        {
            var report = LensReplay(s, ResponseOf(s, file));
            return new JsonObject
            {
                ["file"] = report.Response.Name, ["summary"] = report.Summary,
                ["denials"] = new JsonArray([.. report.Rows.Select(r => (JsonNode)new JsonObject
                {
                    ["verdict"] = Report.Verdict(r.Verdict), ["account"] = r.Denial.Account.Length > 0 ? r.Denial.Account : "batch", ["line"] = r.Denial.Line > 0 ? r.Denial.Line : null,
                    ["procedure"] = r.Denial.Procedure.Length > 0 ? r.Denial.Procedure : null, ["reason"] = r.Denial.Description, ["amount"] = r.Denial.Amount > 0 ? r.Denial.Amount : null,
                    ["caught_by"] = r.CaughtBy.Count > 0 ? new JsonArray([.. r.CaughtBy.Select(f => f.Rule.Id).Distinct().Select(x => (JsonNode)x)]) : null,
                    ["checks"] = new JsonArray([.. r.Rules.Concat(r.Kinds).Select(x => (JsonNode)x)]), ["explanation"] = r.Explanation,
                })]),
                ["draft"] = Replay.Draft(Replay.Run(ResponseOf(s, file).File, [.. s.Files.Select(f => f.Result)])) != null ? "draft_rule_pack drafts payer rules from the missed denials." : null,
            };
        }, cancel);

    [McpServerTool(Name = "draft_rule_pack", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Drafts payer rules from a response's missed denials that a payer rule would have caught (prior authorization, not covered, a filing limit, a modifier a place of service needs, unit limits), each with the evidence in its \"why\". Answers with the pack's JSON; with output, also saves it as a new file. It's a draft: the person checks it against the payer's policy and adds it (File › Add Rule Pack…); it's never added by itself.")]
    public Task<string> DraftRulePack(
        [Description("The open response: its name or path. Optional when one is open.")] string? file = null,
        [Description("A new .json file to save the draft in, relative to the working folder. Optional.")] string? output = null,
        CancellationToken cancel = default) =>
        Session("drafted a rule pack", s =>
        {
            var report = Replay.Run(ResponseOf(s, file).File, [.. s.Files.Select(f => f.Result)]);
            var draft = Replay.Draft(report) ?? throw new InvalidOperationException("Nothing to draft: no missed denial in it is one a payer rule would catch.");
            var o = new JsonObject { ["pack"] = draft };
            if (!string.IsNullOrWhiteSpace(output))
            {
                var full = _files.Resolve(output, "A rule pack", [".json"]);
                _files.CheckWritable(full);
                File.WriteAllText(full, Replay.Text(draft));
                _files.Wrote(full);
                o["wrote"] = _files.Relative(full);
            }
            o["note"] = "A draft: check each rule against the payer's policy before it's used. test_rule_pack shows what it would flag.";
            return o;
        }, cancel);

    // ───────────────────────────── writing ─────────────────────────────

    [McpServerTool(Name = "save_corrected_copy", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes a corrected copy of an open 837 as a new file: the clear fixes its findings suggest (counts, control numbers, totals, line numbers, decimal points in diagnoses) and nothing else, and lists every change. When agents see de-identified values the copy is of the de-identified file: the person makes the real one with File › Save Corrected Copy.")]
    public Task<string> SaveCorrectedCopy(
        [Description(FileHelp)] string file,
        [Description("The new file, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote a corrected copy", output, "A corrected copy", [".x12", ".edi", ".txt", ".837", ".dat"], (s, full) =>
        {
            var f = FileOf(s, file);
            var (batch, lens) = Lensed(s, f);
            var (text, changes) = Corrector.Apply(lens ? Lens(s).Apply(File.ReadAllText(f.Path)).Text : File.ReadAllText(f.Path), batch.File, f.Result.Findings);
            if (changes.Count == 0) throw new InvalidOperationException($"{batch.Name} has no findings with a clear fix.");
            File.WriteAllText(full, text);
            var after = Scrubber.Scrub(ClaimReader.Read(Path.GetFileName(full), X12File.Parse(text)), s.Settings);
            return new JsonObject
            {
                ["changes"] = new JsonArray([.. changes.Select(c => (JsonNode)new JsonObject { ["segment"] = c.SegmentIndex + 1, ["element"] = c.Element, ["was"] = c.Was, ["now"] = c.Now, ["why"] = c.Why })]),
                ["after"] = Summary(after),
                ["note"] = lens ? "This copy is de-identified (it's what you may see): the person makes the real corrected copy with File › Save Corrected Copy." : null,
            };
        }, cancel);

    [McpServerTool(Name = "export_report", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes a report of the open 837s (claims, findings, the rules behind them) and the open responses' denial replays, as a new .html page or .xlsx workbook in the working folder.")]
    public Task<string> ExportReport(
        [Description("The new file, relative to the working folder: .html or .xlsx.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote a report", output, "A report", [".html", ".htm", ".xlsx"], (s, full) =>
        {
            if (s.Files.Count == 0) throw new InvalidOperationException("No 837 is open: open_file one first.");
            var results = s.Files.Select(f => { var (b, _) = Lensed(s, f); return new ScrubResult { Batch = b, Findings = f.Result.Findings }; }).ToList();
            var replays = s.Responses.Select(r => LensReplay(s, r)).ToList();
            if (full.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) Report.Excel(full, results, replays, DateTime.Now);
            else File.WriteAllText(full, Report.Html(results, replays, DateTime.Now));
            return new JsonObject { ["files"] = results.Count, ["claims"] = results.Sum(r => r.Batch.Claims.Count), ["findings"] = results.Sum(r => r.Findings.Count), ["replays"] = replays.Count };
        }, cancel);

    [McpServerTool(Name = "deidentify_file", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false)]
    [Description("Writes a de-identified copy of an X12 file in the working folder (open or not) as a new file: names, member and account numbers, addresses, phone numbers and notes replaced with stand-ins, dates moved, the structure, codes and amounts kept. Always de-identified, even when agents may see real values: for sharing a problem claim.")]
    public Task<string> DeidentifyFile(
        [Description("The file, relative to the working folder.")] string file,
        [Description("The new file, relative to the working folder.")] string output,
        CancellationToken cancel = default) =>
        Write("wrote a de-identified copy", output, "A de-identified copy", [".x12", ".edi", ".txt", ".837", ".835", ".999", ".277", ".dat"], (_, full) =>
        {
            var d = host.Deidentifier.Load(_files.Existing(file));
            File.WriteAllText(full, d.Text);
            return new JsonObject { ["replaced"] = d.Summary };
        }, cancel);

    // ───────────────────────────── the view ─────────────────────────────

    [McpServerTool(Name = "get_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What the window shows: the file selected, the claim selected in it, and the tab.")]
    public Task<string> GetView(CancellationToken cancel = default) =>
        Session("looked at the view", s =>
        {
            var f = s.View.File is { } p ? s.Files.FirstOrDefault(x => x.Path == p) : null;
            return new JsonObject
            {
                ["file"] = s.View.File == null ? null : Path.GetFileName(s.View.File), ["claim"] = s.View.Claim > 0 ? s.View.Claim : null,
                ["account"] = f != null && s.View.Claim > 0 && s.View.Claim <= f.Result.Batch.Claims.Count ? Lensed(s, f).Batch.Claims[s.View.Claim - 1].Account : null,
                ["tab"] = s.View.Tab, ["live"] = host.Live,
            };
        }, cancel);

    [McpServerTool(Name = "show_in_view", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Shows the person something in their window: a file's batch, a claim (its loops and segments, findings highlighted), or the findings, rules, code sets or responses tab. Headless, it only remembers it (render_view draws it).")]
    public Task<string> ShowInView(
        [Description(FileHelp + " Optional for the findings, rules, code sets and responses tabs.")] string? file = null,
        [Description(ClaimHelp + " Optional.")] string? claim = null,
        [Description("batch, claim, findings, rules, code sets or responses. Default: claim when a claim is given, else batch.")] string? tab = null,
        CancellationToken cancel = default) =>
        Session("showed something", s =>
        {
            var t = (tab ?? (string.IsNullOrWhiteSpace(claim) ? "batch" : "claim")).Trim().ToLowerInvariant();
            if (t is not ("batch" or "claim" or "findings" or "rules" or "code sets" or "responses")) throw new ArgumentException("tab is batch, claim, findings, rules, code sets or responses.");
            string? path = null;
            var number = 0;
            if (!string.IsNullOrWhiteSpace(file) || t is "batch" or "claim")
            {
                var f = FileOf(s, file);
                path = f.Path;
                if (!string.IsNullOrWhiteSpace(claim)) number = ClaimOf(Lensed(s, f).Batch, claim).Number;
            }
            if (t == "claim" && number == 0) throw new ArgumentException("The claim tab needs a claim.");
            s.Show(new ViewState(path, number, t));
            return new JsonObject { ["showing"] = t, ["file"] = path == null ? null : Path.GetFileName(path), ["claim"] = number > 0 ? number : null };
        }, cancel);

    [McpServerTool(Name = "render_view", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("A picture (PNG) to show the person: a claim's segments with the ones findings point at highlighted and the findings under them, a file's claims, or a response's replay. Without arguments, what the window shows now.")]
    public async Task<CallToolResult> RenderView(
        [Description(FileHelp + " Or an open response. Optional.")] string? file = null,
        [Description(ClaimHelp + " Optional.")] string? claim = null,
        [Description("Width in pixels, from 200 to 2000. Default 1000.")] int width = 1000,
        CancellationToken cancel = default)
    {
        try
        {
            if (width is < 200 or > 2000) throw new ArgumentException("width is from 200 to 2000 pixels.");
            var picture = await host.RunAsync("drew the view", s =>
            {
                if (string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(claim)) return PictureOf(s, s.View);
                if (!string.IsNullOrWhiteSpace(file) && s.Responses.FirstOrDefault(r => Matches(r.Path, file)) is { } resp) return PictureOf(s, new ViewState(resp.Path, 0, "responses"));
                var f = FileOf(s, file);
                return PictureOf(s, new ViewState(f.Path, string.IsNullOrWhiteSpace(claim) ? 0 : ClaimOf(Lensed(s, f).Batch, claim).Number, string.IsNullOrWhiteSpace(claim) ? "batch" : "claim"));
            }, cancel).ConfigureAwait(false);
            var png = await host.RunAsync("drew the view", _ => host.Painter.Picture(picture, width / 1000.0), cancel).ConfigureAwait(false);
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = picture.Title + (picture.Subtitle.Length > 0 ? " — " + picture.Subtitle : "") }] };
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>A picture of a view: a claim's segments, a file's claims, a response's replay, or the findings.</summary>
    internal ViewPicture PictureOf(IExplorerSession s, ViewState view)
    {
        if (view.Tab == "responses" || (view.File != null && s.Responses.Any(r => r.Path == view.File)))
        {
            var resp = s.Responses.FirstOrDefault(r => r.Path == view.File) ?? s.Responses.FirstOrDefault();
            if (resp == null) return new ViewPicture("No responses open", "", [], 1, 0, 0, false, []);
            var rep = LensReplay(s, resp);
            return new ViewPicture(rep.Response.Name, rep.Summary,
                [.. rep.Rows.Select(r => $"{Report.Verdict(r.Verdict),-14} {(r.Denial.Account.Length > 0 ? r.Denial.Account : "batch"),-16} {r.Denial.Description}")], 1, 0, 0, false,
                [.. rep.Rows.Where(r => r.Verdict == Verdict.Missed).Select(r => r.Explanation)]);
        }
        var f = view.File is { } p ? s.Files.FirstOrDefault(x => x.Path == p) : s.Files.FirstOrDefault();
        if (f == null) return new ViewPicture("Nothing open", "", [], 1, 0, 0, false, []);
        var (batch, _) = Lensed(s, f);
        if (view.Claim > 0 && view.Claim <= batch.Claims.Count)
        {
            var c = batch.Claims[view.Claim - 1];
            var segs = c.Loop.All().SelectMany(l => l.Segments).OrderBy(x => x.Index).ToList();
            var findings = f.Result.Findings.Where(x => x.Claim == c.Number).ToList();
            var hit = findings.Where(x => x.SegmentIndex >= 0).Select(x => x.SegmentIndex).Order().ToList();
            return new ViewPicture($"Claim {c.Number}: {c.Account}", $"{batch.Name} · {Status(f.Result, c)} · {findings.Count} finding{(findings.Count == 1 ? "" : "s")}",
                [.. segs.Select(x => x.Text)], segs.Count > 0 ? segs[0].Index + 1 : 1, hit.Count > 0 ? hit[0] + 1 : 0, hit.Count > 0 ? hit[0] + 1 : 0, true,
                [.. findings.Select(x => $"{x.Severity} {x.Rule.Id} {x.Place}: {x.Message}")]);
        }
        return new ViewPicture(batch.Name, Summary(f.Result),
            [.. batch.Claims.Select(c => $"{c.Number,3}  {c.Account,-18} {c.Kind,-13} {c.Total,10:0.00}  {Status(f.Result, c),-6} {f.Result.Findings.Count(x => x.Claim == c.Number)} findings")], 1, 0, 0, false,
            [.. f.Result.Envelope.Select(x => $"{x.Severity} {x.Rule.Id} {x.Place}: {x.Message}")]);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Whether agents see de-identified values now, and the session's de-identifier.</summary>
    private Deidentifier Lens(IExplorerSession s) => host.Deidentifier;

    /// <summary>An open 837 as the agent may see it (its de-identified copy unless real values are allowed), and whether it's de-identified.</summary>
    private (ClaimBatch Batch, bool Lens) Lensed(IExplorerSession s, OpenFile f) =>
        s.SeeRealValues ? (f.Result.Batch, false) : (ClaimReader.Read(f.Result.Batch.Name, X12File.Parse(host.Deidentifier.Apply(File.ReadAllText(f.Path)).Text)), true);

    private ResponseFile LensResponse(IExplorerSession s, OpenResponse r) =>
        s.SeeRealValues ? r.File : ResponseReader.Read(r.File.Name, X12File.Parse(host.Deidentifier.Apply(File.ReadAllText(r.Path)).Text)) ?? r.File;

    /// <summary>A replay as the agent may see it: the de-identified response against the de-identified claims, with the findings of the real ones.</summary>
    private ReplayReport LensReplay(IExplorerSession s, OpenResponse r) =>
        Replay.Run(LensResponse(s, r), [.. s.Files.Select(f => new ScrubResult { Batch = Lensed(s, f).Batch, Findings = f.Result.Findings, Answered = f.Result.Answered })], s.Settings.CodeSets);

    private static bool Matches(string path, string name)
    {
        var n = name.Trim().Replace('\\', '/');
        return Path.GetFileName(path).Equals(Path.GetFileName(n), StringComparison.OrdinalIgnoreCase) && path.Replace('\\', '/').EndsWith(n, StringComparison.OrdinalIgnoreCase);
    }

    private static string PathOf(IExplorerSession s, string file) =>
        s.Files.Select(f => f.Path).Concat(s.Responses.Select(r => r.Path)).FirstOrDefault(p => Matches(p, file))
        ?? throw new ArgumentException($"{file} isn't open. list_open gives the open files; open_file opens one.");

    private static OpenFile FileOf(IExplorerSession s, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return s.Files.Count switch
            {
                0 => throw new InvalidOperationException("No 837 is open: list_files, then open_file."),
                1 => s.Files[0],
                _ => s.View.File is { } v && s.Files.FirstOrDefault(f => f.Path == v) is { } shown ? shown
                    : throw new ArgumentException($"{s.Files.Count} 837s are open: give file ({string.Join(", ", s.Files.Select(f => Path.GetFileName(f.Path)))})."),
            };
        return s.Files.FirstOrDefault(f => Matches(f.Path, file))
               ?? throw new ArgumentException(s.Responses.Any(r => Matches(r.Path, file)) ? $"{file} is a response, not an 837: get_response or replay read it." : $"{file} isn't open. open_file opens it.");
    }

    private static OpenResponse ResponseOf(IExplorerSession s, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return s.Responses.Count == 1 ? s.Responses[0]
                : throw new ArgumentException(s.Responses.Count == 0 ? "No response is open: open_file a 999, 277CA or 835." : $"{s.Responses.Count} responses are open: give file ({string.Join(", ", s.Responses.Select(r => r.File.Name))}).");
        return s.Responses.FirstOrDefault(r => Matches(r.Path, file)) ?? throw new ArgumentException($"{file} isn't an open response. open_file opens it.");
    }

    private static Claim ClaimOf(ClaimBatch batch, string claim)
    {
        var c = claim.Trim();
        if (int.TryParse(c, out var n) && n >= 1 && n <= batch.Claims.Count) return batch.Claims[n - 1];
        return batch.Claims.FirstOrDefault(x => x.Account.Equals(c, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"{batch.Name} has no claim {claim}: it has claims 1 to {batch.Claims.Count} (get_batch lists them).");
    }

    private static string Status(ScrubResult r, Claim c) => r.Status(c)?.ToString() ?? "Clean";

    internal static string Summary(ScrubResult r)
    {
        var claims = r.Batch.Claims;
        return $"{claims.Count} claim{(claims.Count == 1 ? "" : "s")}, {claims.Count(c => r.Status(c) == null)} clean, {claims.Count(c => r.Status(c) is Severity.Reject or Severity.Deny)} would be rejected or denied, {r.Findings.Count} finding{(r.Findings.Count == 1 ? "" : "s")}";
    }

    private static JsonObject BatchSummary(IExplorerSession s, OpenFile f) => new()
    {
        ["file"] = f.Result.Batch.Name,
        ["kind"] = f.Result.Batch.Claims.FirstOrDefault()?.Kind.ToString(),
        ["summary"] = Summary(f.Result),
        ["by_severity"] = new JsonObject(f.Result.Findings.GroupBy(x => x.Severity).OrderByDescending(g => g.Key).Select(g => KeyValuePair.Create(g.Key.ToString().ToLowerInvariant(), (JsonNode?)g.Count()))),
    };

    internal static JsonObject FindingJson(Finding f, ClaimBatch batch) => new()
    {
        ["rule"] = f.Rule.Id, ["severity"] = f.Severity.ToString(), ["title"] = f.Rule.Title,
        ["claim"] = f.Claim > 0 ? f.Claim : null, ["account"] = f.Claim > 0 && f.Claim <= batch.Claims.Count ? batch.Claims[f.Claim - 1].Account : null,
        ["line"] = f.Line > 0 ? f.Line : null, ["where"] = f.Place, ["segment"] = f.SegmentIndex >= 0 ? f.SegmentIndex + 1 : null,
        ["message"] = f.Message, ["fix"] = f.Fix?.Description,
    };

    /// <summary>Runs a call on the session (on the window's thread), with "deidentified" added to its answer.</summary>
    private async Task<string> Session(string action, Func<IExplorerSession, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, s =>
            {
                var o = work(s);
                o["deidentified"] = !s.SeeRealValues;
                return o.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>Runs a call that writes one new file in the working folder, and answers with where it went.</summary>
    private async Task<string> Write(string action, string output, string what, string[] extensions, Func<IExplorerSession, string, JsonObject> work, CancellationToken cancel)
    {
        try
        {
            var full = _files.Resolve(output, what, extensions);
            _files.CheckWritable(full);
            return await host.RunAsync(action, s =>
            {
                var o = work(s, full);
                _files.Wrote(full);
                o["wrote"] = _files.Relative(full);
                o["deidentified"] = !s.SeeRealValues;
                return o.ToJsonString(Json.Options);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    private async Task<T> Run<T>(string action, Func<IExplorerSession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or ExplorerBusyException or System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
