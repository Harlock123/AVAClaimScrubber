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

internal static class VisualExtensions
{
    public static IEnumerable<Avalonia.Visual> GetVisualDescendants(this Avalonia.Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v);
}
