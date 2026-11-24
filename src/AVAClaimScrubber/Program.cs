using Avalonia;

namespace AVAClaimScrubber;

internal static class Program
{
    /// <summary>
    /// Starts the window, opening and checking any files given. With <c>--mcp</c> it opens no window: it serves the Model
    /// Context Protocol for AI agents instead (see <see cref="Mcp.McpCommandLine"/> and Agents/AgentMain). With
    /// <c>--deidentify INPUT OUTPUT</c> it writes de-identified copies and exits.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (Mcp.McpCommandLine.Wanted(args)) return Agents.AgentMain.Run(args);
        if (args.Length == 3 && args[0] == "--deidentify") return Deidentify(args[1], args[2]);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>De-identified copies of an X12 file, or of every X12 file in a folder (kept in the same layout).</summary>
    private static int Deidentify(string input, string output)
    {
        try
        {
            var d = new Core.X12.Deidentifier();
            var files = Directory.Exists(input)
                ? Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Where(Core.X12.X12File.LooksLikeX12).Select(f => (f, Path.Combine(output, Path.GetRelativePath(input, f)))).ToList()
                : [(input, output)];
            foreach (var (from, to) in files)
            {
                if (File.Exists(to)) { Console.Error.WriteLine($"{to} is there already: nothing is written over."); return 1; }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
                var r = d.Load(from);
                File.WriteAllText(to, r.Text);
                Console.WriteLine($"{from} → {to}: {r.Summary}");
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
