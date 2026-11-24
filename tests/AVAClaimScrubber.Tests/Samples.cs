using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;

namespace AVAClaimScrubber.Tests;

/// <summary>The samples folder, found from the test's output folder, and its files read and checked.</summary>
public static class Samples
{
    public static string Folder { get; } = Find();

    public static string Path(string name) => System.IO.Path.Combine(Folder, name);

    public static ClaimBatch Load(string name) => ClaimReader.Load(Path(name));

    public static ScrubResult Scrub(string name, ScrubSettings? settings = null) => Scrubber.Scrub(Load(name), settings);

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
