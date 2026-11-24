using System.Text;
using AVAClaimScrubber.Core.X12;

namespace AVAClaimScrubber.Core.Checks;

/// <summary>A change made in a corrected copy: where, what it was, what it is now, and why.</summary>
public sealed record Change(int SegmentIndex, string Element, string Was, string Now, string Why);

/// <summary>
/// Writes a corrected copy of an 837: the fixes the findings suggest (or the ones chosen), applied to the segments they
/// point at, and nothing else; the separators, line breaks and every other segment as they were. The original file is
/// never changed: the copy is a new file, and every change is listed so a person can check it before it's sent.
/// </summary>
public static class Corrector
{
    public static (string Text, List<Change> Changes) Apply(string original, X12File file, IEnumerable<Finding> findings)
    {
        var d = file.Delimiters;
        var texts = file.Segments.Select(s => s.Text).ToArray();
        var changes = new List<Change>();
        foreach (var f in findings.Where(f => f.Fix != null).GroupBy(f => (f.Fix!.SegmentIndex, f.Fix.Element, f.Fix.Component)).Select(g => g.First()))
        {
            var fix = f.Fix!;
            if (fix.SegmentIndex < 0 || fix.SegmentIndex >= texts.Length) continue;
            var elements = texts[fix.SegmentIndex].Split(d.Element);
            if (fix.Element >= elements.Length) continue;
            var was = elements[fix.Element];
            string now;
            if (fix.Component > 0)
            {
                var parts = was.Split(d.Component);
                if (fix.Component > parts.Length) continue;
                parts[fix.Component - 1] = fix.NewValue;
                now = string.Join(d.Component, parts);
            }
            else now = fix.NewValue;
            if (now == was) continue;
            elements[fix.Element] = now;
            texts[fix.SegmentIndex] = string.Join(d.Element, elements);
            changes.Add(new Change(fix.SegmentIndex, $"{elements[0]}{fix.Element:00}" + (fix.Component > 0 ? $"-{fix.Component}" : ""), was, now, $"{f.Rule.Id}: {fix.Description}"));
        }
        // Keep the original's layout: a line break after each terminator if it had them.
        var newline = original.Contains(d.Segment + "\r\n", StringComparison.Ordinal) ? "\r\n" : original.Contains(d.Segment + "\n", StringComparison.Ordinal) ? "\n" : "";
        var sb = new StringBuilder();
        foreach (var t in texts)
        {
            sb.Append(t);
            if (!d.LineTerminated) sb.Append(d.Segment);
            sb.Append(d.LineTerminated ? "\n" : newline);
        }
        return (sb.ToString(), changes);
    }
}
