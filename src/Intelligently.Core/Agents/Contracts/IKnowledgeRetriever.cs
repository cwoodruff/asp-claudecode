using Intelligently.Core.Entities.AI;

namespace Intelligently.Core.Agents.Contracts;

/// <summary>A knowledge entry with its relevance to the query (higher is better).</summary>
public sealed record KnowledgeMatch(AIKnowledgeEntry Entry, double Score);

/// <summary>Context Plane retrieval over <see cref="AIKnowledgeEntry"/>, scoped by course and module.</summary>
public interface IKnowledgeRetriever
{
    /// <summary>
    /// The top <paramref name="maxResults"/> active entries matching <paramref name="query"/>. A course id
    /// restricts results to that course plus course-less entries; a module id likewise within the course.
    /// Results are ordered by score descending, then by most recently updated, then by id, so the same
    /// query returns the same order on every turn of a run.
    /// </summary>
    Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(string query, int? courseId, int? moduleId, int maxResults, CancellationToken ct = default);
}
