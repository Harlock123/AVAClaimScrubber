using Avalonia.Controls;
using Avalonia.Input;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber;

/// <summary>The hooks later parts of the window fill: more tabs, menus and settings.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<TabItem> MoreTabs() =>
    [
        new TabItem { Header = "Rules", Content = BuildRulesPage(), FontSize = 14 },
        new TabItem { Header = "Code Sets", Content = BuildCodeSetsPage(), FontSize = 14 },
    ];
    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];

    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
        [
            item("_Rules", () => { ShowRules(); Tabs.SelectedIndex = 3; }, Key.D4, KeyModifiers.Control),
            item("Code _Sets", () => { ShowCodeSets(); Tabs.SelectedIndex = 4; }, Key.D5, KeyModifiers.Control),
        ];
    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
        [
            item("Save Corrected _Copy…", () => _ = SaveCorrected(), null, KeyModifiers.None),
            item("Add Rule _Pack…", () => _ = AddPicked(), null, KeyModifiers.None),
            new Separator(),
        ];

    /// <summary>
    /// Writes a corrected copy of the selected file (the fixes its findings suggest, and nothing else) as a new file,
    /// opens it, and says what changed and what's left.
    /// </summary>
    public async Task SaveCorrected()
    {
        if (_file == null) { Say("Pick a file first."); return; }
        var r = _file.Result;
        if (!r.Findings.Any(f => f.Fix != null)) { Say($"{_file.Name} has no findings with a fix."); return; }
        var path = await PickSaveFile("Save a corrected copy", Path.GetFileNameWithoutExtension(_file.Name) + "-corrected" + Path.GetExtension(_file.Name));
        if (path == null) return;
        if (Path.GetFullPath(path) == _file.Path) { Say("A corrected copy is a new file: choose another name."); return; }
        try
        {
            var (text, changes) = Corrector.Apply(File.ReadAllText(_file.Path), r.Batch.File, r.Findings);
            File.WriteAllText(path, text);
            LastChanges = changes;
            Open(path);
            var now = _open.First(o => o.Path == Path.GetFullPath(path));
            Say($"Wrote {Path.GetFileName(path)} with {changes.Count} change{(changes.Count == 1 ? "" : "s")} ({string.Join("; ", changes.Select(c => $"{c.Element} {c.Was} → {c.Now}"))}). Now: {Summary(now.Result)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say("Couldn't write the corrected copy: " + e.Message);
        }
    }

    /// <summary>The changes the last corrected copy made (for tests and the status line).</summary>
    public IReadOnlyList<Change> LastChanges { get; private set; } = [];

    private async Task AddPicked()
    {
        if (await PickRulePack() is { } p) AddRulePack(p);
    }
    private partial ScrubSettings Settings() => new() { Packs = Packs, CodeSets = CodeSets };

    partial void AfterBuild()
    {
        LoadPacks();
        LoadCodeSets();
        ShowRules();
        ShowCodeSets();
    }
}
