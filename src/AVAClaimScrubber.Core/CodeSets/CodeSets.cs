using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AVAClaimScrubber.Core.CodeSets;

/// <summary>An ICD-10-CM code: whether it's billable (a full code) or a header (a category with codes under it), and its short title.</summary>
public sealed record IcdCode(string Code, bool Billable, string Title);

/// <summary>A HCPCS Level II code: its title and the dates it was added, took its current form, and ended (if it has).</summary>
public sealed record HcpcsCode(string Code, string Title, DateOnly? Added, DateOnly? Effective, DateOnly? Terminated);

/// <summary>
/// An NCCI procedure-to-procedure edit: the column 2 code isn't paid with the column 1 code on the same day, unless the
/// modifier indicator is 1 and an NCCI-associated modifier shows the services were distinct.
/// </summary>
public sealed record PtpEdit(string Column1, string Column2, DateOnly? Effective, DateOnly? Deleted, char Modifier);

/// <summary>A medically unlikely edit: the most units of a code one patient gets, per line (MAI 1) or per day (MAI 2, 3).</summary>
public sealed record MueEdit(string Code, int Units, int Mai, string Rationale);

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
    /// <summary>NCCI PTP edits by "column1|column2".</summary>
    public Dictionary<string, PtpEdit>? Ptp { get; set; }
    /// <summary>Medically unlikely edits for professional claims (the practitioner table) and institutional ones (outpatient hospital).</summary>
    public Dictionary<string, MueEdit>? MuePractitioner { get; set; }
    public Dictionary<string, MueEdit>? MueHospital { get; set; }
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

    /// <summary>
    /// CMS's NCCI PTP files (text, tab-separated, after their notice and heading lines): column 1, column 2, a
    /// pre-1996 flag, the effective date, the deletion date (* for none) and the modifier indicator (0, 1 or 9).
    /// </summary>
    public static Dictionary<string, PtpEdit> ParsePtp(string text, Dictionary<string, PtpEdit>? into = null)
    {
        var edits = into ?? new Dictionary<string, PtpEdit>(StringComparer.OrdinalIgnoreCase);
        var before = edits.Count;
        foreach (var raw in text.Split('\n'))
        {
            var f = raw.TrimEnd('\r').Split(raw.Contains('\t') ? '\t' : ',').Select(x => x.Trim().Trim('"')).ToArray();
            if (f.Length < 6 || f[0].Length != 5 || f[1].Length != 5 || Date(f[3]) is not { } effective || f[5].Length != 1 || f[5][0] is not ('0' or '1' or '9')) continue;
            edits[f[0] + "|" + f[1]] = new PtpEdit(f[0], f[1], effective, Date(f[4]), f[5][0]);
        }
        if (edits.Count == before) throw new FormatException("No NCCI PTP edits found: give CMS's PTP edit file (tab-separated text).");
        return edits;
    }

    /// <summary>CMS's MUE table (CSV, after its notice lines): code, MUE value, the MUE adjudication indicator (\"1 Line Edit\", \"3 Date of Service Edit: Clinical\"…), rationale.</summary>
    public static Dictionary<string, MueEdit> ParseMue(string text)
    {
        var edits = new Dictionary<string, MueEdit>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Csv(text))
        {
            if (row.Count < 3 || row[0].Trim().Length != 5 || !int.TryParse(row[1].Trim(), out var units) || row[2].Trim().Length == 0 || !char.IsDigit(row[2].Trim()[0])) continue;
            edits[row[0].Trim()] = new MueEdit(row[0].Trim(), units, row[2].Trim()[0] - '0', row.Count > 3 ? row[3].Trim() : "");
        }
        if (edits.Count == 0) throw new FormatException("No MUEs found: give CMS's MUE table (CSV).");
        return edits;
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
        foreach (var ptp in Directory.EnumerateFiles(folder, "ncci-ptp*.txt").Order(StringComparer.Ordinal))
        {
            sets.Ptp = ParsePtp(File.ReadAllText(ptp, Encoding.Latin1), sets.Ptp);
        }
        if (sets.Ptp != null) sets.Info.Add(Info(Directory.EnumerateFiles(folder, "ncci-ptp*.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal).First()!, "NCCI PTP edits", sets.Ptp.Count));
        if (Read("mue-practitioner.csv") is { } mp) { sets.MuePractitioner = ParseMue(mp); sets.Info.Add(Info("mue-practitioner.csv", "MUE (practitioner)", sets.MuePractitioner.Count)); }
        if (Read("mue-hospital.csv") is { } mh) { sets.MueHospital = ParseMue(mh); sets.Info.Add(Info("mue-hospital.csv", "MUE (outpatient hospital)", sets.MueHospital.Count)); }
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
