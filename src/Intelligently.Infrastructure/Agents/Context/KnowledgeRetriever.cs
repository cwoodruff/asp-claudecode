using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Entities.AI;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Retrieval over <see cref="AIKnowledgeEntry"/> scoped by course and module. On SQL Server with the
/// full-text index from the Phase 3 migration, candidates come from <c>FREETEXT</c> over question, answer
/// and tags; everywhere else (SQLite in the test hosts, or SQL Server without the full-text component)
/// candidates come from a <c>LIKE</c> filter on the query terms. Candidates are then scored in memory by
/// weighted term frequency (question 3, tags 2, answer 1) so both paths return the same shape: the top N
/// active entries with a relevance score, in a deterministic order.
/// </summary>
public sealed partial class KnowledgeRetriever : IKnowledgeRetriever
{
    private const int MaxTerms = 12;
    private const int MaxCandidates = 200;
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "have", "has", "are", "was", "were", "not", "but",
        "you", "your", "can", "when", "what", "how", "why", "does", "did", "its", "into", "than", "then", "there",
        "about", "which", "while", "will", "would", "should", "could", "they", "them", "their", "our", "out",
    };

    private static readonly ConcurrentDictionary<string, bool> FullTextByConnection = new(StringComparer.Ordinal);

    private readonly IntelligentlyDbContext _db;
    private readonly ILogger<KnowledgeRetriever> _logger;

    public KnowledgeRetriever(IntelligentlyDbContext db, ILogger<KnowledgeRetriever> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(string query, int? courseId, int? moduleId, int maxResults, CancellationToken ct = default)
    {
        var terms = Terms(query);
        if (terms.Count == 0 || maxResults <= 0) return [];

        var scoped = _db.AIKnowledgeEntries
            .AsNoTracking()
            .Where(e => e.IsActive)
            .Where(e => courseId == null || e.CourseId == null || e.CourseId == courseId)
            .Where(e => moduleId == null || e.ModuleId == null || e.ModuleId == moduleId);

        var candidates = await (await UseFullTextAsync(ct)
                ? scoped.Where(e => EF.Functions.FreeText(e.Question, query) || EF.Functions.FreeText(e.Answer, query) || (e.Tags != null && EF.Functions.FreeText(e.Tags, query)))
                : scoped.Where(AnyTermLike(terms)))
            .OrderByDescending(e => e.UpdatedAt)
            .ThenBy(e => e.Id)
            .Take(MaxCandidates)
            .ToListAsync(ct);

        return candidates
            .Select(e => new KnowledgeMatch(e, Score(e, terms)))
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Entry.UpdatedAt)
            .ThenBy(m => m.Entry.Id)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>Distinct search terms: words of three or more characters that are not stop words, longest first.</summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        return WordPattern().Matches(query)
            .Select(m => m.Value.Trim('.'))
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => w.Length)
            .ThenBy(w => w, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)
            .ToList();
    }

    /// <summary>Weighted, capped term frequency normalized by the number of terms.</summary>
    public static double Score(AIKnowledgeEntry entry, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        double score = 0;
        foreach (var term in terms)
        {
            score += 3 * Occurrences(entry.Question, term)
                   + 2 * Occurrences(entry.Tags, term)
                   + 1 * Occurrences(entry.Answer, term);
        }
        return Math.Round(score / terms.Count, 4);
    }

    private static int Occurrences(string? text, string term)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        var index = 0;
        while (count < 5 && (index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    /// <summary>
    /// <c>e =&gt; LIKE(e.Question, %t%) OR LIKE(e.Answer, %t%) OR LIKE(e.Tags, %t%)</c> for every term, built
    /// as an expression tree so the filter runs in SQL on any provider (LIKE is case-insensitive on
    /// SQLite and under the default SQL Server collation).
    /// </summary>
    internal static Expression<Func<AIKnowledgeEntry, bool>> AnyTermLike(IReadOnlyList<string> terms)
    {
        var entry = Expression.Parameter(typeof(AIKnowledgeEntry), "e");
        var functions = Expression.Property(null, typeof(EF), nameof(EF.Functions));
        var like = typeof(DbFunctionsExtensions).GetMethod(
            nameof(DbFunctionsExtensions.Like),
            [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

        Expression? body = null;
        foreach (var term in terms)
        {
            var pattern = Expression.Constant("%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            foreach (var property in new[] { nameof(AIKnowledgeEntry.Question), nameof(AIKnowledgeEntry.Answer), nameof(AIKnowledgeEntry.Tags) })
            {
                var member = Expression.Property(entry, property);
                Expression test = Expression.Call(like, functions, member, pattern, Expression.Constant("\\"));
                if (property == nameof(AIKnowledgeEntry.Tags))
                    test = Expression.AndAlso(Expression.NotEqual(member, Expression.Constant(null, typeof(string))), test);
                body = body is null ? test : Expression.OrElse(body, test);
            }
        }

        return Expression.Lambda<Func<AIKnowledgeEntry, bool>>(body ?? Expression.Constant(false), entry);
    }

    /// <summary>True when the database is SQL Server and the knowledge table has an active full-text index.</summary>
    private async Task<bool> UseFullTextAsync(CancellationToken ct)
    {
        if (!_db.Database.IsSqlServer()) return false;
        var key = _db.Database.GetConnectionString() ?? string.Empty;
        if (FullTextByConnection.TryGetValue(key, out var cached)) return cached;

        bool available;
        try
        {
            var flags = await _db.Database
                .SqlQueryRaw<int>("SELECT CAST(ISNULL(OBJECTPROPERTYEX(OBJECT_ID('dbo.AIKnowledgeEntries'), 'TableHasActiveFulltextIndex'), 0) AS int) AS [Value]")
                .ToListAsync(ct);
            available = flags.Count > 0 && flags[0] == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not determine full-text support for AIKnowledgeEntries; using LIKE retrieval");
            available = false;
        }

        FullTextByConnection[key] = available;
        return available;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_#\.\+]+")]
    private static partial Regex WordPattern();
}
