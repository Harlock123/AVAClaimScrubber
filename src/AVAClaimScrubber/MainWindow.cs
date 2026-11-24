using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber;

/// <summary>An open file: its claims and what the checks found.</summary>
public sealed class OpenBatch(string path, ScrubResult result)
{
    public string Path { get; } = path;
    public ScrubResult Result { get; set; } = result;
    public ClaimBatch Batch => Result.Batch;
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>An entry in the list on the left: a file, or one of its claims.</summary>
public sealed record ListEntry(OpenBatch File, Claim? Claim)
{
    public override string ToString() => Claim == null ? File.Name : $"{Claim.Number} {Claim.Account}";
}

/// <summary>
/// The window: the open files and their claims on the left, each marked clean, warn, deny or reject; the Batch tab
/// (the files' totals, envelope findings and every claim), the Claim tab (its loops, its segments with the one a
/// finding points at highlighted, and its findings with their fixes) and the Findings tab (everything, by rule). The
/// window shows real values; built in code; its controls are reachable from tests.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<OpenBatch> _open = [];
    private OpenBatch? _file;
    private Claim? _claim;
    private bool _filling;

    public ListBox FileList { get; } = new() { Background = Look.Bar };
    public TabControl Tabs { get; } = new();
    public StackPanel BatchPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 6 };
    public TextBlock ClaimTitle { get; } = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };
    public TextBlock ClaimWhere { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    public TreeView LoopTree { get; } = new() { Background = Look.Bar };
    public SelectableTextBlock Segments { get; } = new() { FontFamily = Look.Mono, FontSize = 12.5, Foreground = Look.Code, Margin = new Thickness(8) };
    public ScrollViewer SegmentScroll { get; } = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Background = Look.Sheet };
    public ListBox ClaimFindings { get; } = new() { Background = Look.Sheet };
    public ListBox AllFindings { get; } = new() { Background = Look.Sheet };
    public TextBlock FindingsCount { get; } = new() { Foreground = Look.Heading, Margin = new Thickness(14, 10, 14, 6) };
    public TextBlock txtStatusBar { get; } = new() { Foreground = Look.Heading, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>Asks for files to open; tests answer it themselves.</summary>
    public Func<Task<IReadOnlyList<string>>> PickFiles { get; set; }

    /// <summary>Asks where to save, given a title and the name to suggest; tests answer it themselves.</summary>
    public Func<string, string, Task<string?>> PickSaveFile { get; set; }

    public IReadOnlyList<OpenBatch> OpenFiles => _open;
    public OpenBatch? CurrentFile => _file;
    public Claim? CurrentClaim => _claim;

    public MainWindow() : this(null) { }

    public MainWindow(IEnumerable<string>? open)
    {
        Title = "AVA Claim Scrubber";
        Width = 1440;
        Height = 880;
        MinWidth = 800;
        MinHeight = 500;
        Background = Look.Window;
        PickFiles = AskForFiles;
        PickSaveFile = AskWhereToSave;

        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var status = new Border { Child = txtStatusBar, Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 5) };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        AddExtraDocks(root);

        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("300,4,*") };
        var left = new DockPanel { Background = Look.Bar };
        var leftTitle = new TextBlock { Text = "FILES AND CLAIMS", Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(10, 8, 10, 4) };
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        left.Children.Add(FileList);
        FileList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ListEntry>((e, _) => e == null ? new TextBlock() : EntryLabel(e));
        FileList.SelectionChanged += (_, _) =>
        {
            if (_filling || FileList.SelectedItem is not ListEntry e) return;
            if (e.Claim == null) ShowBatch(e.File);
            else ShowClaim(e.File, e.Claim);
        };
        main.Children.Add(left);
        var split = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split, 1);
        main.Children.Add(split);
        Grid.SetColumn(Tabs, 2);
        main.Children.Add(Tabs);
        Tabs.ItemsSource = BuildTabs();
        root.Children.Add(main);
        Content = root;

        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            foreach (var f in e.Data.GetFiles() ?? []) if (f.TryGetLocalPath() is { } p) Open(p);
        });
        DragDrop.SetAllowDrop(this, true);
        ShowBatchPanel();
        Say("Open 837 files (File › Open, Ctrl+O, or drop them here). Try the samples folder.");
        AfterBuild();
        foreach (var path in open ?? []) Open(path);
    }

    /// <summary>The tabs, in order. Later parts add theirs.</summary>
    private List<TabItem> BuildTabs() =>
    [
        new TabItem { Header = "Batch", Content = new ScrollViewer { Content = BatchPanel }, FontSize = 14 },
        new TabItem { Header = "Claim", Content = BuildClaimPage(), FontSize = 14 },
        new TabItem { Header = "Findings", Content = BuildFindingsPage(), FontSize = 14 },
        .. MoreTabs(),
    ];

    partial void AddExtraDocks(DockPanel root);
    partial void AfterBuild();
    private partial IEnumerable<TabItem> MoreTabs();
    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);
    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item);

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action click, Key? key = null, KeyModifiers mods = KeyModifiers.Control)
        {
            var m = new MenuItem { Header = header };
            if (key is { } k) m.InputGesture = new KeyGesture(k, mods);
            m.Click += (_, _) => click();
            return m;
        }
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new Command(() => _ = OpenPicked()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = new Command(Recheck) });
        var file = new MenuItem
        {
            Header = "_File",
            ItemsSource = (object[])
            [
                Item("_Open…", () => _ = OpenPicked(), Key.O, KeyModifiers.Control),
                Item("_Check Again", Recheck, Key.F5, KeyModifiers.None),
                Item("_Close File", CloseCurrent, Key.W, KeyModifiers.Control),
                new Separator(),
                .. MoreFileItems(Item),
                Item("E_xit", Close, null, KeyModifiers.None),
            ],
        };
        var view = new MenuItem
        {
            Header = "_View",
            ItemsSource = (object[])
            [
                Item("_Batch", () => Tabs.SelectedIndex = 0, Key.D1, KeyModifiers.Control),
                Item("_Claim", () => Tabs.SelectedIndex = 1, Key.D2, KeyModifiers.Control),
                Item("_Findings", () => Tabs.SelectedIndex = 2, Key.D3, KeyModifiers.Control),
                .. MoreViewItems(Item),
            ],
        };
        return new Menu { Background = Look.Bar, ItemsSource = (object[])[file, view, .. MoreMenus(Item)] };
    }

    // ----- Opening -----

    /// <summary>Opens and checks a file (or every X12 file in a folder).</summary>
    public void Open(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path).Where(X12File.LooksLikeX12).Order(StringComparer.Ordinal)) Open(f);
            return;
        }
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            _open.RemoveAll(o => o.Path == full);
            var batch = ClaimReader.Load(full);
            if (batch.Claims.Count == 0 && batch.File.Transactions.All(t => t.Code != "837"))
            {
                Say($"{System.IO.Path.GetFileName(path)} has no 837 claims.");
                OpenedOther(full, batch);
                return;
            }
            var open = new OpenBatch(full, Scrubber.Scrub(batch, Settings()));
            _open.Add(open);
            FillList();
            ShowBatch(open);
            Say($"Checked {open.Name}: {Summary(open.Result)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't open {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
    }

    /// <summary>A file with no claims (a 999, 277CA or 835): later parts read those.</summary>
    partial void OpenedOther(string path, ClaimBatch batch);

    /// <summary>After every file is checked again: later parts refresh what depends on the findings.</summary>
    partial void Rechecked();

    /// <summary>What the checks run with (code sets, payer rules): later parts fill it.</summary>
    private partial ScrubSettings Settings();

    /// <summary>Checks every open file again (after the rules or code sets change).</summary>
    public void Recheck()
    {
        foreach (var o in _open) o.Result = Scrubber.Scrub(ClaimReader.Load(o.Path), Settings());
        FillList();
        if (_file != null && _open.FirstOrDefault(o => o.Path == _file.Path) is { } f)
        {
            var claim = _claim == null ? null : f.Batch.Claims.FirstOrDefault(c => c.Number == _claim.Number);
            if (claim != null) ShowClaim(f, claim);
            else ShowBatch(f);
        }
        else ShowBatchPanel();
        FillFindings();
        ShowRules();
        Rechecked();
        Say("Checked again.");
    }

    private async Task OpenPicked()
    {
        foreach (var p in await PickFiles()) Open(p);
    }

    private void CloseCurrent()
    {
        if (_file == null) return;
        _open.Remove(_file);
        _file = null;
        _claim = null;
        FillList();
        ShowBatchPanel();
        FillFindings();
    }

    public static string Summary(ScrubResult r)
    {
        var claims = r.Batch.Claims;
        var clean = claims.Count(c => r.Status(c) == null);
        return $"{claims.Count} claim{(claims.Count == 1 ? "" : "s")}, {clean} clean, {r.Findings.Count} finding{(r.Findings.Count == 1 ? "" : "s")}";
    }

    private void FillList()
    {
        _filling = true;
        var entries = new List<ListEntry>();
        foreach (var o in _open)
        {
            entries.Add(new ListEntry(o, null));
            entries.AddRange(o.Batch.Claims.Select(c => new ListEntry(o, c)));
        }
        FileList.ItemsSource = entries;
        _filling = false;
    }

    private Control EntryLabel(ListEntry e)
    {
        var t = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
        if (e.Claim == null)
        {
            t.Inlines!.Add(new Run(e.File.Name) { Foreground = Look.Accent, FontWeight = FontWeight.Bold });
            t.Inlines.Add(new Run($"  {e.File.Batch.Claims.Count} claims") { Foreground = Look.Dim, FontSize = 11 });
            t.Margin = new Thickness(0, 6, 0, 0);
            return t;
        }
        var status = e.File.Result.Status(e.Claim);
        t.Inlines!.Add(new Run("●  ") { Foreground = Look.Of(status) });
        t.Inlines.Add(new Run($"{e.Claim.Number}. {e.Claim.Account}") { Foreground = Look.Text });
        t.Inlines.Add(new Run($"  {(e.Claim.Total ?? 0).ToString("N2", CultureInfo.InvariantCulture)}") { Foreground = Look.Dim, FontSize = 11 });
        t.Margin = new Thickness(12, 0, 0, 0);
        return t;
    }

    // ----- The batch -----

    public void ShowBatch(OpenBatch file)
    {
        _file = file;
        _claim = null;
        ShowBatchPanel();
        FillFindings();
        Tabs.SelectedIndex = 0;
    }

    private void ShowBatchPanel()
    {
        BatchPanel.Children.Clear();
        if (_open.Count == 0)
        {
            BatchPanel.Children.Add(Heading("Nothing open yet"));
            BatchPanel.Children.Add(new TextBlock
            {
                Text = "Open 837 files (professional or institutional, 5010) to check them before they're sent: the envelope and syntax, " +
                       "totals and dates, and later layers' code sets, edits and payer rules. Each finding says what's wrong, where, why, and how to fix it.",
                TextWrapping = TextWrapping.Wrap, Foreground = Look.Dim, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
            });
            return;
        }
        var files = _file != null ? [_file] : _open;
        var claims = files.SelectMany(f => f.Batch.Claims.Select(c => (f, c))).ToList();
        BatchPanel.Children.Add(Heading(_file?.Name ?? "All open files"));
        BatchPanel.Children.Add(new TextBlock { Text = string.Join("  ·  ", files.Select(f => $"{f.Name}: {Summary(f.Result)}")), Foreground = Look.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var cards = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) };
        void Card(string number, string label, IBrush brush)
        {
            var s = new StackPanel { Spacing = 2 };
            s.Children.Add(new TextBlock { Text = number, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = brush });
            s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Look.Heading });
            cards.Children.Add(new Border { Child = s, Background = Look.Card, BorderBrush = Look.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 10, 10), MinWidth = 120 });
        }
        Card(claims.Count.ToString(CultureInfo.InvariantCulture), "claims", Look.Text);
        foreach (var s in new Severity?[] { null, Severity.Warn, Severity.Deny, Severity.Reject })
            Card(claims.Count(x => x.f.Result.Status(x.c) == s).ToString(CultureInfo.InvariantCulture), Look.Label(s), Look.Of(s));
        var atRisk = claims.Where(x => x.f.Result.Status(x.c) is Severity.Deny or Severity.Reject).Sum(x => x.c.Total ?? 0);
        Card("$" + atRisk.ToString("N2", CultureInfo.InvariantCulture), "at risk (deny or reject)", Look.Deny);
        BatchPanel.Children.Add(cards);

        var envelope = files.SelectMany(f => f.Result.Envelope.Select(e => (f, e))).ToList();
        if (envelope.Count > 0)
        {
            BatchPanel.Children.Add(Section("The envelope"));
            foreach (var (f, e) in envelope)
                BatchPanel.Children.Add(FindingLine(e, f, compact: true));
        }

        BatchPanel.Children.Add(Section("Claims"));
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var _ in Enumerable.Range(0, 7)) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var row = 0;
        void Cell(Control c, int col)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, col);
            c.Margin = new Thickness(0, 2, 22, 2);
            grid.Children.Add(c);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] heads = ["#", "Status", "Account", "Kind", "Lines", "Total", "Worst finding"];
        for (var i = 0; i < heads.Length; i++) Cell(new TextBlock { Text = heads[i], Foreground = Look.Heading, FontSize = 11.5 }, i);
        foreach (var (f, c) in claims)
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var status = f.Result.Status(c);
            Cell(new TextBlock { Text = c.Number.ToString(CultureInfo.InvariantCulture), Foreground = Look.Dim }, 0);
            Cell(new TextBlock { Text = Look.Label(status), Foreground = Look.Of(status), FontWeight = FontWeight.SemiBold }, 1);
            var open = new Button { Content = c.Account, Background = Brushes.Transparent, Foreground = Look.Accent, Padding = new Thickness(0) };
            var (file, claim) = (f, c);
            open.Click += (_, _) => ShowClaim(file, claim);
            Cell(open, 2);
            Cell(new TextBlock { Text = c.Kind == ClaimKind.Professional ? "837P" : "837I", Foreground = Look.Code }, 3);
            Cell(new TextBlock { Text = c.Lines.Count.ToString(CultureInfo.InvariantCulture), Foreground = Look.Code, HorizontalAlignment = HorizontalAlignment.Right }, 4);
            Cell(new TextBlock { Text = (c.Total ?? 0).ToString("N2", CultureInfo.InvariantCulture), Foreground = Look.Code, HorizontalAlignment = HorizontalAlignment.Right }, 5);
            var worst = f.Result.Of(c).FirstOrDefault();
            Cell(new TextBlock { Text = worst == null ? "" : $"{worst.Rule.Id}  {worst.Rule.Title}", Foreground = Look.Dim, FontSize = 12 }, 6);
        }
        BatchPanel.Children.Add(grid);
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text };

    private static TextBlock Section(string text) => new() { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = Look.Heading, Margin = new Thickness(0, 14, 0, 4) };

    // ----- A claim -----

    private Control BuildClaimPage()
    {
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8), Spacing = 3 };
        header.Children.Add(ClaimTitle);
        header.Children.Add(ClaimWhere);
        SegmentScroll.Content = Segments;
        LoopTree.SelectionChanged += (_, _) =>
        {
            if (LoopTree.SelectedItem is TreeViewItem { Tag: Loop l }) HighlightSegments(l.Segments.Select(s => s.Index).ToHashSet());
        };
        ClaimFindings.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Finding>((f, _) => f == null || _file == null ? new TextBlock() : FindingLine(f, _file, compact: false));
        ClaimFindings.SelectionChanged += (_, _) =>
        {
            if (ClaimFindings.SelectedItem is Finding f && f.SegmentIndex >= 0) HighlightSegments([f.SegmentIndex]);
        };
        var findingsTitle = new TextBlock { Text = "FINDINGS", Foreground = Look.Heading, FontSize = 11, Margin = new Thickness(8, 8, 8, 4) };
        var right = new DockPanel { Background = Look.Bar };
        DockPanel.SetDock(findingsTitle, Dock.Top);
        right.Children.Add(findingsTitle);
        right.Children.Add(ClaimFindings);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("230,4,*,4,420") };
        body.Children.Add(LoopTree);
        var s1 = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(s1, 1);
        body.Children.Add(s1);
        Grid.SetColumn(SegmentScroll, 2);
        body.Children.Add(SegmentScroll);
        var s2 = new GridSplitter { Background = Look.Border, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(s2, 3);
        body.Children.Add(s2);
        Grid.SetColumn(right, 4);
        body.Children.Add(right);
        var page = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        page.Children.Add(header);
        page.Children.Add(body);
        return page;
    }

    private IReadOnlyList<Segment> _shown = [];

    /// <summary>Shows a claim: its loops (from its subscriber down), its segments, and its findings.</summary>
    public void ShowClaim(OpenBatch file, Claim claim)
    {
        _file = file;
        _claim = claim;
        ClaimTitle.Text = $"Claim {claim.Number}: {claim.Account}";
        var status = file.Result.Status(claim);
        ClaimWhere.Inlines!.Clear();
        ClaimWhere.Inlines.Add(new Run(Look.Label(status).ToUpperInvariant()) { Foreground = Look.Of(status), FontWeight = FontWeight.SemiBold });
        ClaimWhere.Inlines.Add(new Run($"   {(claim.Kind == ClaimKind.Professional ? "837P professional" : "837I institutional")}  ·  total {(claim.Total ?? 0).ToString("N2", CultureInfo.InvariantCulture)}  ·  {claim.Lines.Count} line{(claim.Lines.Count == 1 ? "" : "s")}  ·  payer {claim.Payer?[3].Trim()}  ·  {file.Name}"));
        var top = claim.Patient?.Parent ?? claim.Subscriber ?? claim.Loop;
        var context = new List<Loop>();
        if (claim.BillingProvider != null) context.Add(claim.BillingProvider);
        LoopTree.ItemsSource = context.Select(l => LoopItem(l, expand: false, only: null)).Append(LoopItem(top, expand: true, only: claim.Loop)).ToList();
        // The claim's segments: its subscriber (and patient) loops, then the claim and its lines.
        _shown = SegmentsOf(top, claim.Loop).OrderBy(s => s.Index).ToList();
        ClaimFindings.ItemsSource = file.Result.Of(claim).ToList();
        HighlightSegments(file.Result.Of(claim).Select(f => f.SegmentIndex).Where(i => i >= 0).ToHashSet());
        Tabs.SelectedIndex = 1;
        SelectInList(file, claim);
    }

    /// <summary>A loop's segments, and those of loops inside it, leaving out other claims.</summary>
    private static IEnumerable<Segment> SegmentsOf(Loop loop, Loop claim)
    {
        foreach (var s in loop.Segments) yield return s;
        foreach (var c in loop.Children.Where(c => c.Id != "2300" || c == claim))
            foreach (var s in SegmentsOf(c, claim)) yield return s;
    }

    private static TreeViewItem LoopItem(Loop loop, bool expand, Loop? only)
    {
        var text = new TextBlock { FontSize = 12.5 };
        text.Inlines!.Add(new Run(loop.Id + "  ") { Foreground = Look.SegmentId, FontFamily = Look.Mono });
        text.Inlines.Add(new Run(loop.Name) { Foreground = Look.Text });
        return new TreeViewItem
        {
            Header = text,
            Tag = loop,
            IsExpanded = expand,
            ItemsSource = loop.Children.Where(c => only == null || c.Id != "2300" || c == only).Select(c => LoopItem(c, expand, only)).ToList(),
        };
    }

    private void HighlightSegments(HashSet<int> indexes)
    {
        var d = _file?.Batch.File.Delimiters ?? Delimiters.Standard;
        var inlines = new InlineCollection();
        foreach (var s in _shown)
        {
            var on = indexes.Contains(s.Index);
            inlines.Add(new Run((s.Index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5) + "  ") { Foreground = on ? Look.Deny : Look.Gutter });
            var parts = s.Text.Split(d.Element);
            inlines.Add(new Run(parts[0]) { Foreground = Look.SegmentId, Background = on ? Look.Highlight : null, FontWeight = FontWeight.SemiBold });
            for (var i = 1; i < parts.Length; i++)
            {
                inlines.Add(new Run(d.Element.ToString()) { Foreground = Look.Separator, Background = on ? Look.Highlight : null });
                inlines.Add(new Run(parts[i]) { Background = on ? Look.Highlight : null });
            }
            inlines.Add(new Run(d.Shown + "\n") { Foreground = Look.Separator });
        }
        Segments.Inlines = inlines;
        var first = _shown.Select((s, i) => (s, i)).FirstOrDefault(x => indexes.Contains(x.s.Index));
        if (first.s != null)
            Dispatcher.UIThread.Post(() =>
            {
                var lineHeight = Segments.Bounds.Height > 0 && _shown.Count > 0 ? Segments.Bounds.Height / _shown.Count : 16;
                SegmentScroll.Offset = new Vector(0, Math.Max(0, (first.i - 4) * lineHeight));
            }, DispatcherPriority.Background);
    }

    /// <summary>The highlighted text in the segments view (for tests).</summary>
    public string Highlighted => string.Concat(Segments.Inlines?.OfType<Run>().Where(r => r.Background != null).Select(r => r.Text) ?? []);

    private void SelectInList(OpenBatch file, Claim claim)
    {
        if (FileList.ItemsSource is not IEnumerable<ListEntry> entries) return;
        _filling = true;
        FileList.SelectedItem = entries.FirstOrDefault(e => e.File == file && e.Claim?.Number == claim.Number);
        _filling = false;
    }

    /// <summary>A finding as a line: severity, rule, where, what's wrong, the fix.</summary>
    private Control FindingLine(Finding f, OpenBatch file, bool compact)
    {
        var lines = new StackPanel { Spacing = 2, Margin = new Thickness(2, 3) };
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        head.Inlines!.Add(new Run(Look.Label(f.Severity).ToUpperInvariant() + "  ") { Foreground = Look.Of(f.Severity), FontWeight = FontWeight.SemiBold, FontSize = 11 });
        head.Inlines.Add(new Run(f.Rule.Id + "  ") { Foreground = Look.Dim, FontFamily = Look.Mono, FontSize = 11.5 });
        head.Inlines.Add(new Run(f.Message) { Foreground = Look.Text });
        lines.Children.Add(head);
        var where = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        where.Inlines!.Add(new Run(f.Place) { Foreground = Look.Accent });
        if (!compact) where.Inlines.Add(new Run("   " + f.Rule.Title) { Foreground = Look.Dim });
        lines.Children.Add(where);
        if (f.Fix is { } fix) lines.Children.Add(new TextBlock { Text = "Fix: " + fix.Description, Foreground = Look.Clean, FontSize = 12 });
        return lines;
    }

    // ----- All findings -----

    private Control BuildFindingsPage()
    {
        AllFindings.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<(OpenBatch File, Finding Finding)>((x, _) => x.File == null ? new TextBlock() : FindingLine(x.Finding, x.File, compact: false));
        AllFindings.DoubleTapped += (_, _) =>
        {
            if (AllFindings.SelectedItem is (OpenBatch file, Finding f) && file.Batch.Claims.FirstOrDefault(c => c.Number == f.Claim) is { } claim)
            {
                ShowClaim(file, claim);
                if (f.SegmentIndex >= 0) HighlightSegments([f.SegmentIndex]);
            }
        };
        var page = new DockPanel();
        DockPanel.SetDock(FindingsCount, Dock.Top);
        page.Children.Add(FindingsCount);
        page.Children.Add(AllFindings);
        return page;
    }

    /// <summary>Every finding in the open files (or the selected one), by rule.</summary>
    private void FillFindings()
    {
        var files = _file != null ? [_file] : _open;
        var all = files.SelectMany(f => f.Result.Findings.Select(x => (f, x))).OrderByDescending(x => x.x.Severity).ThenBy(x => x.x.Rule.Id, StringComparer.Ordinal).ThenBy(x => x.x.Claim).ToList();
        AllFindings.ItemsSource = all;
        FindingsCount.Text = $"{all.Count} findings · " + string.Join(", ", all.GroupBy(x => x.x.Rule.Id).Select(g => $"{g.Key} ×{g.Count()}"));
    }

    // ----- Asking -----

    private async Task<IReadOnlyList<string>> AskForFiles()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open 837 files", AllowMultiple = true });
        return picked.Select(p => p.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async Task<string?> AskWhereToSave(string title, string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggested });
        return file?.TryGetLocalPath();
    }

    public void Say(string text) => txtStatusBar.Text = text;

    private sealed class Command(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
