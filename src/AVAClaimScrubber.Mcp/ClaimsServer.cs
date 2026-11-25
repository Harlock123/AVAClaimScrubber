using AVAClaimScrubber.Core.X12;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVAClaimScrubber.Mcp;

/// <summary>
/// The MCP server: <see cref="ClaimsTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection), with
/// instructions that tell an agent AVA Claim Scrubber's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open window, over the network with a token, and in-process for the AI Chat.
/// </summary>
public static class ClaimsServer
{
    public const string Name = "AVAClaimScrubber";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        AVA Claim Scrubber checks healthcare claims (X12 837P professional and 837I institutional, 5010) before they're sent, the way a clearinghouse and a payer would, and reads what payers send back (999 acknowledgments, 277CA claim acknowledgments, 835 remittances). The checks are done by code, the same way every time; you explain them, look things up, and draft rules for a person to approve. The only files you write are new ones in the working folder (corrected copies, rule packs, reports, de-identified copies, steps files); you never change a rule pack or code set the person uses.

        Privacy (it matters): claims carry patients' names, member IDs, dates of birth and service, and addresses. Unless the person lets agents see real values, everything you read is de-identified first: names, member and account numbers replaced by stand-ins (the same stand-in every time in this session, in claims and responses alike), dates moved, addresses blanked; codes, amounts and the structure are kept. Every answer says "deidentified". The checks themselves run on the real files, and findings' messages never carry names, IDs or dates, so they read the same either way. Don't ask for real values, and don't try to undo the stand-ins.

        Findings (it matters):
        - Seven layers of rules: 1 the envelope and syntax (counts, control numbers, required elements, dates, amounts), 2 the loop structure (HL parents, required loops, diagnosis pointers), 3 arithmetic and dates (totals, units, future dates, statement periods, frequency codes), 4 code sets (ICD-10-CM codes exist and are billable, HCPCS in effect on the date, CPT's shape, modifiers, places of service, NPI check digits, taxonomies), 5 Medicare's edits (NCCI pairs, MUEs, age and sex), 6 payer rules from rule packs (filing limits, prior authorization, required modifiers, unit limits, procedures not covered), 7 eligibility against the payer's 271 answers when they're open (V1 coverage on the dates, V2 member found, V3 another payer primary, V4 authorization for care the 271 says needs it).       - Severities: reject (a clearinghouse or payer won't take it), deny (it'll be taken and not paid), warn, info. A claim's status is its worst finding; clean when it has none. Rule packs can turn a rule off or change its severity for a payer.
        - Each finding names its rule, where it is (claim, line, loop, element, segment number) and, when the fix is clear (a count, a total, a line number, a decimal point), the fix. explain_rule says why the rule exists and its source.
        - Code sets come from their publishers (CMS, NUCC) and only check what's loaded: get_code_sets says which. CPT belongs to the AMA: without a licensed file only its shape is checked. Don't present a code as valid or invalid from memory: lookup_code it.

        Responses: replay sets each denial (an 835's CARC and RARC codes, a 277CA's rejection, a 999's error) against the claim's findings: caught, missed, not checkable from a claim (medical necessity; eligibility unless the payer's 271 about the patient is open), or no claim open. Opening a 271 (open_file) re-checks the claims against it. draft_rule_pack turns missed denials a payer rule would catch into a draft pack (with all, from every open remittance together, one pack for each payer); the person checks it and adds it. test_rule_pack shows what a pack would flag before anyone adds it.

        A good way to work: list_files, open_file the claims (and any responses); get_batch for the claims' statuses; get_findings (worst first) and get_claim to explain one, citing the rule and where; lookup_code for any code; save_corrected_copy for the clear fixes; replay and draft_rule_pack for a remittance; export_report for the lot (.html or .xlsx). render_view shows the person a claim with its problems marked, and show_in_view selects it in their window. batch runs several calls at once; save_steps keeps a session's calls to run again on next month's files.

        Say what a finding means for the claim (rejected at the door, denied after, or a warning), and what the person should change. Never invent a payer's policy: a payer rule comes from a rule pack or a drafted one the person approves.

        The server's prompts are saved recipes (scrub a batch, fix and resend, explain a denial, replay a remittance, draft payer rules, draft them from weeks of remittances, look up codes, try a rule pack, month-end report): step-by-step plans with the exact tool calls.
        """;



    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(IExplorerHost host) => Tools(new ClaimsTools(host));

    /// <summary>The MCP tools of one set of handlers (one session's: its checkpoints and its recording).</summary>
    public static IEnumerable<McpServerTool> Tools(ClaimsTools target)
    {
        foreach (var method in typeof(ClaimsTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "AVA Claim Scrubber",
        Version = typeof(ClaimsServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(IExplorerHost host) => Configure(new McpServerOptions(), host);

    /// <summary>Fills a new session's options (ones the network door's HTTP handler made) as <see cref="Options"/> makes them.</summary>
    public static McpServerOptions Configure(McpServerOptions options, IExplorerHost host)
    {
        options.ServerInfo = Info;
        options.ServerInstructions = Instructions;
        options.ToolCollection = [];
        var target = new ClaimsTools(host);
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                var result = await next(request, cancel).ConfigureAwait(false);
                // The session's calls, as steps save_steps can keep: every call that worked.
                if (tool != null && result.IsError != true)
                    target.Record(tool.Name, request.Params!.Arguments);
                return result;
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(target)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, IExplorerHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (!tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            // A required argument that's missing: say which, and what it is (the SDK's own words are about dictionaries).
            if (tool.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                var missing = required.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0 && args?.ContainsKey(r) != true).ToList();
                if (missing.Count > 0)
                    return $"{tool.Name} needs {string.Join(" and ", missing)}: " + string.Join(" ", missing.Select(m =>
                        props.TryGetProperty(m, out var p) && p.TryGetProperty("description", out var d) ? $"{m} is {d.GetString()?.TrimEnd('.')}." : $"{m}."));
            }
            if (args == null) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : IExplorerHost
    {
        public string Root => Path.GetTempPath();
        public IViewPainter Painter => throw new InvalidOperationException("Not connected.");
        public Deidentifier Deidentifier { get; } = new();
        public bool Live => false;
        public Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected.");
    }
}
