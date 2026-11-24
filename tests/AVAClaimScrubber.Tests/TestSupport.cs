using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(AVAClaimScrubber.Tests.HeadlessApp))]

namespace AVAClaimScrubber.Tests;

/// <summary>The headless Avalonia app the window tests run on: one for the whole run, with Skia so frames can be captured.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs <paramref name="action"/> on the headless UI thread.</summary>
    public static void Run(Action action) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly)
            .Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Lets queued UI work run.</summary>
    public static void Settle(int times = 20)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }
}

/// <summary>Before any test runs, the program's settings folder (rules, code sets, AI settings) is a temporary one.</summary>
internal static class SettingsIsolation
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Isolate()
    {
        var folder = Directory.CreateTempSubdirectory("avaclaimscrubber-settings-").FullName;
        AVAClaimScrubber.MainWindow.FolderOverride = folder;
        // Windows in tests have the code-set excerpts, as a person who'd pressed Update would have the full sets.
        var codes = Path.Combine(folder, "CodeSets");
        Directory.CreateDirectory(codes);
        File.Copy(Samples.DataPath("icd10cm_order_extract.txt"), Path.Combine(codes, "icd10cm.txt"));
        File.Copy(Samples.DataPath("hcpcs_extract.txt"), Path.Combine(codes, "hcpcs.txt"));
        File.Copy(Samples.DataPath("taxonomy_extract.csv"), Path.Combine(codes, "taxonomy.csv"));
        File.Copy(Samples.DataPath("mue_practitioner_extract.csv"), Path.Combine(codes, "mue-practitioner.csv"));
        File.Copy(Samples.DataPath("ncci_ptp_invented.txt"), Path.Combine(codes, "ncci-ptp-1.txt"));
    }
}
