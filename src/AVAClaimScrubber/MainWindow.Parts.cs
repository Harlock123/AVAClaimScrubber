using Avalonia.Controls;
using Avalonia.Input;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber;

/// <summary>The hooks later parts of the window fill: more tabs, menus and settings.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<TabItem> MoreTabs() => [];
    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) => [];
    private partial ScrubSettings Settings() => new();
}
