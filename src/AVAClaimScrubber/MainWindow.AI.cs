using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AVAClaimScrubber.Mcp;

namespace AVAClaimScrubber;

/// <summary>The AI side's place in the window: the chat's dock, the AI menu, and the view as agents see and set it.</summary>
public sealed partial class MainWindow
{
    public Border ChatHolder { get; } = new() { Width = 440, IsVisible = false, BorderBrush = Look.Border, BorderThickness = new Thickness(1, 0, 0, 0) };

    /// <summary>Where pictures of the view are drawn for agents, out of sight.</summary>
    public Panel AgentStage { get; } = new() { Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false };

    public MenuItem ChatMenuItem { get; } = new() { Header = "AI _Chat", InputGesture = new KeyGesture(Key.F12), ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem SeeRealMenuItem { get; } = new() { Header = "Let AI Agents _See Real Values", ToggleType = MenuItemToggleType.CheckBox };
    public MenuItem AllowAgentsMenuItem { get; } = new() { Header = "_Allow AI Agents (MCP)", ToggleType = MenuItemToggleType.CheckBox };

    /// <summary>Started with <c>--deidentify</c>: agents get de-identified values whatever the setting says.</summary>
    public bool ForceDeidentify { get; init; }

    /// <summary>Started with <c>--real</c>: agents see real values (unless <see cref="ForceDeidentify"/>).</summary>
    public bool RealFromCommandLine { get; init; }

    partial void AddExtraDocks(DockPanel root)
    {
        DockPanel.SetDock(ChatHolder, Dock.Right);
        root.Children.Add(ChatHolder);
        DockPanel.SetDock(AgentStage, Dock.Right);
        root.Children.Add(AgentStage);
    }

    private partial IEnumerable<object> MoreMenus(Func<string, Action, Key?, KeyModifiers, MenuItem> item)
    {
        ChatMenuItem.Click += OnToggleChatClick;
        SeeRealMenuItem.Click += OnSeeRealClick;
        AllowAgentsMenuItem.Click += OnAllowAgentsClick;
        return
        [
            new MenuItem
            {
                Header = "_AI",
                ItemsSource = (object[])
                [
                    ChatMenuItem,
                    new Separator(),
                    SeeRealMenuItem,
                    item("_Working Folder…", () => OnWorkingFolderClick(null, new RoutedEventArgs()), null, KeyModifiers.None),
                    new Separator(),
                    AllowAgentsMenuItem,
                    item("C_onnect an Agent…", () => _ = ShowConnect(), null, KeyModifiers.None),
                ],
            },
            new MenuItem
            {
                Header = "_Help",
                ItemsSource = (object[])
                [
                    item("_About", () => Say($"AVA Claim Scrubber {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}: checks X12 837P and 837I claims before they're sent, and replays 999, 277CA and 835 responses. MIT licence, Lonnie Watson."), null, KeyModifiers.None),
                ],
            },
        ];
    }

    private static readonly string[] TabNames = ["batch", "claim", "findings", "rules", "code sets", "responses"];

    /// <summary>What the window shows, as agents see it.</summary>
    public ViewState CurrentView
    {
        get
        {
            var tab = Tabs.SelectedIndex >= 0 && Tabs.SelectedIndex < TabNames.Length ? TabNames[Tabs.SelectedIndex] : "batch";
            return new ViewState(_file?.Path, _claim?.Number ?? 0, tab);
        }
    }

    /// <summary>Shows what an agent asked: a file's batch, a claim, or a tab.</summary>
    public void ShowView(ViewState view)
    {
        var file = view.File is { } p ? _open.FirstOrDefault(o => o.Path == p) : null;
        if (file != null)
        {
            if (view.Claim > 0 && view.Claim <= file.Batch.Claims.Count) ShowClaim(file, file.Batch.Claims[view.Claim - 1]);
            else ShowBatch(file);
        }
        if (view.Tab == "responses") ShowResponses();
        Tabs.SelectedIndex = Math.Max(0, Array.IndexOf(TabNames, view.Tab));
    }

    /// <summary>The responses open, for agents.</summary>
    internal IReadOnlyList<OpenResponse> OpenResponses => [.. _responses.Select(r => new OpenResponse(r.Path, r.File))];

    /// <summary>The checks' settings now, for agents.</summary>
    internal Core.Checks.ScrubSettings CurrentSettings => Settings();

    /// <summary>Opens a file for an agent, saying why when it isn't one the program reads.</summary>
    internal void OpenForAgent(string path)
    {
        var full = Path.GetFullPath(path);
        Open(full);
        if (_open.All(o => o.Path != full) && _responses.All(r => r.Path != full))
            throw new NotSupportedException(txtStatusBar.Text is { Length: > 0 } said ? said : $"{Path.GetFileName(full)} isn't an 837, 999, 277CA, 835 or 271.");
    }

    /// <summary>Closes an open file or response for an agent.</summary>
    internal void CloseForAgent(string path)
    {
        var full = Path.GetFullPath(path);
        var answers = _responses.Any(r => r.Path == full && r.File.Kind == "271");
        if (_responses.RemoveAll(r => r.Path == full) > 0)
        {
            if (answers) Recheck();
            ShowResponses();
        }
        if (_open.FirstOrDefault(o => o.Path == full) is { } o)
        {
            _open.Remove(o);
            if (_file == o)
            {
                _file = null;
                _claim = null;
            }
            FillList();
            ShowBatchPanel();
            FillFindings();
            ShowResponses();
        }
    }

    partial void AgentsReady()
    {
        SetUpAgents();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.F12) return;
            ToggleChat();
            e.Handled = true;
        };
    }
}
