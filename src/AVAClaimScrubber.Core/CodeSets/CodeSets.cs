using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AVAClaimScrubber.Core.CodeSets;

/// <summary>An ICD-10-CM code: whether it's billable (a full code) or a header (a category with codes under it), and its short title.</summary>
public sealed record IcdCode(string Code, bool Billable, string Title);

/// <summary>A HCPCS Level II code: its title and the dates it was added, took its current form, and ended (if it has).</summary>
public sealed record HcpcsCode(string Code, string Title, DateOnly? Added, DateOnly? Effective, DateOnly? Terminated);

/// <summary>Where a code set came from: its name, version, source and when it was loaded. Every report says which it used.</summary>
public sealed record CodeSetInfo(string Name, string Version, string Source, DateTimeOffset Loaded, int Count);

/// <summary>
/// The code sets checks look codes up in: ICD-10-CM (CMS's order file: billable or header), HCPCS Level II (CMS's
/// quarterly alpha-numeric file: titles, dates, and the Level II modifiers), the NUCC provider taxonomy, CMS's places
/// of service (built in), and CPT only if a licensed file is loaded (CPT belongs to the AMA; without it, CPT codes and
/// numeric modifiers get a shape check only). A set that isn't loaded is left null, and the checks say so once.
/// </summary>
public sealed class CodeSets
{
    public Dictionary<string, IcdCode>? Icd { get; set; }
    public Dictionary<string, HcpcsCode>? Hcpcs { get; set; }
    public HashSet<string>? HcpcsModifiers { get; set; }
    public Dictionary<string, string>? Taxonomy { get; set; }
    public Dictionary<string, string>? Cpt { get; set; }
    public List<CodeSetInfo> Info { get; } = [];

    public static IReadOnlyDictionary<string, string> PlacesOfService => PlaceOfService.Codes;

    public bool Empty => Icd == null && Hcpcs == null && Taxonomy == null && Cpt == null;

    // ----- Parsing CMS's and NUCC's files -----

    /// <summary>
    /// CMS's ICD-10-CM order file (icd10cm_order_YYYY.txt): fixed width, the order number (5), the code (7, no decimal
    /// point), the header flag (1: 0 header, 1 billable), the short title (60), and the long title.
    /// </summary>
    public static Dictionary<string, IcdCode> ParseIcd(string text)
    {
        var codes = new Dictionary<string, IcdCode>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // An order number (five digits) and a space start every line of the order file.
            if (line.Length < 16 || !line[..5].All(char.IsDigit) || line[5] != ' ') continue;
            var code = line[6..13].Trim();
            if (code.Length == 0) continue;
            codes[code] = new IcdCode(code, line[14] == '1', line.Length > 16 ? line[16..Math.Min(line.Length, 76)].Trim() : "");
        }
        if (codes.Count == 0) throw new FormatException("That isn't an ICD-10-CM order file (icd10cm_order_YYYY.txt).");
        return codes;
    }

    /// <summary>
    /// CMS's HCPCS alpha-numeric file (HCPCYYYY_QQQ_ANWEB.txt): 320-character records. The code is in 1–5, the record
    /// type in 11 (3 a code's first line, 7 a modifier's, with the modifier in 4–5), the long title in 12–91, the
    /// short one in 92–119, and the added, effective and termination dates (CCYYMMDD) in 269–292.
    /// </summary>
    public static (Dictionary<string, HcpcsCode> Codes, HashSet<string> Modifiers) ParseHcpcs(string text)
    {
        var codes = new Dictionary<string, HcpcsCode>(StringComparer.OrdinalIgnoreCase);
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 293) continue;
            if (line[10] == '7' && line[..3].Trim().Length == 0) modifiers.Add(line[3..5].Trim());
            if (line[10] != '3') continue;
            var code = line[..5].Trim();
            codes[code] = new HcpcsCode(code, line[91..119].Trim(), Date(line[268..276]), Date(line[276..284]), Date(line[284..292]));
        }
        if (codes.Count == 0) throw new FormatException("That isn't a HCPCS alpha-numeric file (HCPCYYYY_QQQ_ANWEB.txt).");
        return (codes, modifiers);
    }

    /// <summary>NUCC's taxonomy CSV: Code, Grouping, Classification, Specialization, Definition, Notes, Display Name, Section.</summary>
    public static Dictionary<string, string> ParseTaxonomy(string text)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Csv(text).Skip(1))
            if (row.Count >= 7 && row[0].Length == 10) codes[row[0]] = row[6].Length > 0 ? row[6] : row[2];
        if (codes.Count == 0) throw new FormatException("That isn't NUCC's taxonomy CSV.");
        return codes;
    }

    /// <summary>A licensed CPT file the person has: CSV or tab-separated, the code then its description.</summary>
    public static Dictionary<string, string> ParseCpt(string text)
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split(raw.Contains('\t') ? '\t' : ',', 2);
            var code = parts[0].Trim().Trim('"');
            if (code.Length == 5 && (code.All(char.IsDigit) || char.IsDigit(code[0]) && code[4] is 'F' or 'T' or 'U')) codes[code] = parts.Length > 1 ? parts[1].Trim().Trim('"') : "";
        }
        if (codes.Count == 0) throw new FormatException("No CPT codes found: give a CSV or tab-separated file of code and description.");
        return codes;
    }

    private static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>A small CSV reader (quoted fields, doubled quotes, commas and line breaks inside quotes).</summary>
    public static IEnumerable<List<string>> Csv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row;
                    row = [];
                    break;
                default: field.Append(ch); break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    // ----- The store: the files kept in the settings folder -----

    /// <summary>
    /// Loads what's in a code-set folder: icd10cm.txt, hcpcs.txt, taxonomy.csv and cpt.txt (whichever are there), and
    /// manifest.json saying where each came from. Files are kept as their sources give them, so they can be checked.
    /// </summary>
    public static CodeSets Load(string folder)
    {
        var sets = new CodeSets();
        if (!Directory.Exists(folder)) return sets;
        var manifest = ReadManifest(folder);
        CodeSetInfo Info(string file, string name, int count) =>
            manifest.GetValueOrDefault(file) is { } m ? m with { Count = count } : new CodeSetInfo(name, "unknown", "a file put there by hand", File.GetLastWriteTimeUtc(Path.Combine(folder, file)), count);
        string? Read(string file) => File.Exists(Path.Combine(folder, file)) ? File.ReadAllText(Path.Combine(folder, file), Encoding.Latin1) : null;
        if (Read("icd10cm.txt") is { } icd) { sets.Icd = ParseIcd(icd); sets.Info.Add(Info("icd10cm.txt", "ICD-10-CM", sets.Icd.Count)); }
        if (Read("hcpcs.txt") is { } hcpcs) { (sets.Hcpcs, sets.HcpcsModifiers) = ParseHcpcs(hcpcs); sets.Info.Add(Info("hcpcs.txt", "HCPCS Level II", sets.Hcpcs.Count)); }
        if (Read("taxonomy.csv") is { } tax) { sets.Taxonomy = ParseTaxonomy(File.ReadAllText(Path.Combine(folder, "taxonomy.csv"), Encoding.UTF8)); sets.Info.Add(Info("taxonomy.csv", "NUCC provider taxonomy", sets.Taxonomy.Count)); }
        if (Read("cpt.txt") is { } cpt) { sets.Cpt = ParseCpt(cpt); sets.Info.Add(Info("cpt.txt", "CPT (licensed)", sets.Cpt.Count)); }
        return sets;
    }

    private static Dictionary<string, CodeSetInfo> ReadManifest(string folder)
    {
        var path = Path.Combine(folder, "manifest.json");
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CodeSetInfo>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Saves a code-set file into the folder (as given, or from inside a zip), and notes where it came from.</summary>
    public static void Store(string folder, string file, byte[] content, string name, string version, string source, Func<string, bool>? pickFromZip = null)
    {
        Directory.CreateDirectory(folder);
        if (pickFromZip != null)
        {
            using var zip = new ZipArchive(new MemoryStream(content));
            var entry = zip.Entries.FirstOrDefault(e => pickFromZip(e.FullName)) ?? throw new FormatException($"The download for {name} doesn't hold the file expected.");
            using var s = entry.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            content = m.ToArray();
            version = $"{version} ({Path.GetFileName(entry.FullName)})";
        }
        File.WriteAllBytes(Path.Combine(folder, file), content);
        var manifest = ReadManifest(folder);
        manifest[file] = new CodeSetInfo(name, version, source, DateTimeOffset.UtcNow, 0);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
