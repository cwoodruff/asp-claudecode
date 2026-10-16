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

    /// <summary>
    /// How often the run worker looks for runs whose approval was decided in another process
    /// (Admin) and re-enqueues them. 0 disables the sweep.
    /// </summary>
    public int ApprovalSweepSeconds { get; set; } = 15;

    /// <summary>Control Plane rules, bound from <c>Agents:Policy</c>.</summary>
    public PolicyOptions Policy { get; set; } = new();

    public sealed class PolicyOptions
    {
        /// <summary>Ordered rules; when empty, <see cref="Contracts.PolicyRule.Defaults"/> apply.</summary>
        public List<Contracts.PolicyRule> Rules { get; set; } = [];

        /// <summary>The rules in force: configured ones, or the defaults when none are configured.</summary>
        public IReadOnlyList<Contracts.PolicyRule> EffectiveRules => Rules.Count > 0 ? Rules : Contracts.PolicyRule.Defaults;
    }

    public sealed class BudgetOptions
    {
        public int ReplansLeft { get; set; } = 3;
        public int RetriesLeft { get; set; } = 2;
        public int ToolCallsLeft { get; set; } = 40;
        public int TokensLeft { get; set; } = 300_000;
        public int WallClockMinutes { get; set; } = 30;
    }
}
