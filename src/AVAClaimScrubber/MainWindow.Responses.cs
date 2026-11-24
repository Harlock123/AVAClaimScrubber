using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.Reports;
using AVAClaimScrubber.Core.Responses;

namespace AVAClaimScrubber;

/// <summary>
/// The Responses tab: 999s, 277CAs and 835s opened beside the claims, each replayed against the findings (which denials
/// a check would have stopped, which it missed, which no check could), a draft rule pack from the missed ones, and
/// reports of the lot.
/// </summary>
public sealed partial class MainWindow
{
    public StackPanel ResponsesPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 6 };

    private readonly List<(string Path, ResponseFile File)> _responses = [];

    public IReadOnlyList<ReplayReport> Replays { get; private set; } = [];

    partial void OpenedOther(string path, ClaimBatch batch)
    {
        if (ResponseReader.Read(Path.GetFileName(path), batch.File) is not { } response) return;
        _responses.RemoveAll(r => r.Path == path);
        _responses.Add((path, response));
        // A 271's answers are checks' input (layer 7): the open claims are checked again with them.
        if (response.Kind == "271") Recheck();
        ShowResponses();
        Tabs.SelectedIndex = 5;
        if (response.Kind == "271")
        {
            var answered = _open.Sum(o => o.Result.Answered.Count);
            Say($"Read {response.Name} ({response.KindName}): {response.Summary}. The open claims are checked against it: {answered} answered about.");
            return;
        }
        var replay = Replays.First(r => r.Response == response);
        Say($"Read {response.Name} ({response.KindName}): {response.Summary}. {replay.Summary}");
    }

    partial void Rechecked() => ShowResponses();

    /// <summary>Replays every open response against the open claims' findings, and shows them.</summary>
    public void ShowResponses()
    {
        var results = _open.Select(o => o.Result).ToList();
        Replays = [.. _responses.Select(r => Replay.Run(r.File, results, CodeSets))];
        ResponsesPanel.Children.Clear();
        ResponsesPanel.Children.Add(new TextBlock { Text = "Responses", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        if (Replays.Count == 0)
        {
            ResponsesPanel.Children.Add(new TextBlock
            {
                Text = "Open a payer's 999, 277CA or 835 (File › Open) beside the claims it answers. Each denial or rejection is matched to its claim " +
                       "(by CLM01; a 999 by its control numbers) and set against the findings: caught by a check, missed, or not checkable from a claim. " +
                       "Open the payer's 271 eligibility answers too, and the claims are checked against them (layer 7), so eligibility denials can be caught.",
                TextWrapping = TextWrapping.Wrap, Foreground = Look.Dim, FontSize = 12.5, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left,
            });
            return;
        }
        foreach (var rep in Replays)
        {
            var head = new WrapPanel { Margin = new Thickness(0, 14, 0, 2) };
            head.Children.Add(new TextBlock { Text = rep.Response.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Look.Text, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var answers = rep.Response.Kind == "271";
            if (!answers && Replay.Draft(rep) != null)
            {
                var draft = new Button { Content = "Draft Rule Pack…", Margin = new Thickness(0, 0, 8, 0) };
                var r = rep;
                draft.Click += async (_, _) => await SaveDraft(r);
                head.Children.Add(draft);
            }
            var remove = new Button { Content = "Close" };
            var path = _responses.First(x => x.File == rep.Response).Path;
            remove.Click += (_, _) =>
            {
                _responses.RemoveAll(x => x.Path == path);
                if (answers) Recheck();
                ShowResponses();
            };
            head.Children.Add(remove);
            ResponsesPanel.Children.Add(head);
            ResponsesPanel.Children.Add(new TextBlock { Text = $"{rep.Response.KindName}{(rep.Response.Payer.Length > 0 ? " from " + rep.Response.Payer : "")}: {rep.Response.Summary}.", Foreground = Look.Dim, FontSize = 12.5 });
            if (answers)
            {
                var checkedNow = _open.Sum(o => o.Result.Answered.Count);
                ResponsesPanel.Children.Add(new TextBlock { Text = $"The open claims are checked against its answers (layer 7): {checkedNow} claim{(checkedNow == 1 ? "" : "s")} answered about.", Foreground = Look.Clean, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                continue;
            }
            ResponsesPanel.Children.Add(new TextBlock { Text = rep.Summary, Foreground = rep.Count(Verdict.Missed) > 0 ? Look.Deny : Look.Clean, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            foreach (var row in rep.Rows) ResponsesPanel.Children.Add(RowView(row));
        }
    }

    private Control RowView(ReplayRow row)
    {
        var brush = row.Verdict switch { Verdict.Caught => Look.Clean, Verdict.Missed => Look.Reject, _ => Look.Dim };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,200,*"), Margin = new Thickness(0, 4, 0, 0) };
        var verdict = new TextBlock { Text = Report.Verdict(row.Verdict), Foreground = brush, FontWeight = FontWeight.SemiBold, FontSize = 12.5 };
        var claim = new TextBlock
        {
            Text = (row.Denial.Account.Length > 0 ? row.Denial.Account : "batch") + (row.Denial.Line > 0 ? $" · line {row.Denial.Line}" : ""),
            FontFamily = Look.Mono, FontSize = 12, Foreground = row.Claim != null ? Look.Accent : Look.Code,
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = row.Denial.Description, TextWrapping = TextWrapping.Wrap, Foreground = Look.Text, FontSize = 12.5 });
        text.Children.Add(new TextBlock { Text = row.Explanation, TextWrapping = TextWrapping.Wrap, Foreground = Look.Heading, FontSize = 12 });
        Grid.SetColumn(claim, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(verdict);
        grid.Children.Add(claim);
        grid.Children.Add(text);
        if (row.Claim != null && _open.FirstOrDefault(o => o.Result == row.Result) is { } file)
        {
            claim.Cursor = new Cursor(StandardCursorType.Hand);
            claim.PointerPressed += (_, _) => { ShowClaim(file, row.Claim); Tabs.SelectedIndex = 1; };
        }
        return grid;
    }

    /// <summary>Saves the rule pack drafted from a response's missed denials, for the person to check and then add.</summary>
    public async Task SaveDraft(ReplayReport report)
    {
        if (Replay.Draft(report) is not { } draft) { Say("Nothing to draft: no missed denial a payer rule would catch."); return; }
        var path = await PickSaveFile("Save the drafted rule pack", Path.GetFileNameWithoutExtension(report.Response.Name) + "-drafted-rules.json");
        if (path == null) return;
        try
        {
            File.WriteAllText(path, Replay.Text(draft));
            Say($"Saved {Path.GetFileName(path)} with {draft["rules"]!.AsArray().Count} drafted rules. Check each against the payer's policy, then File › Add Rule Pack… to use it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say("Couldn't save the draft: " + e.Message);
        }
    }

    /// <summary>Writes a report of every open file (and response replay): .html, or .xlsx for a workbook.</summary>
    public async Task ExportReport()
    {
        if (_open.Count == 0) { Say("Open some claims first."); return; }
        var path = await PickSaveFile("Export a report (.html or .xlsx)", "claim-scrub-report.html");
        if (path == null) return;
        try
        {
            var results = _open.Select(o => o.Result).ToList();
            if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) Report.Excel(path, results, Replays, DateTime.Now);
            else File.WriteAllText(path, Report.Html(results, Replays, DateTime.Now));
            Say($"Wrote {Path.GetFileName(path)}. {Report.Notice}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say("Couldn't write the report: " + e.Message);
        }
    }
}
