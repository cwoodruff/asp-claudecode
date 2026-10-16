using System.Text;

namespace Intelligently.Infrastructure.Agents.Tasks;

/// <summary>
/// A small line-based unified diff (longest common subsequence, three lines of context) for the
/// learner-facing fix: enough to show which lines of a lab file the agent changed, without a package.
/// </summary>
public static class UnifiedDiff
{
    private const int Context = 3;
    private const char Newline = (char)10;

    /// <summary>The unified diff of <paramref name="oldText"/> to <paramref name="newText"/> for one file, or null when they are identical.</summary>
    public static string? Create(string path, string? oldText, string? newText)
    {
        // Identical text (newline style aside) needs no table: unchanged files are the common case.
        if (string.Equals(Normalize(oldText), Normalize(newText), StringComparison.Ordinal))
            return null;

        var oldLines = Lines(oldText);
        var newLines = Lines(newText);
        var edits = Edits(oldLines, newLines);
        if (edits.All(e => e.Kind == EditKind.Equal))
            return null;

        var sb = new StringBuilder();
        // Always LF: the diff is shown and compared as text, whatever the host's newline.
        sb.Append("--- ").Append(oldText is null ? "/dev/null" : $"a/{path}").Append(Newline);
        sb.Append("+++ ").Append(newText is null ? "/dev/null" : $"b/{path}").Append(Newline);

        // Group edits into hunks: a run of changes with up to Context equal lines on each side.
        var i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Equal) { i++; continue; }

            var start = Math.Max(0, i - Context);
            var end = i;
            while (end < edits.Count)
            {
                // Extend to the next change if it is within 2 * Context equal lines.
                var nextChange = end;
                while (nextChange < edits.Count && edits[nextChange].Kind != EditKind.Equal) nextChange++;
                var gap = nextChange;
                while (gap < edits.Count && edits[gap].Kind == EditKind.Equal && gap - nextChange < 2 * Context) gap++;
                if (gap < edits.Count && edits[gap].Kind != EditKind.Equal)
                {
                    end = gap;
                    continue;
                }
                end = Math.Min(edits.Count, nextChange + Context);
                break;
            }

            var hunk = edits.GetRange(start, end - start);
            var oldStart = hunk[0].OldIndex + 1;
            var newStart = hunk[0].NewIndex + 1;
            var oldCount = hunk.Count(e => e.Kind != EditKind.Insert);
            var newCount = hunk.Count(e => e.Kind != EditKind.Delete);
            sb.Append("@@ -").Append(oldCount == 0 ? oldStart - 1 : oldStart).Append(',').Append(oldCount)
              .Append(" +").Append(newCount == 0 ? newStart - 1 : newStart).Append(',').Append(newCount).Append(" @@").Append(Newline);
            foreach (var edit in hunk)
            {
                sb.Append(edit.Kind switch { EditKind.Insert => '+', EditKind.Delete => '-', _ => ' ' }).Append(edit.Text).Append(Newline);
            }
            i = end;
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private enum EditKind { Equal, Insert, Delete }

    private readonly record struct Edit(EditKind Kind, string Text, int OldIndex, int NewIndex);

    private static string? Normalize(string? text) => text?.Replace("\r\n", "\n");

    private static List<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = Normalize(text)!.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Edit script from an LCS table; the files are small (lab sources), so O(n·m) is fine.</summary>
    private static List<Edit> Edits(List<string> a, List<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var edits = new List<Edit>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                edits.Add(new Edit(EditKind.Equal, a[x], x, y));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                edits.Add(new Edit(EditKind.Delete, a[x], x, y));
                x++;
            }
            else
            {
                edits.Add(new Edit(EditKind.Insert, b[y], x, y));
                y++;
            }
        }
        while (x < n) { edits.Add(new Edit(EditKind.Delete, a[x], x, y)); x++; }
        while (y < m) { edits.Add(new Edit(EditKind.Insert, b[y], x, y)); y++; }
        return edits;
    }
}
