using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AVAClaimScrubber.Agents.Chat;
using AVAClaimScrubber.Tests.Agents;
using static AVAClaimScrubber.Tests.Agents.FakeModelServer;

namespace AVAClaimScrubber.Tests;

/// <summary>
/// The README's pictures (CLAIMS_SHOTS set to a folder): a claim with its findings, a batch, every finding, the rules
/// with a payer's pack, the code sets (the real ones, downloaded, when CLAIMS_LIVE_DOWNLOADS=1), a remittance replayed,
/// and the AI Chat answering on a scripted local model. Off unless CLAIMS_SHOTS is set; run with --filter Screenshots.
/// </summary>
public sealed class Screenshots
{
    private static void Settle(int times = 30)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static void Save(Window w, string path)
    {
        Settle();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path);
    }

    [Fact]
    public void Main_window() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Samples.Copy();
        var w = new MainWindow(new[] { "clinic-837p.x12", "coding-errors.x12", "edits-837p.x12", "payer-837p.x12" }.Select(f => Path.Combine(root, f))) { Width = 1500, Height = 900 };
        w.AgentSettings.ChatOpen = false;
        w.Show();
        try
        {
            if (Environment.GetEnvironmentVariable("CLAIMS_LIVE_DOWNLOADS") == "1")
            {
                foreach (var invented in Directory.EnumerateFiles(w.CodeSetsFolder, "ncci-ptp*.txt")) File.Delete(invented);
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AVAClaimScrubber/1.0");
                w.Downloads = http;
                AgentTests.Wait(w.UpdateCodeSets().ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }), 600);
            }
            var clinic = w.OpenFiles[0];
            w.ShowClaim(clinic, clinic.Batch.Claims[1]);
            w.Tabs.SelectedIndex = 1;
            Save(w, Path.Combine(dir, "main-window.png"));
            var coding = w.OpenFiles[1];
            w.ShowBatch(coding);
            w.Tabs.SelectedIndex = 0;
            Save(w, Path.Combine(dir, "batch.png"));
            w.ShowClaim(coding, coding.Batch.Claims[1]);
            w.Tabs.SelectedIndex = 1;
            Save(w, Path.Combine(dir, "code-sets-claim.png"));
            w.Tabs.SelectedIndex = 2;
            Save(w, Path.Combine(dir, "findings.png"));
            w.AddRulePack(Path.Combine(root, "rules", "sunplan.json"));
            w.ShowRules();
            w.Tabs.SelectedIndex = 3;
            Save(w, Path.Combine(dir, "rules.png"));
            w.ShowCodeSets();
            w.Tabs.SelectedIndex = 4;
            Save(w, Path.Combine(dir, "code-sets.png"));
            w.Open(Path.Combine(root, "responses", "sunplan-835.x12"));
            w.Tabs.SelectedIndex = 5;
            Save(w, Path.Combine(dir, "responses.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Eligibility() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Samples.Copy();
        var w = new MainWindow(new[] { "coding-errors.x12", "edits-837p.x12", "payer-837p.x12" }.Select(f => Path.Combine(root, f))) { Width = 1500, Height = 900 };
        w.AgentSettings.ChatOpen = false;
        w.Show();
        try
        {
            w.AddRulePack(Path.Combine(root, "rules", "sunplan.json"));
            w.Open(Path.Combine(root, "responses", "sunplan-835.x12"));
            w.Open(Path.Combine(root, "responses", "sunplan-271.x12"));
            w.Tabs.SelectedIndex = 5;
            Save(w, Path.Combine(dir, "eligibility.png"));
            var payer = w.OpenFiles.Single(f => f.Name == "payer-837p.x12");
            w.ShowClaim(payer, payer.Batch.Claims[4]);
            w.Tabs.SelectedIndex = 1;
            Save(w, Path.Combine(dir, "eligibility-claim.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Drafts() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Samples.Copy();
        var w = new MainWindow(new[] { "payer-837p.x12", "payer-later-837p.x12" }.Select(f => Path.Combine(root, f))) { Width = 1500, Height = 900 };
        w.AgentSettings.ChatOpen = false;
        w.Show();
        try
        {
            foreach (var f in new[] { "sunplan-835.x12", "sunplan-835-1204.x12", "lakeshore-835-1205.x12" }) w.Open(Path.Combine(root, "responses", f));
            w.Tabs.SelectedIndex = 5;
            w.PickSaveFile = (_, name) => Task.FromResult<string?>(Path.Combine(root, name));
            w.SaveDrafts().GetAwaiter().GetResult();
            Save(w, Path.Combine(dir, "drafts.png"));
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Ai_chat() => HeadlessApp.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var root = Samples.Copy();
        var w = new MainWindow { Width = 1600, Height = 900 };
        w.ChatKeys = (new ApiKeyStore(null, null), new ApiKeyStore(null, null));
        w.AgentSettings.ChatProvider = "OpenAICompatible";
        w.AgentSettings.ChatServerUrl = "http://localhost:1234/v1";
        w.AgentSettings.ChatServerModel = "qwen3-8b";
        w.AgentSettings.WorkingFolder = root;
        w.Show();
        try
        {
            if (!w.ChatHolder.IsVisible) w.ToggleChat();
            var server = new FakeModelServer("http://localhost:1234/v1")
                .Reply(Say("I'll open the batch: every claim is checked as it opens."), Call("c1", "open_file", new { file = "coding-errors.x12" }))
                .Reply(Call("c2", "get_findings", new { file = "coding-errors.x12" }))
                .Reply(Say("Showing you claim 2."), Call("c3", "show_in_view", new { file = "coding-errors.x12", claim = "2" }))
                .Reply(Say("Eight of the nine claims would be stopped; only claim 1 is clean.\n\n" +
                           "• Claim 2: E11 is a header code, not billable. The coder picks a code under it (E1110, E1165…) from the record.\n" +
                           "• Claim 3: E1199 isn't an ICD-10-CM code.\n" +
                           "• Claim 4: the rendering provider's NPI fails its check digit: likely a typo.\n" +
                           "• Claim 5: G9975 ended 672 days before the service.\n" +
                           "• Claim 6: X9 isn't a HCPCS modifier.\n" +
                           "• Claim 7: place of service 30 isn't one of CMS's codes.\n" +
                           "• Claim 8: taxonomy 207Q0000 is too short: ten characters, ending in X.\n" +
                           "• Claim 9: 9921 isn't the shape of a CPT code: a digit is missing.\n\n" +
                           "Claims 5 and 6 would be denied after they're taken; the rest rejected at the door. The account numbers I saw are de-identified stand-ins."));
            var panel = w.Chat!;
            panel.ServerFactory = (_, key) => server.Client(key);
            panel.Input.Text = "Which claims in coding-errors.x12 would be rejected, and what should we fix?";
            panel.Send();
            AgentTests.Wait(panel.Running.ContinueWith(_ => true));
            w.Tabs.SelectedIndex = 1;
            Save(w, Path.Combine(dir, "ai-chat.png"));
            panel.SetupOpen = true;
            Save(w, Path.Combine(dir, "ai-chat-setup.png"));
        }
        finally { w.Close(); }
    });
}
