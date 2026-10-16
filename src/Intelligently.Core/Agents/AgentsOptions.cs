namespace Intelligently.Core.Agents;

/// <summary>Bound from the <c>Agents</c> configuration section.</summary>
public sealed class AgentsOptions
{
    public const string SectionName = "Agents";

    public string DefaultModel { get; set; } = "claude-opus-5-5";
    public string CheapModel { get; set; } = "claude-haiku-5-5";
    public BudgetOptions Budgets { get; set; } = new();

    /// <summary>Capacity of the bounded run queue.</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Maximum characters kept from a single tool result before truncation.</summary>
    public int MaxToolResultChars { get; set; } = 16_000;

    public sealed class BudgetOptions
    {
        public int ReplansLeft { get; set; } = 3;
        public int RetriesLeft { get; set; } = 2;
        public int ToolCallsLeft { get; set; } = 40;
        public int TokensLeft { get; set; } = 300_000;
        public int WallClockMinutes { get; set; } = 30;
    }
}
