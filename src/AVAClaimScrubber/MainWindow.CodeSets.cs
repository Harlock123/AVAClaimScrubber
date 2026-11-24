using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AVAClaimScrubber.Core.CodeSets;

namespace AVAClaimScrubber;

/// <summary>The Code Sets tab: what's loaded, from where and when; Update downloads the public sets; Import takes a file.</summary>
public sealed partial class MainWindow
{
    public StackPanel CodeSetsPanel { get; } = new() { Margin = new Thickness(20, 14), Spacing = 6 };

    /// <summary>The code sets the checks use, loaded from <see cref="CodeSetsFolder"/>.</summary>
    public CodeSets CodeSets { get; private set; } = new();

    public string CodeSetsFolder => Path.Combine(SettingsFolder, "CodeSets");

    /// <summary>Asks for a file to import as a code set; tests answer it themselves.</summary>
    public Func<string, Task<string?>> PickCodeSetFile { get; set; } = null!;

    /// <summary>The HTTP client Update downloads with (tests give a fake one, or none).</summary>
    public HttpClient? Downloads { get; set; }

    private void LoadCodeSets()
    {
        try
        {
            CodeSets = CodeSets.Load(CodeSetsFolder);
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            CodeSets = new CodeSets();
            Say("A code set in the settings folder doesn't read: " + e.Message);
        }
    }

    private Control BuildCodeSetsPage()
    {
        PickCodeSetFile = async title =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title });
            return picked.FirstOrDefault()?.TryGetLocalPath();
        };
        return new ScrollViewer { Content = CodeSetsPanel };
    }

    /// <summary>Downloads the public code sets (CMS's ICD-10-CM and HCPCS, NUCC's taxonomy), then checks everything again.</summary>
    public async Task UpdateCodeSets()
    {
        var http = Downloads ?? new HttpClient();
        if (Downloads == null) http.DefaultRequestHeaders.UserAgent.ParseAdd("AVAClaimScrubber/" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1"));
        try
        {
            var said = await Updater.UpdateAll(CodeSetsFolder, http, DateOnly.FromDateTime(DateTime.Today), new Progress<string>(Say));
            LoadCodeSets();
            Recheck();
            ShowCodeSets();
            Say(string.Join("  ", said));
        }
        finally
        {
            if (Downloads == null) http.Dispose();
        }
    }

    /// <summary>Takes a code-set file the person has (kind: icd, hcpcs, taxonomy or cpt), keeps it, and checks everything again.</summary>
    public void ImportCodeSet(string kind, string path)
    {
        var (file, name) = kind switch
        {
            "icd" => ("icd10cm.txt", "ICD-10-CM"),
            "hcpcs" => ("hcpcs.txt", "HCPCS Level II"),
            "taxonomy" => ("taxonomy.csv", "NUCC provider taxonomy"),
            "cpt" => ("cpt.txt", "CPT (licensed)"),
            "ncci" => ($"ncci-ptp-{Directory.EnumerateFiles(CodeSetsFolder, "ncci-ptp*.txt").Count() + 1}.txt", "NCCI PTP edits"),
            "mue" => ("mue-practitioner.csv", "MUE (practitioner)"),
            "mue-hospital" => ("mue-hospital.csv", "MUE (outpatient hospital)"),
            _ => throw new ArgumentException("kind is icd, hcpcs, taxonomy, cpt, ncci, mue or mue-hospital."),
        };
        try
        {
            Directory.CreateDirectory(CodeSetsFolder);
            var bytes = File.ReadAllBytes(path);
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            _ = kind switch
            {
                "icd" => CodeSets.ParseIcd(text).Count,
                "hcpcs" => CodeSets.ParseHcpcs(text).Codes.Count,
                "taxonomy" => CodeSets.ParseTaxonomy(System.Text.Encoding.UTF8.GetString(bytes)).Count,
                "ncci" => CodeSets.ParsePtp(text).Count,
                "mue" or "mue-hospital" => CodeSets.ParseMue(text).Count,
                _ => CodeSets.ParseCpt(text).Count,
            };
            CodeSets.Store(CodeSetsFolder, file, bytes, name, "imported", Path.GetFileName(path));
            LoadCodeSets();
            Recheck();
            ShowCodeSets();
            Say($"Imported {name} from {Path.GetFileName(path)}.");
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            Say($"That file isn't {name}: {e.Message}");
        }
    }

    public void ShowCodeSets()
    {
        CodeSetsPanel.Children.Clear();
        CodeSetsPanel.Children.Add(new TextBlock { Text = "Code sets", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Look.Text });
        CodeSetsPanel.Children.Add(new TextBlock
        {
            Text = "Diagnosis and procedure codes are checked against these. Update downloads the public ones from their publishers (CMS's ICD-10-CM and HCPCS Level II, " +
                   "CMS's MUE tables, NUCC's provider taxonomy): only when you press it, and only those requests leave this computer. CMS's NCCI PTP edits are behind " +
                   "the AMA's licence page on cms.gov: accept it there, download the practitioner (or hospital) files, and import each. CPT belongs to the AMA: without a " +
                   "licensed CPT file, CPT codes get a shape check only. Places of service are built in.",
            TextWrapping = TextWrapping.Wrap, Foreground = Look.Dim, FontSize = 12.5, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left,
        });
        var bar = new WrapPanel();
        var update = new Button { Content = "Update (download)", Margin = new Thickness(0, 6, 8, 6) };
        update.Click += async (_, _) => await UpdateCodeSets();
        bar.Children.Add(update);
        foreach (var (kind, label) in new[] { ("icd", "Import ICD-10-CM…"), ("hcpcs", "Import HCPCS…"), ("taxonomy", "Import Taxonomy…"), ("ncci", "Import NCCI PTP…"), ("mue", "Import MUE…"), ("cpt", "Load Licensed CPT…") })
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 6, 8, 6) };
            var k = kind;
            b.Click += async (_, _) =>
            {
                if (await PickCodeSetFile(label.TrimEnd('…')) is { } p) ImportCodeSet(k, p);
            };
            bar.Children.Add(b);
        }
        CodeSetsPanel.Children.Add(bar);

        var rows = new List<(string Name, string Version, string Source, string Loaded, string Count)>();
        foreach (var name in new[] { "ICD-10-CM", "HCPCS Level II", "NUCC provider taxonomy", "NCCI PTP edits", "MUE (practitioner)", "MUE (outpatient hospital)", "CPT (licensed)" })
        {
            var info = CodeSets.Info.FirstOrDefault(i => i.Name == name);
            rows.Add(info == null
                ? (name, name.StartsWith("CPT", StringComparison.Ordinal) ? "not loaded (shape check only)" : "not loaded", "", "", "")
                : (name, info.Version, info.Source, info.Loaded.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), info.Count.ToString("N0", CultureInfo.InvariantCulture)));
        }
        rows.Add(("Place of service", "built in", PlaceOfService.Source, "", PlaceOfService.Codes.Count.ToString(CultureInfo.InvariantCulture)));
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var _ in Enumerable.Range(0, 5)) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        string[] heads = ["Code set", "Version", "Codes", "Loaded", "From"];
        var r = 0;
        void Row(string[] cells, bool head)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < cells.Length; i++)
            {
                var t = new TextBlock
                {
                    Text = cells[i], FontSize = head ? 11.5 : 12.5, Margin = new Thickness(0, 3, 22, 3), MaxWidth = i == 4 ? 520 : double.PositiveInfinity, TextWrapping = TextWrapping.Wrap,
                    Foreground = head ? Look.Heading : i == 1 && cells[i].StartsWith("not loaded", StringComparison.Ordinal) ? Look.Warn : i == 0 ? Look.Text : Look.Code,
                };
                Grid.SetRow(t, r);
                Grid.SetColumn(t, i);
                grid.Children.Add(t);
            }
            r++;
        }
        Row(heads, true);
        foreach (var x in rows) Row([x.Name, x.Version, x.Count, x.Loaded, x.Source], false);
        CodeSetsPanel.Children.Add(grid);
    }
}
