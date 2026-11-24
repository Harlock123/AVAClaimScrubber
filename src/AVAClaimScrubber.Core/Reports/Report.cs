using System.Globalization;
using System.Net;
using System.Text;
using AVAClaimScrubber.Core.Checks;
using AVAClaimScrubber.Core.Claims;
using AVAClaimScrubber.Core.Responses;
using ClosedXML.Excel;

namespace AVAClaimScrubber.Core.Reports;

/// <summary>
/// The scrub as a report for people: an HTML page (one file, no scripts) or an Excel workbook (Summary, Claims,
/// Findings, Rules and, with responses, Denials). Reports carry claim account numbers, so they say to handle them as
/// the claims are handled; the findings' messages carry no names, dates of birth or member IDs.
/// </summary>
public static class Report
{
    public const string Notice = "This report lists claims by their account numbers (CLM01): handle it as you handle the claims.";

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static string Status(ScrubResult r, Claim c) => r.Status(c) is { } s ? s.ToString() : "Clean";

    public static string Html(IReadOnlyList<ScrubResult> results, IReadOnlyList<ReplayReport> replays, DateTime made)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Claim scrub report</title><style>
            body{font:14px/1.45 system-ui,sans-serif;margin:24px;color:#1d2430;background:#fff}h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}
            .dim{color:#5b6575}table{border-collapse:collapse;width:100%;margin:6px 0 14px}th,td{border-bottom:1px solid #dde2ea;padding:5px 8px;text-align:left;vertical-align:top}
            th{background:#f2f4f8;font-weight:600}td.n{text-align:right}code{font:12.5px ui-monospace,monospace}.Reject{color:#b3261e;font-weight:600}.Deny{color:#a85400;font-weight:600}
            .Warn{color:#8a6d00}.Info{color:#4b5b74}.Clean,.Caught{color:#1d7a3a;font-weight:600}.Missed{color:#b3261e;font-weight:600}.NotCheckable,.NoClaim{color:#5b6575}
            </style></head><body>
            """);
        sb.Append("<h1>Claim scrub report</h1>");
        sb.Append($"<p class=\"dim\">Made {made:yyyy-MM-dd HH:mm} by AVA Claim Scrubber. {H(Notice)}</p>");
        foreach (var r in results)
        {
            var claims = r.Batch.Claims;
            sb.Append($"<h2>{H(r.Batch.Name)}</h2><p>{claims.Count} claims: {claims.Count(c => r.Status(c) == null)} clean, " +
                      $"{claims.Count(c => r.Status(c) is Severity.Reject or Severity.Deny)} would be rejected or denied; {r.Findings.Count} findings.</p>");
            sb.Append("<table><tr><th>#</th><th>Account</th><th>Kind</th><th>Charge</th><th>Lines</th><th>Status</th><th>Findings</th></tr>");
            foreach (var c in claims)
                sb.Append($"<tr><td class=\"n\">{c.Number}</td><td><code>{H(c.Account)}</code></td><td>{c.Kind}</td><td class=\"n\">{c.Total:0.00}</td><td class=\"n\">{c.Lines.Count}</td>" +
                          $"<td class=\"{Status(r, c)}\">{Status(r, c)}</td><td class=\"n\">{r.Of(c).Count()}</td></tr>");
            sb.Append("</table>");
            if (r.Findings.Count == 0) continue;
            sb.Append("<table><tr><th>Severity</th><th>Rule</th><th>Claim</th><th>Where</th><th>Finding</th><th>Fix</th></tr>");
            foreach (var f in r.Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Claim).ThenBy(f => f.Line))
                sb.Append($"<tr><td class=\"{f.Severity}\">{f.Severity}</td><td><code>{H(f.Rule.Id)}</code> {H(f.Rule.Title)}</td>" +
                          $"<td>{(f.Claim > 0 ? H(claims[f.Claim - 1].Account) : "envelope")}</td><td>{H(f.Place)}</td><td>{H(f.Message)}</td><td>{H(f.Fix?.Description ?? "")}</td></tr>");
            sb.Append("</table>");
        }
        foreach (var rep in replays)
        {
            sb.Append($"<h2>Denial replay: {H(rep.Response.Name)}</h2><p>{H(rep.Response.KindName)}: {H(rep.Response.Summary)}. {H(rep.Summary)}</p>");
            sb.Append("<table><tr><th>Verdict</th><th>Claim</th><th>Line</th><th>Reason</th><th>Amount</th><th>Explanation</th></tr>");
            foreach (var row in rep.Rows)
                sb.Append($"<tr><td class=\"{row.Verdict}\">{Verdict(row.Verdict)}</td><td><code>{H(row.Denial.Account.Length > 0 ? row.Denial.Account : "batch")}</code></td>" +
                          $"<td>{(row.Denial.Line > 0 ? $"{row.Denial.Line} {H(row.Denial.Procedure)}" : "")}</td><td>{H(row.Denial.Description)}</td>" +
                          $"<td class=\"n\">{(row.Denial.Amount > 0 ? row.Denial.Amount.ToString("0.00", CultureInfo.InvariantCulture) : "")}</td><td>{H(row.Explanation)}</td></tr>");
            sb.Append("</table>");
        }
        var rules = results.SelectMany(r => r.Findings.Select(f => f.Rule)).DistinctBy(r => r.Id).OrderBy(r => r.Layer).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        if (rules.Count > 0)
        {
            sb.Append("<h2>The rules behind the findings</h2><table><tr><th>Rule</th><th>Layer</th><th>Why</th><th>Source</th></tr>");
            foreach (var r in rules) sb.Append($"<tr><td><code>{H(r.Id)}</code> {H(r.Title)}</td><td class=\"n\">{r.Layer}</td><td>{H(r.Why)}</td><td>{H(r.Source)}</td></tr>");
            sb.Append("</table>");
        }
        if (replays.Count > 0) sb.Append($"<p class=\"dim\">Reason codes as published: {H(ResponseCodes.Source)}.</p>");
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    public static string Verdict(Verdict v) => v switch { Responses.Verdict.NotCheckable => "Not checkable", Responses.Verdict.NoClaim => "No claim open", _ => v.ToString() };

    public static void Excel(string path, IReadOnlyList<ScrubResult> results, IReadOnlyList<ReplayReport> replays, DateTime made)
    {
        using var book = new XLWorkbook();
        var summary = book.AddWorksheet("Summary");
        summary.Cell(1, 1).Value = "Claim scrub report";
        summary.Cell(1, 1).Style.Font.Bold = true;
        summary.Cell(2, 1).Value = $"Made {made:yyyy-MM-dd HH:mm} by AVA Claim Scrubber. {Notice}";
        Table(summary, 4, ["File", "Claims", "Clean", "Rejected or denied", "Findings"],
            results.Select(r => new object[] { r.Batch.Name, r.Batch.Claims.Count, r.Batch.Claims.Count(c => r.Status(c) == null), r.Batch.Claims.Count(c => r.Status(c) is Severity.Reject or Severity.Deny), r.Findings.Count }));

        Table(book.AddWorksheet("Claims"), 1, ["File", "#", "Account", "Kind", "Charge", "Lines", "Status", "Findings"],
            results.SelectMany(r => r.Batch.Claims.Select(c => new object[] { r.Batch.Name, c.Number, c.Account, c.Kind.ToString(), c.Total ?? 0m, c.Lines.Count, Status(r, c), r.Of(c).Count() })));
        Table(book.AddWorksheet("Findings"), 1, ["File", "Severity", "Rule", "Title", "Claim", "Line", "Where", "Finding", "Fix"],
            results.SelectMany(r => r.Findings.Select(f => new object[]
            {
                r.Batch.Name, f.Severity.ToString(), f.Rule.Id, f.Rule.Title, f.Claim > 0 ? r.Batch.Claims[f.Claim - 1].Account : "envelope", f.Line, f.Place, f.Message, f.Fix?.Description ?? "",
            })));
        Table(book.AddWorksheet("Rules"), 1, ["Rule", "Layer", "Title", "Severity", "Why", "Source"],
            results.SelectMany(r => r.Findings.Select(f => f.Rule)).DistinctBy(r => r.Id).OrderBy(r => r.Layer).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new object[] { r.Id, r.Layer, r.Title, r.Severity.ToString(), r.Why, r.Source }));
        if (replays.Count > 0)
            Table(book.AddWorksheet("Denials"), 1, ["Response", "Verdict", "Claim", "Line", "Procedure", "Code", "Reason", "Amount", "Caught by", "Explanation"],
                replays.SelectMany(rep => rep.Rows.Select(row => new object[]
                {
                    rep.Response.Name, Verdict(row.Verdict), row.Denial.Account.Length > 0 ? row.Denial.Account : "batch", row.Denial.Line, row.Denial.Procedure, row.Denial.Code,
                    row.Denial.Description, row.Denial.Amount, string.Join(", ", row.CaughtBy.Select(f => f.Rule.Id).Distinct()), row.Explanation,
                })));
        book.SaveAs(path);
    }

    private static void Table(IXLWorksheet sheet, int row, string[] heads, IEnumerable<object[]> rows)
    {
        for (var i = 0; i < heads.Length; i++)
        {
            sheet.Cell(row, i + 1).Value = heads[i];
            sheet.Cell(row, i + 1).Style.Font.Bold = true;
            sheet.Cell(row, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F4F8");
        }
        var r = row + 1;
        foreach (var values in rows)
        {
            for (var i = 0; i < values.Length; i++)
                sheet.Cell(r, i + 1).Value = values[i] switch
                {
                    int n => n, decimal d => d, string s => s, var o => o?.ToString() ?? "",
                };
            r++;
        }
        sheet.SheetView.FreezeRows(row);
        sheet.Columns(1, heads.Length).AdjustToContents(row, Math.Min(r, row + 200));
        foreach (var c in sheet.Columns(1, heads.Length)) if (c.Width > 70) { c.Width = 70; c.Style.Alignment.WrapText = true; }
    }
}
