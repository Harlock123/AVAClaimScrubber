using System.Diagnostics;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.Responses;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Mcp;

/// <summary>
/// What the window shows: the file selected (a full path; an 837 or a response), the claim selected in it (0: none)
/// and the tab on show (batch, claim, findings, rules, code sets or responses). Agents read the same checks the person
/// sees; this is only where the person is looking.
/// </summary>
public sealed record ViewState(string? File, int Claim, string Tab)
{
    public static readonly ViewState Empty = new(null, 0, "batch");
}

/// <summary>What a picture of the view shows: a title, a line under it, and lines (segments, claims, denials), some highlighted.</summary>
public sealed record ViewPicture(string Title, string Subtitle, IReadOnlyList<string> Lines, int FirstLine, int HighlightFrom, int HighlightTo, bool Numbered, IReadOnlyList<string> Notes);

/// <summary>What the server needs from the program that it can't do itself: drawing the view, which needs Avalonia.</summary>
public interface IViewPainter
{
    /// <summary>The view drawn as a PNG, at <paramref name="scale"/>.</summary>
    byte[] Picture(ViewPicture view, double scale);
}

/// <summary>An 837 open in the session, and its scrub (worked out on the real file).</summary>
public sealed record OpenFile(string Path, ScrubResult Result);

/// <summary>A 999, 277CA or 835 open in the session.</summary>
public sealed record OpenResponse(string Path, ResponseFile File);

/// <summary>The window (or the headless server's stand-in for one) as an agent works with it. Used on the host's thread only.</summary>
public interface IExplorerSession
{
    /// <summary>What the window shows now.</summary>
    ViewState View { get; }

    /// <summary>
    /// Whether the person lets agents see real values (AI › Let AI Agents See Real Values, or <c>--real</c>). Off
    /// unless turned on: everything an agent reads is de-identified first.
    /// </summary>
    bool SeeRealValues { get; }

    /// <summary>The 837s open, each checked with <see cref="Settings"/>.</summary>
    IReadOnlyList<OpenFile> Files { get; }

    /// <summary>The responses open.</summary>
    IReadOnlyList<OpenResponse> Responses { get; }

    /// <summary>What the checks run with: the rule packs and code sets in use.</summary>
    ScrubSettings Settings { get; }

    /// <summary>Opens an 837 (checking it) or a response; throws saying why when the file is neither.</summary>
    void Open(string path);

    /// <summary>Closes an open file or response.</summary>
    void Close(string path);

    /// <summary>Selects in the window (headless: just remembers it).</summary>
    void Show(ViewState view);
}

/// <summary>
/// Where the MCP tools' work runs. The handlers (<see cref="ClaimsTools"/>) touch the window only through
/// <see cref="RunAsync{T}"/>, so the same handlers serve the headless server and the open window.
/// </summary>
public interface IExplorerHost
{
    /// <summary>The working folder: files are read from it, and new files written only inside it.</summary>
    string Root { get; }

    IViewPainter Painter { get; }

    /// <summary>The session's de-identifier: one key, so the same value gets the same stand-in in every call.</summary>
    Deidentifier Deidentifier { get; }

    /// <summary>Working in the open window, with the person watching.</summary>
    bool Live { get; }

    /// <summary>Runs <paramref name="work"/> on the thread that owns the window, one call at a time.</summary>
    Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default);
}

public static class ExplorerHostExtensions
{
    public static Task<T> RunAsync<T>(this IExplorerHost host, string action, Func<IExplorerSession, T> work, CancellationToken cancel = default) =>
        host.RunAsync(action, s => Task.FromResult(work(s)), cancel);
}

/// <summary>Runs work on a UI thread (the program's Avalonia dispatcher, or a test's).</summary>
public interface IUiThread
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>The program as the host sees it: the session, whether it can take a call now, and the status bar.</summary>
public interface IExplorerDesk
{
    IExplorerSession Session { get; }

    /// <summary>Why the program can't take a call just now ("a dialog box is open"), or null. The call waits.</summary>
    string? Busy { get; }

    /// <summary>Tells the person what an agent just did.</summary>
    void Report(string text);
}

/// <summary>
/// The host: calls run against the desk's session on its UI thread, one at a time. While the person is busy (a dialog
/// open) a call waits up to <see cref="BusyWait"/>.
/// </summary>
public sealed class ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, Func<string> root, Deidentifier? deidentifier = null) : IExplorerHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExplorerHost(IUiThread ui, IExplorerDesk desk, IViewPainter painter, string root, Deidentifier? deidentifier = null)
        : this(ui, desk, painter, () => root, deidentifier) { }

    public string Root => Path.GetFullPath(root());

    public IViewPainter Painter { get; } = painter;

    public Deidentifier Deidentifier { get; } = deidentifier ?? new Deidentifier();

    public bool Live { get; init; }

    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromMilliseconds(200);

    public async Task<T> RunAsync<T>(string action, Func<IExplorerSession, Task<T>> work, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (ran, result, busy) = await ui.InvokeAsync(async () =>
                {
                    if (desk.Busy is { } why) return (false, default(T), why);
                    try
                    {
                        var r = await work(desk.Session);
                        desk.Report($"AI agent: {action}.");
                        return (true, r, (string?)null);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        desk.Report($"AI agent: {action} failed: {e.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                if (ran) return result!;
                if (clock.Elapsed >= BusyWait)
                    throw new ExplorerBusyException($"AVA Claim Scrubber is busy: {busy}. The call waited {BusyWait.TotalSeconds:0} seconds; ask the person to finish, then try again.");
                await Task.Delay(BusyPoll, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class ExplorerBusyException(string message) : Exception(message);

/// <summary>A session with no window round it, as <c>AVAClaimScrubber --mcp</c> serves it.</summary>
public sealed class HeadlessSession(bool seeRealValues = false, ScrubSettings? settings = null) : IExplorerSession
{
    private readonly List<OpenFile> _files = [];
    private readonly List<OpenResponse> _responses = [];

    public ViewState View { get; private set; } = ViewState.Empty;

    public bool SeeRealValues { get; set; } = seeRealValues;

    public IReadOnlyList<OpenFile> Files => _files;

    public IReadOnlyList<OpenResponse> Responses => _responses;

    public ScrubSettings Settings { get; } = settings ?? new ScrubSettings();

    public void Open(string path)
    {
        var full = Path.GetFullPath(path);
        var file = X12File.Load(full);
        Close(full);
        if (ResponseReader.Read(Path.GetFileName(full), file) is { } response) _responses.Add(new OpenResponse(full, response));
        else if (file.Transactions.Any(t => t.Code == "837")) _files.Add(new OpenFile(full, Scrubber.Scrub(ClaimReader.Read(Path.GetFileName(full), file), Settings)));
        else throw new NotSupportedException($"{Path.GetFileName(full)} isn't an 837, 999, 277CA or 835" + (file.Code is { } c ? $" (its transactions are {c}s)." : " (it has no ST segment)."));
    }

    public void Close(string path)
    {
        var full = Path.GetFullPath(path);
        _files.RemoveAll(f => f.Path == full);
        _responses.RemoveAll(r => r.Path == full);
    }

    public void Show(ViewState view) => View = view;
}

public sealed class HeadlessDesk(TextWriter? log = null, bool seeRealValues = false, ScrubSettings? settings = null) : IExplorerDesk
{
    public IExplorerSession Session { get; } = new HeadlessSession(seeRealValues, settings);

    public string? Busy => null;

    public void Report(string text) => log?.WriteLine(text);
}
