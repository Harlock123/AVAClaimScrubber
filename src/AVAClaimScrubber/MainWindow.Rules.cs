using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVAClaimScrubber.Core.Checks;

namespace AVAClaimScrubber;

/// <summary>The Rules tab: every rule by layer, how often it fired, and the rule packs that change them.</summary>
public sealed partial class MainWindow
{
    private readonly List<RulePack> _addedPacks = [];
    private List<RulePack> _folderPacks = [];
    private List<string> _packProblems = [];

    public StackPanel RulesPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 4 };

    /// <summary>The folder rule packs are read from (each *.json in it), besides those added by hand.</summary>
    public string RulesFolder { get; set; } = DefaultRulesFolder;

    /// <summary>Tests point this at a folder of their own, so no test reads the person's packs.</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>The program's settings folder (rules, code sets, AI settings live under it).</summary>
    internal static string SettingsFolder => FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVAClaimScrubber");

    private static string DefaultRulesFolder => Path.Combine(SettingsFolder, "Rules");

    /// <summary>Asks for a rule pack to add; tests answer it themselves.</summary>
    public Func<Task<string?>> PickRulePack { get; set; } = null!;

    /// <summary>Every pack in use: the folder's, then those added.</summary>
    public IReadOnlyList<RulePack> Packs => [.. _folderPacks, .. _addedPacks];


    private void LoadPacks()
    {
        (_folderPacks, _packProblems) = RulePack.LoadFolder(RulesFolder);
    }

    /// <summary>Adds a rule pack from a file and checks everything again.</summary>
    public void AddRulePack(string path)
    {
        try
        {
            var pack = RulePack.Load(path);
            _addedPacks.RemoveAll(p => p.Path == pack.Path);
            _addedPacks.Add(pack);
            Recheck();
            ShowRules();
            Say($"Added the rule pack \"{pack.Name}\"" + (pack.Payers.Count > 0 ? $" for {string.Join(", ", pack.Payers)}." : " for every payer."));
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            Say($"That rule pack doesn't read: {e.Message}");
        }
    }

    private Control BuildRulesPage()
    {
        PickRulePack = async () =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add a rule pack",
                FileTypeFilter = [new FilePickerFileType("Rule packs") { Patterns = ["*.json"] }],
            });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };
        return new ScrollViewer { Content = RulesPanel };
    }

    /// <summary>Fills the Rules tab.</summary>
    public void ShowRules()
    {
        RulesPanel.Children.Clear();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "Add Rule Pack…" };
        add.Click += async (_, _) =>
        {
            if (await PickRulePack() is { } p) AddRulePack(p);
        };
        var reload = new Button { Content = "Reload the Rules Folder" };
        reload.Click += (_, _) =>
        {
            LoadPacks();
            Recheck();
            ShowRules();
        };
        bar.Children.Add(add);
        bar.Children.Add(reload);
        RulesPanel.Children.Add(bar);

        RulesPanel.Children.Add(new TextBlock { Text = "RULE PACKS", FontSize = 11, Foreground = Look.Heading, Margin = new Thickness(0, 14, 0, 2) });
        RulesPanel.Children.Add(new TextBlock { Text = $"Read from {RulesFolder}, and any added. A pack turns a rule off or changes its severity, for every payer or the ones it names.", Foreground = Look.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (Packs.Count == 0 && _packProblems.Count == 0) RulesPanel.Children.Add(new TextBlock { Text = "None yet.", Foreground = Look.Dim, FontSize = 12.5 });
        foreach (var p in Packs)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            t.Inlines!.Add(new Run(p.Name) { Foreground = Look.Text, FontWeight = FontWeight.SemiBold });
            t.Inlines.Add(new Run($"  {(p.Payers.Count == 0 ? "every payer" : "payers " + string.Join(", ", p.Payers))}  ·  version {p.Version}  ·  {Path.GetFileName(p.Path ?? "")}") { Foreground = Look.Dim, FontSize = 12 });
            foreach (var o in p.Overrides)
                t.Inlines.Add(new Run($"\n   {o.RuleId}: {(o.Enabled ? (o.Severity is { } s ? Look.Label(s) : "on") : "off")}{(o.Why is { Length: > 0 } w ? " — " + w : "")}") { Foreground = Look.Code, FontSize = 12 });
            if (p.Rules.Count > 0) t.Inlines.Add(new Run($"\n   {p.Rules.Count} payer rule{(p.Rules.Count == 1 ? "" : "s")}") { Foreground = Look.Code, FontSize = 12 });
            RulesPanel.Children.Add(t);
        }
        foreach (var problem in _packProblems)
            RulesPanel.Children.Add(new TextBlock { Text = "Doesn't read: " + problem, Foreground = Look.Reject, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });

        var hits = _open.SelectMany(o => o.Result.Findings).GroupBy(f => f.Rule.Id).ToDictionary(g => g.Key, g => g.Count());
        foreach (var layer in Scrubber.AllRules(Settings()).GroupBy(r => r.Layer))
        {
            RulesPanel.Children.Add(new TextBlock { Text = LayerName(layer.Key).ToUpperInvariant(), FontSize = 11, Foreground = Look.Heading, Margin = new Thickness(0, 16, 0, 2) });
            foreach (var r in layer)
            {
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 2) };
                t.Inlines!.Add(new Run(r.Id.PadRight(4)) { FontFamily = Look.Mono, Foreground = Look.Dim });
                t.Inlines.Add(new Run(Look.Label(r.Severity).ToUpperInvariant() + "  ") { Foreground = Look.Of(r.Severity), FontSize = 11, FontWeight = FontWeight.SemiBold });
                t.Inlines.Add(new Run(r.Title) { Foreground = Look.Text });
                if (hits.TryGetValue(r.Id, out var n)) t.Inlines.Add(new Run($"   {n} in the open files") { Foreground = Look.Deny, FontSize = 12 });
                t.Inlines.Add(new Run($"\n{r.Why}  ") { Foreground = Look.Dim, FontSize = 12 });
                t.Inlines.Add(new Run(r.Source) { Foreground = Look.Gutter, FontSize = 11.5, FontStyle = FontStyle.Italic });
                RulesPanel.Children.Add(t);
            }
        }
    }

    public static string LayerName(int layer) => layer switch
    {
        1 => "1  Envelope and syntax",
        2 => "2  Claim structure",
        3 => "3  Arithmetic and dates",
        4 => "4  Code sets",
        5 => "5  NCCI and MUE edits",
        6 => "6  Payer rules",
        _ => layer.ToString(CultureInfo.InvariantCulture),
    };
}
