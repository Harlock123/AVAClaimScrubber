using Avalonia.Controls;
using Avalonia.Input;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;

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
        [item("Add Rule _Pack…", () => _ = AddPicked(), null, KeyModifiers.None), new Separator()];

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
