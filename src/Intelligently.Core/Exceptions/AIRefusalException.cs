namespace Intelligently.Core.Exceptions;

/// <summary>
/// Thrown when the model declines a request (stop_reason "refusal") rather than
/// completing it. Callers should surface this to the user instead of treating
/// the empty content as a valid answer.
/// </summary>
public sealed class AIRefusalException : Exception
{
    public AIRefusalException(string? category, string? explanation)
        : base(BuildMessage(category, explanation))
    {
        Category = category;
        Explanation = explanation;
    }

    /// <summary>Refusal category reported by the API (e.g. "cyber"), when present.</summary>
    public string? Category { get; }

    /// <summary>Human-readable explanation reported by the API, when present.</summary>
    public string? Explanation { get; }

    private static string BuildMessage(string? category, string? explanation)
    {
        var msg = "The model refused to complete the request";
        if (!string.IsNullOrWhiteSpace(category)) msg += $" (category: {category})";
        if (!string.IsNullOrWhiteSpace(explanation)) msg += $": {explanation}";
        return msg + ".";
    }
}
