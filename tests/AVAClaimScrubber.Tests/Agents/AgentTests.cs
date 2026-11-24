using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AVAClaimScrubber.Agents;
using AVAClaimScrubber.Agents.Chat;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.Responses;
using AVAClaimScrubber.Core.X12;
using AVAClaimScrubber.Mcp;
using ModelContextProtocol.Server;
using static AVAClaimScrubber.Tests.Agents.FakeModelServer;

namespace AVAClaimScrubber.Tests.Agents;

/// <summary>
/// Before any test runs: the AI side's settings, keys, token and saved chats go to a temporary folder, so no test
/// reads or writes the person's own (and none ever finds a real API key).
/// </summary>
internal static class AgentSettingsIsolation
{
    [ModuleInitializer]
    internal static void Isolate() =>
        AgentSettings.FolderOverride = Directory.CreateTempSubdirectory("avaclaimscrubber-agents-").FullName;
}

internal sealed class AvaloniaThread : IUiThread
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}

/// <summary>
/// The MCP server and the AI Chat, driven as agents drive them: through an in-process MCP client over the real server,
/// on Avalonia's headless platform, on a copy of the samples. De-identified unless a test says otherwise, as agents
/// are by default. The chat's models are scripted fakes of both APIs; no real model or key is ever used.
/// </summary>
public sealed class AgentTests
{
    /// <summary>Every tool the server offers. A new tool goes here, with descriptions on it and its arguments.</summary>
    internal static readonly string[] ToolNames =
    [
        "list_files", "open_file", "close_file", "list_open", "get_batch", "get_claim", "get_segments", "get_findings", "check_text",
        "list_rules", "explain_rule", "list_rule_packs", "test_rule_pack", "write_rule_pack", "get_code_sets", "lookup_code", "search_codes",
        "get_response", "replay", "draft_rule_pack", "save_corrected_copy", "export_report", "deidentify_file", "get_view", "show_in_view",
        "render_view", "batch", "get_recording", "save_steps", "run_steps", "checkpoint", "diff", "render_checkpoint", "restore_checkpoint",
    ];

    /// <summary>A key for tests, so stand-ins are the same from run to run.</summary>
    internal static readonly byte[] Key = Enumerable.Range(7, 32).Select(i => (byte)i).ToArray();

    /// <summary>A server on a copy of the samples, with no window round it, checking with the tests' code sets.</summary>
    internal sealed class Rig : IDisposable
    {
        private readonly Window _window;

        public HeadlessDesk Desk { get; }
        public string Root { get; } = Samples.Copy();
        public McpToolBridge Bridge { get; }
        public HeadlessSession Session => (HeadlessSession)Desk.Session;

        public Rig(bool real = false, ScrubSettings? settings = null)
        {
            Desk = new HeadlessDesk(null, real, settings ?? new ScrubSettings { CodeSets = Samples.CodeSets });
            var stage = new Panel();
            _window = new Window { Width = 20, Height = 20, Content = stage };
            _window.Show();
            Bridge = Wait(McpToolBridge.StartAsync(new ExplorerHost(new AvaloniaThread(), Desk, new ViewPainter(stage), Root, new Deidentifier(Key))));
        }

        public ToolOutcome Call(string tool, object? args = null) =>
            Wait(Bridge.CallAsync(tool, JsonSerializer.SerializeToElement(args ?? new { }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

        public JsonElement Ok(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.False(o.IsError, $"{tool} failed: {o.Text}");
            return o.Text.StartsWith('{') ? JsonDocument.Parse(o.Text).RootElement.Clone() : default;
        }

        public string Error(string tool, object? args = null)
        {
            var o = Call(tool, args);
            Assert.True(o.IsError, $"{tool} should have failed: {o.Text}");
            return o.Text;
        }

        public void Dispose()
        {
            Wait(Bridge.DisposeAsync().AsTask().ContinueWith(_ => true));
            _window.Close();
        }
    }

    internal static T Wait<T>(Task<T> task, int seconds = 30)
    {
        for (var i = 0; i < seconds * 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
        Assert.True(task.IsCompleted, "It didn't finish in time.");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    private static void WithRig(Action<Rig> test, bool real = false) => HeadlessApp.Run(() =>
    {
        using var rig = new Rig(real);
        test(rig);
    });

    private static IEnumerable<string> Strings(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!);

    /// <summary>The 837 and response samples.</summary>
    internal static readonly string[] Claims = ["clinic-837p.x12", "hospital-837i.x12", "envelope-errors.x12", "structure-errors.x12", "coding-errors.x12", "edits-837p.x12", "payer-837p.x12"];
    internal static readonly string[] Responses = ["responses/structure-999.x12", "responses/envelope-999.x12", "responses/clinic-277ca.x12", "responses/sunplan-835.x12"];

    // ───────────────────────────── the tools ─────────────────────────────

    [Fact]
    public void The_tools_are_the_ones_listed_and_each_says_what_it_and_its_arguments_are() => WithRig(rig =>
    {
        var tools = Wait(rig.Bridge.ListToolsAsync());
        Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        foreach (var t in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Name);
            if (t.InputSchema.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                {
                    Assert.True(p.Value.TryGetProperty("description", out _), $"{t.Name}.{p.Name} has no description.");
                    Assert.Matches("^[a-z_]+$", p.Name);
                }
        }
        Assert.Contains("de-identified", ClaimsServer.Instructions);
        Assert.Contains("unknown_tool", rig.Error("unknown_tool").Replace("\"", ""));
        Assert.Contains("has no argument \"nope\"", rig.Error("list_files", new { nope = 1 }));
    });

    [Fact]
    public void Files_are_listed_opened_and_their_claims_and_findings_read() => WithRig(rig =>
    {
        var files = rig.Ok("list_files").GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("file").GetString()!, f => f.GetProperty("kind").GetString());
        Assert.Equal("837P claims", files["clinic-837p.x12"]);
        Assert.Equal("837I claims", files["hospital-837i.x12"]);
        Assert.Equal("835 remittance", files["responses/sunplan-835.x12"]);
        Assert.Equal("999 acknowledgment", files["responses/structure-999.x12"]);

        var opened = rig.Ok("open_file", new { file = "clinic-837p.x12" });
        Assert.Equal("7 claims, 1 clean, 6 would be rejected or denied, 6 findings", opened.GetProperty("summary").GetString());
        Assert.True(opened.GetProperty("deidentified").GetBoolean());
        Assert.Contains("isn't an 837", rig.Error("open_file", new { file = "rules/sunplan.json" }));

        var batch = rig.Ok("get_batch");
        var claims = batch.GetProperty("claims").EnumerateArray().ToList();
        Assert.Equal(7, claims.Count);
        Assert.Equal("Reject", claims[1].GetProperty("status").GetString());
        var account = claims[1].GetProperty("account").GetString()!;
        Assert.NotEqual("TOTAL-02", account);

        var claim = rig.Ok("get_claim", new { claim = account });
        Assert.Equal(2, claim.GetProperty("claim").GetInt32());
        Assert.Equal("SUN01", claim.GetProperty("payer_id").GetString());
        Assert.Equal("E119", claim.GetProperty("diagnoses")[0].GetProperty("code").GetString());
        Assert.Equal("99214", claim.GetProperty("lines")[0].GetProperty("procedure").GetString());
        Assert.NotEqual("20261104", claim.GetProperty("lines")[0].GetProperty("date").GetString());
        var finding = Assert.Single(claim.GetProperty("findings").EnumerateArray());
        Assert.Equal("A1", finding.GetProperty("rule").GetString());
        Assert.Equal("Set CLM02 to 225.00", finding.GetProperty("fix").GetString());

        var segs = rig.Ok("get_segments", new { claim = "2" }).GetProperty("segments").EnumerateArray().ToList();
        Assert.StartsWith("CLM*" + account + "*250.00", segs[0].GetProperty("text").GetString());
        Assert.Equal("A1", segs[0].GetProperty("findings").GetString());
        var parents = rig.Ok("get_segments", new { claim = "2", include_parents = true }).GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("text").GetString()!).ToList();
        Assert.Contains(parents, s => s.StartsWith("NM1*IL*1*") && !s.Contains("BROOKS"));

        rig.Ok("open_file", new { file = "hospital-837i.x12" });
        Assert.Contains("2 837s are open", rig.Error("get_batch"));
        var worst = rig.Ok("get_findings", new { severity = "reject" });
        Assert.All(worst.GetProperty("findings").EnumerateArray(), f => Assert.Equal("Reject", f.GetProperty("severity").GetString()));
        Assert.Equal(1, rig.Ok("get_findings", new { file = "hospital-837i.x12" }).GetProperty("count").GetInt32());
        Assert.Equal(0, rig.Ok("get_findings", new { rule = "A1", file = "hospital-837i.x12" }).GetProperty("count").GetInt32());

        var text = File.ReadAllText(Path.Combine(rig.Root, "clinic-837p.x12")).Replace("CLM*TOTAL-02*250.00", "CLM*TOTAL-02*225.00");
        var check = rig.Ok("check_text", new { text });
        Assert.DoesNotContain(check.GetProperty("findings").EnumerateArray(), f => f.GetProperty("rule").GetString() == "A1");

        Assert.Equal("closed", rig.Ok("close_file", new { file = "hospital-837i.x12" }).EnumerateObject().First().Name);
        Assert.Single(rig.Session.Files);
    });

    [Fact]
    public void Rules_and_codes_are_explained_and_looked_up() => WithRig(rig =>
    {
        Assert.Contains(rig.Ok("list_rules", new { layer = 4 }).GetProperty("rules").EnumerateArray(), r => r.GetProperty("rule").GetString() == "C7");
        var rule = rig.Ok("explain_rule", new { rule = "N1" });
        Assert.Contains("CMS NCCI PTP", rule.GetProperty("source").GetString());
        Assert.Contains(Strings(rule, "answers_to"), a => a.StartsWith("CARC 97:"));
        Assert.Contains("no rule", rig.Error("explain_rule", new { rule = "Z9" }));

        var header = rig.Ok("lookup_code", new { code = "E11" });
        Assert.Equal("icd", header.GetProperty("set").GetString());
        Assert.False(header.GetProperty("billable").GetBoolean());
        Assert.Contains("E1165", Strings(header, "billable_codes_under_it"));
        Assert.Contains("without the decimal point", rig.Ok("lookup_code", new { code = "F32.9" }).GetProperty("note").GetString());
        Assert.Equal("2024-12-31", rig.Ok("lookup_code", new { code = "G9975" }).GetProperty("terminated").GetString());
        Assert.False(rig.Ok("lookup_code", new { code = "1234567894" }).GetProperty("check_digit_ok").GetBoolean());
        Assert.Equal("Family Medicine Physician", rig.Ok("lookup_code", new { code = "207Q00000X" }).GetProperty("description").GetString());
        var carc = rig.Ok("lookup_code", new { code = "CO-197" });
        Assert.Equal("Contractual obligation", carc.GetProperty("group").GetString());
        Assert.Equal("prior-auth", Strings(carc, "payer_rule_kinds").Single());
        Assert.Contains("eligibility", rig.Ok("lookup_code", new { code = "31", set = "carc" }).GetProperty("not_checkable").GetString());
        Assert.Equal("Office", rig.Ok("lookup_code", new { code = "11", set = "pos" }).GetProperty("description").GetString());
        Assert.Contains("E1165", rig.Ok("search_codes", new { text = "type 2 diabetes" }).GetProperty("codes").EnumerateArray().Select(c => c.GetProperty("code").GetString()));
        Assert.Contains("HCPCS Level II", rig.Ok("get_code_sets").GetProperty("loaded").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        rig.Ok("open_file", new { file = "payer-837p.x12" });
        var pack = File.ReadAllText(Path.Combine(rig.Root, "rules", "sunplan.json"));
        var tried = rig.Ok("test_rule_pack", new { pack });
        Assert.Equal(3, tried.GetProperty("changes").GetArrayLength());
        Assert.Contains("kind is", rig.Error("test_rule_pack", new { pack = """{"format":"avaclaimscrubber.rules","name":"x","rules":[{"kind":"late"}]}""" }));
        Assert.Empty(rig.Ok("list_rule_packs").GetProperty("packs").EnumerateArray());
    });

    [Fact]
    public void Responses_are_read_replayed_and_drafted_from() => WithRig(rig =>
    {
        foreach (var f in new[] { "payer-837p.x12", "edits-837p.x12", "coding-errors.x12", "responses/sunplan-835.x12" }) rig.Ok("open_file", new { file = f });
        var response = rig.Ok("get_response");
        Assert.Equal("SUNPLAN", response.GetProperty("payer").GetString());
        var first = response.GetProperty("claims")[0];
        Assert.NotEqual("LATE-01", first.GetProperty("account").GetString());
        Assert.Equal("CO-29 The time limit for filing has expired.: 125.00", first.GetProperty("lines")[0].GetProperty("adjustments")[0].GetString());

        var replay = rig.Ok("replay");
        Assert.Equal("8 denials: 3 caught by a check, 3 missed, 2 not checkable from a claim.", replay.GetProperty("summary").GetString());
        var rows = replay.GetProperty("denials").EnumerateArray().ToList();
        Assert.Equal(first.GetProperty("account").GetString(), rows[0].GetProperty("account").GetString());
        Assert.Equal(["Missed", "Missed", "Missed", "Caught", "Caught", "Caught", "Not checkable", "Not checkable"], rows.Select(r => r.GetProperty("verdict").GetString()));

        var draft = rig.Ok("draft_rule_pack", new { output = "drafts/sunplan.json" });
        Assert.Equal(3, draft.GetProperty("pack").GetProperty("rules").GetArrayLength());
        Assert.Equal("drafts/sunplan.json", draft.GetProperty("wrote").GetString());
        var pack = File.ReadAllText(Path.Combine(rig.Root, "drafts", "sunplan.json"));
        Assert.Equal(3, rig.Ok("test_rule_pack", new { pack, file = "payer-837p.x12" }).GetProperty("changes").GetArrayLength());
        Assert.Contains("isn't an open response", rig.Error("replay", new { file = "payer-837p.x12" }));
    });

    [Fact]
    public void New_files_are_written_inside_the_folder_and_never_over_anything_there() => WithRig(rig =>
    {
        rig.Ok("open_file", new { file = "envelope-errors.x12" });
        var fixedCopy = rig.Ok("save_corrected_copy", new { file = "envelope-errors.x12", output = "out/fixed.x12" });
        Assert.Equal(3, fixedCopy.GetProperty("changes").GetArrayLength());
        Assert.StartsWith("1 claim, 1 clean", fixedCopy.GetProperty("after").GetString());
        Assert.Contains("de-identified", fixedCopy.GetProperty("note").GetString());
        Assert.Contains("already exists", rig.Error("save_corrected_copy", new { file = "envelope-errors.x12", output = "clinic-837p.x12" }));
        Assert.Contains("outside", rig.Error("save_corrected_copy", new { file = "envelope-errors.x12", output = "../outside.x12" }));

        rig.Ok("open_file", new { file = "responses/envelope-999.x12" });
        rig.Ok("export_report", new { output = "out/report.html" });
        Assert.Contains("Denial replay: envelope-999.x12", File.ReadAllText(Path.Combine(rig.Root, "out", "report.html")));
        rig.Ok("export_report", new { output = "out/report.xlsx" });
        Assert.Contains("A report", rig.Error("export_report", new { output = "out/report.pdf" }));

        var copy = rig.Ok("deidentify_file", new { file = "clinic-837p.x12", output = "out/shared.x12" });
        Assert.Contains("names", copy.GetProperty("replaced").GetString());
        Assert.DoesNotContain("MARTINEZ", File.ReadAllText(Path.Combine(rig.Root, "out", "shared.x12")));

        rig.Ok("write_rule_pack", new { pack = """{"format":"avaclaimscrubber.rules","name":"Mine","payers":["SUN01"],"rules":[{"id":"t","kind":"timely-filing","days":60}]}""", output = "out/mine.json" });
        Assert.Equal("Mine", RulePack.Load(Path.Combine(rig.Root, "out", "mine.json")).Name);
        Assert.Contains("needs \"days\"", rig.Error("write_rule_pack", new { pack = """{"format":"avaclaimscrubber.rules","name":"x","rules":[{"kind":"timely-filing"}]}""", output = "out/bad.json" }));
        Assert.False(File.Exists(Path.Combine(rig.Root, "out", "bad.json")));
    });

    [Fact]
    public void The_view_is_drawn_for_the_agent_and_shown_and_remembered() => WithRig(rig =>
    {
        rig.Ok("open_file", new { file = "coding-errors.x12" });
        var shot = rig.Call("render_view", new { claim = "2" });
        Assert.False(shot.IsError, shot.Text);
        Assert.Single(shot.Images);
        Assert.Contains("Claim 2", shot.Text);
        rig.Ok("show_in_view", new { claim = "4" });
        var view = rig.Ok("get_view");
        Assert.Equal("coding-errors.x12", view.GetProperty("file").GetString());
        Assert.Equal(4, view.GetProperty("claim").GetInt32());
        Assert.Equal("claim", view.GetProperty("tab").GetString());
        Assert.NotEqual("NPI-04", view.GetProperty("account").GetString());
        Assert.Single(rig.Call("render_view").Images);
        rig.Ok("open_file", new { file = "responses/clinic-277ca.x12" });
        Assert.Single(rig.Call("render_view", new { file = "clinic-277ca.x12" }).Images);
        Assert.Contains("needs a claim", rig.Error("show_in_view", new { tab = "claim" }));
    });

    [Fact]
    public void Batches_steps_files_and_checkpoints_work() => WithRig(rig =>
    {
        rig.Ok("open_file", new { file = "coding-errors.x12" });
        var batch = rig.Ok("batch", new { steps = new object[] { new { tool = "get_claim", args = new { claim = "2" } }, new { tool = "lookup_code", args = new { code = "E11" } } } });
        Assert.Equal(2, batch.GetProperty("answers").GetArrayLength());
        Assert.Contains("can't be in a batch", rig.Error("batch", new { steps = new object[] { new { tool = "show_in_view", args = new { claim = "1" } } } }));
        Assert.Contains("Step 1 (get_claim) failed", rig.Error("batch", new { steps = new object[] { new { tool = "get_claim", args = new { claim = "99" } } } }));

        Assert.Contains(rig.Ok("get_recording").GetProperty("steps").EnumerateArray(), s => s.GetProperty("tool").GetString() == "open_file");
        rig.Ok("save_steps", new
        {
            path = "steps/check",
            steps = new object[] { new { tool = "open_file", args = new { file = "${file}" } }, new { tool = "get_batch", args = new { file = "${file}" } } },
            parameters = new { file = "clinic-837p.x12" },
            description = "Open a batch and list its claims.",
        });
        var again = rig.Ok("run_steps", new { path = "steps/check.avasteps.json", values = new { file = "edits-837p.x12" } });
        Assert.Equal(6, again.GetProperty("answers")[1].GetProperty("answer").GetProperty("claims").GetArrayLength());

        rig.Ok("checkpoint", new { name = "start" });
        rig.Ok("open_file", new { file = "payer-837p.x12" });
        rig.Ok("show_in_view", new { file = "payer-837p.x12", claim = "1" });
        rig.Ok("deidentify_file", new { file = "payer-837p.x12", output = "out/p.x12" });
        var diff = rig.Ok("diff", new { since = "start" });
        Assert.Equal(["out/p.x12"], Strings(diff, "added"));
        Assert.Contains("opened payer-837p.x12", Strings(diff, "changed_elements"));
        Assert.Single(rig.Call("render_checkpoint", new { name = "start" }).Images);
        var back = rig.Ok("restore_checkpoint", new { name = "start" });
        Assert.Contains("kept", back.GetProperty("note").GetString());
        Assert.DoesNotContain(rig.Session.Files, f => f.Path.EndsWith("payer-837p.x12"));
        Assert.Contains("no checkpoint", rig.Error("diff", new { since = "nope" }));
    });

    [Fact]
    public void With_real_values_allowed_agents_see_the_files_as_they_are() => WithRig(rig =>
    {
        rig.Ok("open_file", new { file = "clinic-837p.x12" });
        var b = rig.Ok("get_batch");
        Assert.False(b.GetProperty("deidentified").GetBoolean());
        Assert.Equal("TOTAL-02", b.GetProperty("claims")[1].GetProperty("account").GetString());
        rig.Session.SeeRealValues = false;
        Assert.NotEqual("TOTAL-02", rig.Ok("get_batch").GetProperty("claims")[1].GetProperty("account").GetString());
    }, real: true);

    // ───────────────────────────── protected health information ─────────────────────────────

    /// <summary>
    /// Every real value in the samples that de-identifying replaces: patients' and subscribers' names, member IDs,
    /// account numbers, payer claim numbers, authorization numbers, addresses, birth dates and service dates (but not a
    /// date that isn't a real date, which can't be moved, or the batch's own creation date).
    /// </summary>
    internal static HashSet<string> Phi()
    {
        var phi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            v = v?.Trim();
            if (!string.IsNullOrEmpty(v) && v.Length >= 2) phi.Add(v);
        }
        void Date(string? v)
        {
            foreach (var part in (v ?? "").Split('-'))
                if (Parse.Date(part) != null) Add(part);
        }
        foreach (var name in Claims.Concat(Responses))
        {
            var file = X12File.Load(Samples.Path(name));
            var person = false;
            var level = "";
            foreach (var s in file.Segments)
            {
                switch (s.Id)
                {
                    case "HL": level = s[3].Trim(); break;
                    case "NM1":
                        person = s[1].Trim() is "IL" or "QC";
                        if (person) { Add(s[3]); Add(s[4]); Add(s[9]); }
                        break;
                    case "N3" when person: Add(s[1]); break;
                    case "N4" when person: Add(s[3]); break;
                    // De-identifying keeps a birth date's year (Safe Harbor allows it) and writes it as January 1, so a
                    // real birth date on January 1 reads the same: it isn't a leak.
                    case "DMG" when !s[2].Trim().EndsWith("0101", StringComparison.Ordinal): Date(s[2]); break;
                    case "DTP": Date(s[3]); break;
                    case "DTM" when s[1].Trim() == "472": Date(s[2]); break;
                    case "CLM": Add(s[1]); break;
                    case "CLP": Add(s[1]); Add(s[7]); break;
                    case "TRN" when s[1].Trim() == "2" && level == "PT": Add(s[2]); break;
                    case "REF" when s[1].Trim() is "1K" or "G1" or "F8": Add(s[2]); break;
                }
            }
        }
        return phi;
    }

    [Fact]
    public void The_list_of_real_values_the_no_leak_test_looks_for_covers_every_kind()
    {
        var phi = Phi();
        foreach (var real in new[] { "MARTINEZ", "ELENA", "SUN100001", "SUN700003", "TOTAL-02", "CODES-CLEAN-01", "SP8350004", "SP2770002", "AUTH0042", "12 ELM STREET", "62704", "19700312", "20261103", "20261020" })
            Assert.Contains(real, phi);
        Assert.DoesNotContain("20261131", phi);
        Assert.DoesNotContain("OKAFOR", phi);
        Assert.True(phi.Count > 60, $"{phi.Count}");
    }

    [Fact]
    public void No_tool_result_carries_a_real_name_identifier_address_or_date_when_values_are_de_identified() => WithRig(rig =>
    {
        var phi = Phi();
        var seen = new List<(string Call, string Text)>();
        void Check(string tool, object? args = null)
        {
            var o = rig.Call(tool, args);
            Assert.False(o.IsError, $"{tool} {JsonSerializer.Serialize(args)}: {o.Text}");
            seen.Add(($"{tool} {JsonSerializer.Serialize(args)}", o.Text));
        }

        Check("list_files");
        foreach (var f in Claims.Concat(Responses)) Check("open_file", new { file = f });
        Check("list_open");
        foreach (var f in Claims)
        {
            Check("get_batch", new { file = f });
            Check("get_segments", new { file = f });
            Check("get_findings", new { file = f });
            for (var n = 1; n <= Samples.Load(f).Claims.Count; n++)
            {
                Check("get_claim", new { file = f, claim = n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                Check("get_segments", new { file = f, claim = n.ToString(System.Globalization.CultureInfo.InvariantCulture), include_parents = true });
                Check("render_view", new { file = f, claim = n.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            Check("render_view", new { file = f });
        }
        Check("get_findings");
        foreach (var f in Responses)
        {
            Check("get_response", new { file = f });
            Check("replay", new { file = f });
            Check("render_view", new { file = f });
        }
        Check("draft_rule_pack", new { file = "responses/sunplan-835.x12", output = "leak/draft.json" });
        Check("test_rule_pack", new { pack = File.ReadAllText(Path.Combine(rig.Root, "rules", "sunplan.json")) });
        Check("save_corrected_copy", new { file = "clinic-837p.x12", output = "leak/fixed.x12" });
        Check("save_corrected_copy", new { file = "structure-errors.x12", output = "leak/fixed2.x12" });
        Check("export_report", new { output = "leak/report.html" });
        Check("deidentify_file", new { file = "hospital-837i.x12", output = "leak/copy.x12" });
        Check("show_in_view", new { file = "clinic-837p.x12", claim = "2" });
        Check("get_view");
        Check("batch", new { steps = new object[] { new { tool = "get_claim", args = new { file = "payer-837p.x12", claim = "1" } }, new { tool = "replay", args = new { file = "responses/clinic-277ca.x12" } } } });
        Check("get_recording");
        Check("checkpoint", new { name = "x" });
        Check("diff", new { since = "x" });

        var written = Directory.EnumerateFiles(Path.Combine(rig.Root, "leak"), "*", SearchOption.AllDirectories).Select(p => ($"file {Path.GetFileName(p)}", File.ReadAllText(p)));
        var leaks = new List<string>();
        // Whole words only: a short first name (LAN, JO) is inside other words (SUNPLAN, JOB) without being a leak.
        var words = phi.Select(real => (real, new System.Text.RegularExpressions.Regex($"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(real)}(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase))).ToList();
        foreach (var (call, text) in seen.Concat(written))
            foreach (var (real, word) in words)
                if (word.IsMatch(text))
                    leaks.Add($"{call}: \"{real}\"");
        Assert.True(seen.Count > 150, $"Only {seen.Count} calls were made.");
        Assert.All(seen.Where(s => s.Text.StartsWith('{')), s => Assert.DoesNotContain("\"deidentified\":false", s.Text));
        Assert.True(leaks.Count == 0, $"{leaks.Count} leaks:\n" + string.Join("\n", leaks.Distinct().Take(60)));
    });

    // ───────────────────────────── recipes ─────────────────────────────

    [Fact]
    public void Every_recipe_runs_call_by_call_on_de_identified_files() => HeadlessApp.Run(() =>
    {
        var recipes = typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null).ToList();
        Assert.Equal(8, recipes.Count);
        foreach (var recipe in recipes)
        {
            using var rig = new Rig();
            var text = (string)recipe.Invoke(null, recipe.GetParameters().Select(_ => (object?)null).ToArray())!;
            var calls = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).ToList();
            Assert.True(calls.Count >= 2, recipe.Name);
            foreach (var call in calls)
            {
                var rest = call[Recipes.CallMarker.Length..];
                var space = rest.IndexOf(' ');
                var tool = rest[..space];
                Assert.Contains(tool, ToolNames);
                var args = JsonDocument.Parse(rest[(space + 1)..]).RootElement;
                var outcome = Wait(rig.Bridge.CallAsync(tool, args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
                Assert.False(outcome.IsError, $"{recipe.Name}: {tool} failed: {outcome.Text}");
            }
        }
    });

    // ───────────────────────────── the chat ─────────────────────────────

    [Fact]
    public void A_chat_on_a_local_openai_compatible_model_reads_shows_and_its_reply_can_be_undone() => WithRig(rig =>
    {
        var server = new FakeModelServer("http://localhost:1234/v1")
            .Reply(Say("I'll open the batch."), Call("c1", "open_file", new { file = "clinic-837p.x12" }))
            .Reply(Call("c2", "show_in_view", new { claim = "2" }))
            .Reply(Say("Claim 2's total is 250.00 but its lines add up to 225.00."));
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.OpenAICompatible, Server = server.Client(), ServerModel = "qwen/qwen3-8b" };
        Wait(chat.SendAsync("which claims would be rejected?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        Assert.Equal(["open_file", "show_in_view"], chat.Entries.OfType<ToolEntry>().Select(t => t.Name));
        Assert.All(chat.Entries.OfType<ToolEntry>(), t => Assert.False(t.IsError, t.Result));
        var system = (string?)server.Requests[0]["messages"]![0]!["content"];
        Assert.Contains("AVA Claim Scrubber", system);
        Assert.Contains("de-identified", system);
        Assert.Contains("\"deidentified\":true", (string?)server.Requests[1]["messages"]!.AsArray().Last()!["content"]);
        Assert.Equal(2, rig.Session.View.Claim);

        var undo = Assert.Single(chat.Entries.OfType<ReplyUndoEntry>());
        Assert.True(Wait(chat.UndoReplyAsync(undo)));
        Assert.Equal(0, rig.Session.View.Claim);
        Assert.Empty(rig.Session.Files);
    });

    [Fact]
    public void A_chat_on_a_local_server_that_speaks_the_messages_api_uses_the_core_tools() => WithRig(rig =>
    {
        var claude = new FakeMessagesServer()
            .Reply(new FakeMessagesServer.ToolUse("t1", "lookup_code", new { code = "CO-197" }))
            .Reply(new FakeMessagesServer.Words("CO-197 means the authorization was missing."));
        var client = new Anthropic.AnthropicClient { ApiKey = "local", BaseUrl = "http://localhost:1234", HttpClient = new HttpClient(claude) };
        var chat = new ChatSession(rig.Bridge) { Provider = ChatProvider.Anthropic, Client = client, Model = "qwen/qwen3-8b", ClaudeServer = "localhost:1234", ClaudeCoreToolsOnly = true, ClaudeSeesPictures = false };
        Wait(chat.SendAsync("what does CO-197 mean?").ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

        Assert.Equal("http://localhost:1234/v1/messages", claude.Urls[0]);
        var offered = claude.Requests[0]["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Equal(ChatTools.Core.Order(), offered.Order());
        Assert.Contains("core set", (string?)claude.Requests[0]["system"]![0]!["text"]);
        var call = Assert.Single(chat.Entries.OfType<ToolEntry>());
        Assert.False(call.IsError, call.Result);
        Assert.Contains("Precertification", call.Result);
        Assert.Empty(chat.Entries.OfType<ReplyUndoEntry>());
    });
}
