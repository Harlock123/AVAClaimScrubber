using System.Text.RegularExpressions;

namespace AVAClaimScrubber.Core.CodeSets;

/// <summary>
/// Downloads the public code sets from their publishers, only when asked (the Update button): ICD-10-CM from CMS (the
/// fiscal year's order file: a new year starts each October 1), HCPCS Level II from CMS (the newest quarterly file
/// that's out), and NUCC's taxonomy CSV (the newest version NUCC lists). Nothing is sent but the requests themselves.
/// Each download is saved as given, with where it came from, so a report can say which version it checked against.
/// </summary>
public static partial class Updater
{
    public const string IcdPage = "https://www.cms.gov/medicare/coding-billing/icd-10-codes";
    public const string HcpcsPage = "https://www.cms.gov/medicare/coding-billing/healthcare-common-procedure-system/quarterly-update";
    public const string NuccPage = "https://www.nucc.org/index.php/code-sets-mainmenu-41/provider-taxonomy-mainmenu-40/csv-mainmenu-57";

    /// <summary>The ICD-10-CM fiscal year in effect on a day (FY2027 runs from October 1, 2026).</summary>
    public static int FiscalYear(DateOnly day) => day.Month >= 10 ? day.Year + 1 : day.Year;

    /// <summary>Downloads every set it can into <paramref name="folder"/>; returns a line for each, saying what it got or why not.</summary>
    public static async Task<List<string>> UpdateAll(string folder, HttpClient http, DateOnly today, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var said = new List<string>();
        async Task Try(string name, Func<Task<string>> work)
        {
            progress?.Report($"Downloading {name}…");
            try
            {
                said.Add(await work());
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or IOException or TaskCanceledException or InvalidDataException)
            {
                said.Add($"{name}: couldn't download it ({e.Message}).");
            }
        }
        await Try("ICD-10-CM", async () =>
        {
            var fy = FiscalYear(today);
            var url = $"https://www.cms.gov/files/zip/{fy}-code-descriptions-tabular-order.zip";
            var bytes = await http.GetByteArrayAsync(url, cancel);
            CodeSets.Store(folder, "icd10cm.txt", bytes, "ICD-10-CM", $"FY{fy}", url, n => Regex.IsMatch(Path.GetFileName(n), @"^icd10cm_order_\d{4}\.txt$", RegexOptions.IgnoreCase));
            return $"ICD-10-CM: FY{fy}, from CMS.";
        });
        await Try("HCPCS Level II", async () =>
        {
            // The newest quarter that's out: try this quarter's file, then the ones before it.
            string[] months = ["january", "april", "july", "october"];
            var quarter = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(3);
            for (var i = 0; i < 5; i++, quarter = quarter.AddMonths(-3))
            {
                var url = $"https://www.cms.gov/files/zip/{months[(quarter.Month - 1) / 3]}-{quarter.Year}-alpha-numeric-hcpcs-file.zip";
                using var response = await http.GetAsync(url, cancel);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
                CodeSets.Store(folder, "hcpcs.txt", bytes, "HCPCS Level II", $"{months[(quarter.Month - 1) / 3]} {quarter.Year}", url, n => Regex.IsMatch(Path.GetFileName(n), @"ANWEB.*\.txt$", RegexOptions.IgnoreCase));
                return $"HCPCS Level II: {months[(quarter.Month - 1) / 3]} {quarter.Year}, from CMS.";
            }
            throw new HttpRequestException("none of the last five quarters' files could be found");
        });
        await Try("NUCC provider taxonomy", async () =>
        {
            var page = await http.GetStringAsync(NuccPage, cancel);
            var link = CsvLink().Match(page);
            if (!link.Success) throw new FormatException("NUCC's page lists no taxonomy CSV");
            var url = new Uri(new Uri(NuccPage), link.Groups[1].Value).ToString();
            var bytes = await http.GetByteArrayAsync(url, cancel);
            CodeSets.Store(folder, "taxonomy.csv", bytes, "NUCC provider taxonomy", "version " + link.Groups[2].Value, url);
            return $"NUCC provider taxonomy: version {link.Groups[2].Value}.";
        });
        return said;
    }

    [GeneratedRegex(@"href=""([^""]*nucc_taxonomy_(\d+)\.csv)""", RegexOptions.IgnoreCase)]
    private static partial Regex CsvLink();
}
