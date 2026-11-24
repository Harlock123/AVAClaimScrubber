using Avalonia.Controls;

namespace AVAClaimScrubber.Tests;

/// <summary>The window, driven headless: opening files, the batch, a claim with its findings highlighted, all findings.</summary>
public sealed class WindowTests
{
    [Fact]
    public void Files_open_into_their_claims_marked_by_status() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow([Samples.Path("clinic-837p.x12"), Samples.Path("hospital-837i.x12")]);
        w.Show();
        HeadlessApp.Settle();
        Assert.Equal(2, w.OpenFiles.Count);
        var entries = ((IEnumerable<ListEntry>)w.FileList.ItemsSource!).ToList();
        Assert.Equal(2 + 7 + 2, entries.Count);
        Assert.Equal(0, w.Tabs.SelectedIndex);
        var text = string.Join("\n", w.BatchPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("hospital-837i.x12", text);
        w.Close();
    });

    [Fact]
    public void A_claim_shows_its_loops_segments_and_findings() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow([Samples.Path("clinic-837p.x12")]);
        w.Show();
        HeadlessApp.Settle();
        var file = w.OpenFiles[0];
        w.ShowClaim(file, file.Batch.Claims.Single(c => c.Account == "TOTAL-02"));
        HeadlessApp.Settle();
        Assert.Equal(1, w.Tabs.SelectedIndex);
        Assert.Equal("Claim 2: TOTAL-02", w.ClaimTitle.Text);
        Assert.StartsWith("CLM*TOTAL-02*250.00", w.Highlighted);
        var finding = Assert.Single((IEnumerable<Core.Checks.Finding>)w.ClaimFindings.ItemsSource!);
        Assert.Equal("A1", finding.Rule.Id);
        var loops = ((IEnumerable<TreeViewItem>)w.LoopTree.ItemsSource!).Select(i => ((Core.Claims.Loop)i.Tag!).Id).ToList();
        Assert.Equal(["2000A", "2000B"], loops);
        w.Close();
    });

    [Fact]
    public void The_findings_tab_lists_everything_by_severity() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow([Samples.Path("envelope-errors.x12")]);
        w.Show();
        HeadlessApp.Settle();
        var all = ((IEnumerable<(OpenBatch, Core.Checks.Finding)>)w.AllFindings.ItemsSource!).ToList();
        Assert.Equal(3, all.Count);
        Assert.StartsWith("3 findings", w.FindingsCount.Text);
        w.Close();
    });
}

public sealed class RulesWindowTests
{
    [Fact]
    public void A_rule_pack_added_changes_the_findings_and_shows_on_the_rules_tab() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow([Samples.Path("hospital-837i.x12")]);
        w.Show();
        HeadlessApp.Settle();
        Assert.Equal(Core.Checks.Severity.Deny, w.OpenFiles[0].Result.Findings.Single().Severity);
        w.AddRulePack(Samples.Path("rules/sunplan.json"));
        HeadlessApp.Settle();
        Assert.Equal(Core.Checks.Severity.Warn, w.OpenFiles[0].Result.Findings.Single().Severity);
        var text = string.Join("\n", w.RulesPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? [])));
        Assert.Contains("Sun Plan", text);
        Assert.Contains("A4: warn", text);
        Assert.Contains("Lines fall inside the statement period", text);
        Assert.Contains("1 in the open files", text);
        Assert.StartsWith(MainWindow.FolderOverride!, w.RulesFolder);
        w.AddRulePack(Samples.Path("clinic-837p.x12"));
        Assert.StartsWith("That rule pack doesn't read", w.txtStatusBar.Text);
        w.Close();
    });
}

public sealed class CorrectionWindowTests
{
    [Fact]
    public void A_corrected_copy_is_written_as_a_new_file_opened_and_checks_clean() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow([Path.Combine(root, "envelope-errors.x12")]);
        w.Show();
        HeadlessApp.Settle();
        var target = Path.Combine(root, "envelope-errors-corrected.x12");
        w.PickSaveFile = (_, _) => Task.FromResult<string?>(target);
        w.SaveCorrected().GetAwaiter().GetResult();
        HeadlessApp.Settle();
        Assert.True(File.Exists(target));
        Assert.Equal(3, w.LastChanges.Count);
        Assert.Equal(2, w.OpenFiles.Count);
        Assert.Empty(w.OpenFiles.Single(o => o.Path == target).Result.Findings);
        Assert.Contains("Now: 1 claim, 1 clean, 0 findings.", w.txtStatusBar.Text);
        // The original is untouched.
        Assert.Equal(File.ReadAllText(Samples.Path("envelope-errors.x12")), File.ReadAllText(Path.Combine(root, "envelope-errors.x12")));
        w.Close();
    });

    [Fact]
    public void The_code_sets_tab_lists_each_set_and_imports_one() => HeadlessApp.Run(() =>
    {
        var w = new MainWindow();
        w.Show();
        HeadlessApp.Settle();
        var text = string.Join("\n", w.CodeSetsPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("ICD-10-CM", text);
        Assert.Contains("CPT (licensed)", text);
        Assert.Contains("not loaded (shape check only)", text);
        w.ImportCodeSet("cpt", Samples.Path("clinic-837p.x12"));
        Assert.StartsWith("That file isn't CPT (licensed)", w.txtStatusBar.Text);
        w.Close();
    });
}

public sealed class ResponseWindowTests
{
    [Fact]
    public void A_remittance_opened_beside_its_claims_is_replayed_and_drafts_a_pack_and_a_report() => HeadlessApp.Run(() =>
    {
        var root = Samples.Copy();
        var w = new MainWindow([Path.Combine(root, "payer-837p.x12"), Path.Combine(root, "edits-837p.x12"), Path.Combine(root, "coding-errors.x12")]);
        w.Show();
        HeadlessApp.Settle();
        w.Open(Path.Combine(root, "responses", "sunplan-835.x12"));
        HeadlessApp.Settle();
        Assert.Equal(5, w.Tabs.SelectedIndex);
        Assert.Equal(3, w.OpenFiles.Count);
        Assert.Equal("Read sunplan-835.x12 (835 remittance): 10 claims, 5 denied, 7 lines unpaid, 611.00 paid. 8 denials: 3 caught by a check, 3 missed, 2 not checkable from a claim.", w.txtStatusBar.Text);
        var text = string.Join("\n", w.ResponsesPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("CO-197 Precertification/authorization/notification/pre-treatment absent.", text);
        Assert.Contains("Caught by N1", text);

        var draft = Path.Combine(root, "drafted.json");
        w.PickSaveFile = (_, _) => Task.FromResult<string?>(draft);
        w.SaveDraft(w.Replays.Single()).GetAwaiter().GetResult();
        Assert.StartsWith("Saved drafted.json with 3 drafted rules.", w.txtStatusBar.Text);
        // Added as a pack, the three missed denials are caught.
        w.AddRulePack(draft);
        HeadlessApp.Settle();
        Assert.Equal(0, w.Replays.Single().Count(Core.Responses.Verdict.Missed));

        foreach (var name in new[] { "report.html", "report.xlsx" })
        {
            var report = Path.Combine(root, name);
            w.PickSaveFile = (_, _) => Task.FromResult<string?>(report);
            w.ExportReport().GetAwaiter().GetResult();
            Assert.True(new FileInfo(report).Length > 1000);
        }
        var html = File.ReadAllText(Path.Combine(root, "report.html"));
        Assert.Contains("Denial replay: sunplan-835.x12", html);
        Assert.Contains("handle it as you handle the claims", html);
        using var book = new ClosedXML.Excel.XLWorkbook(Path.Combine(root, "report.xlsx"));
        Assert.Equal(["Summary", "Claims", "Findings", "Rules", "Denials"], book.Worksheets.Select(s => s.Name));
        Assert.Equal(9, book.Worksheet("Denials").RowsUsed().Count());

        // The payer's 271 opened too: the claims are checked against it, and the eligibility denials are caught.
        w.Open(Path.Combine(root, "responses", "sunplan-271.x12"));
        HeadlessApp.Settle();
        Assert.StartsWith("Read sunplan-271.x12 (271 eligibility answer): 5 people: 3 active, 1 not active, 1 not found.", w.txtStatusBar.Text);
        Assert.Equal(0, w.Replays.Single(r => r.Response.Kind == "835").Count(Core.Responses.Verdict.NotCheckable));
        text = string.Join("\n", w.ResponsesPanel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains("Caught by V2", text);
        Assert.Contains("The open claims are checked against its answers (layer 7): 5 claims answered about.", text);
        Assert.Contains(w.OpenFiles.SelectMany(o => o.Result.Findings), f => f.Rule.Id == "V3");
        w.Close();
    });
}

internal static class VisualExtensions
{
    public static IEnumerable<Avalonia.Visual> GetVisualDescendants(this Avalonia.Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v);
}
