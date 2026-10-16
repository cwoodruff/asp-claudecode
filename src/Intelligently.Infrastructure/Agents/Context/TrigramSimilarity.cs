using System.Text;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Character-trigram Jaccard similarity, the provider-neutral similarity used for episodic recall in
/// this phase (works on SQL Server and SQLite alike; embeddings arrive in Phase 5).
/// </summary>
public static class TrigramSimilarity
{
    /// <summary>Similarity in [0, 1]; 1 when both normalized texts are identical.</summary>
    public static double Compute(string a, string b)
    {
        var ta = Trigrams(a);
        var tb = Trigrams(b);
        if (ta.Count == 0 && tb.Count == 0) return 1;
        if (ta.Count == 0 || tb.Count == 0) return 0;

        var intersection = ta.Count(tb.Contains);
        var union = ta.Count + tb.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    public static HashSet<string> Trigrams(string text)
    {
        var normalized = Normalize(text);
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.Length < 3)
        {
            if (normalized.Length > 0) set.Add(normalized);
            return set;
        }
        for (var i = 0; i + 3 <= normalized.Length; i++)
            set.Add(normalized.Substring(i, 3));
        return set;
    }

    /// <summary>Lower-cased with runs of whitespace and punctuation collapsed to one space.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        var lastSpace = true;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }
}
