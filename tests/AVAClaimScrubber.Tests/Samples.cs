using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Tests;

/// <summary>The samples folder, found from the test's output folder, and its files read and checked.</summary>
public static class Samples
{
    public static string Folder { get; } = Find();

    public static string Path(string name) => System.IO.Path.Combine(Folder, name);

    public static ClaimBatch Load(string name) => ClaimReader.Load(Path(name));

    /// <summary>Checks a sample, with the code-set excerpts loaded unless other settings are given.</summary>
    public static ScrubResult Scrub(string name, ScrubSettings? settings = null) => Scrubber.Scrub(Load(name), settings ?? new ScrubSettings { CodeSets = CodeSets });

    /// <summary>The tests' code sets: excerpts of the real files (Data/), loaded once.</summary>
    public static Core.CodeSets.CodeSets CodeSets => s_codeSets.Value;

    private static readonly Lazy<Core.CodeSets.CodeSets> s_codeSets = new(() =>
    {
        var folder = Directory.CreateTempSubdirectory("claims-codes-").FullName;
        File.Copy(DataPath("icd10cm_order_extract.txt"), System.IO.Path.Combine(folder, "icd10cm.txt"));
        File.Copy(DataPath("hcpcs_extract.txt"), System.IO.Path.Combine(folder, "hcpcs.txt"));
        File.Copy(DataPath("taxonomy_extract.csv"), System.IO.Path.Combine(folder, "taxonomy.csv"));
        File.Copy(DataPath("mue_practitioner_extract.csv"), System.IO.Path.Combine(folder, "mue-practitioner.csv"));
        File.Copy(DataPath("ncci_ptp_invented.txt"), System.IO.Path.Combine(folder, "ncci-ptp-1.txt"));
        return Core.CodeSets.CodeSets.Load(folder);
    });

    public static string DataPath(string name) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Folder)!, "tests", "AVAClaimScrubber.Tests", "Data", name);

    /// <summary>A copy of the samples in a new temporary folder (agents and tests write into it).</summary>
    public static string Copy()
    {
        var root = Directory.CreateTempSubdirectory("claims-samples-").FullName;
        foreach (var f in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
        {
            var to = System.IO.Path.Combine(root, System.IO.Path.GetRelativePath(Folder, f));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return root;
    }

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "samples");
            if (File.Exists(System.IO.Path.Combine(candidate, "clinic-837p.x12"))) return candidate;
        }
        throw new DirectoryNotFoundException("Couldn't find the samples folder.");
    }

    public static Claim Claim(this ClaimBatch batch, string account) => batch.Claims.Single(c => c.Account == account);

    /// <summary>The findings as "RULE account[ line n]" lines, sorted: what the tests pin.</summary>
    public static List<string> Pinned(this ScrubResult r) =>
        [.. r.Findings.Select(f => f.Rule.Id + " " + (f.Claim == 0 ? "envelope" : r.Batch.Claims.First(c => c.Number == f.Claim).Account) + (f.Line > 0 ? $" line {f.Line}" : "")).Order(StringComparer.Ordinal)];
}

public sealed class Dump
{
    [Fact]
    public void Print()
    {
        if (Environment.GetEnvironmentVariable("CLAIMS_DUMP") is not { } o) return;
        using var w = new StreamWriter(o);
        foreach (var f in Directory.EnumerateFiles(Samples.Folder, "*.x12").Order())
        {
            var r = Samples.Scrub(Path.GetFileName(f));
            w.WriteLine("== " + Path.GetFileName(f));
            foreach (var x in r.Findings) w.WriteLine($"{x.Rule.Id} {(x.Claim > 0 ? r.Batch.Claims.First(c => c.Number == x.Claim).Account : "envelope")} {x.Place}: {x.Message}{(x.Fix != null ? "  FIX " + x.Fix.Description : "")}");
        }
    }
}
