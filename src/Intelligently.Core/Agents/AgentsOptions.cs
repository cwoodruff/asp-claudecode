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

    /// <summary>Context Plane settings, bound from <c>Agents:Context</c>.</summary>
    public ContextOptions Context { get; set; } = new();

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

        /// <summary>Input-token budget of one prompt envelope, unless a stage overrides it.</summary>
        public int PromptTokens { get; set; } = 150_000;

        /// <summary>Per-stage prompt budgets keyed by stage name (<c>Plan</c>, <c>Act</c>, ...).</summary>
        public Dictionary<string, int> StagePromptTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The prompt budget for a stage.</summary>
        public int PromptTokensFor(string stage) =>
            StagePromptTokens.TryGetValue(stage, out var tokens) && tokens > 0 ? tokens : PromptTokens;
    }

    public sealed class ContextOptions
    {
        /// <summary>Knowledge entries retrieved for the course/module per envelope.</summary>
        public int KnowledgeEntries { get; set; } = 8;

        /// <summary>Episodes recalled for the learner and task type per envelope.</summary>
        public int Episodes { get; set; } = 5;

        /// <summary>How many recent episodes of a task type are scored for similarity.</summary>
        public int EpisodeCandidates { get; set; } = 200;

        /// <summary>
        /// Prompt size (input tokens) from which server-side compaction is requested for the run's
        /// transcript. Also the compaction trigger sent to the API.
        /// </summary>
        public int CompactAfterTokens { get; set; } = 100_000;

        /// <summary>Maximum characters of an episode summary.</summary>
        public int SummaryMaxChars { get; set; } = 2_000;

        /// <summary>Maximum characters of transcript handed to the local summarizer.</summary>
        public int SummaryInputMaxChars { get; set; } = 24_000;
    }
}
