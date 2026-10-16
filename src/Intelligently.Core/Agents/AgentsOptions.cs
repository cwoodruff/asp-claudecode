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

    /// <summary>
    /// Length of the lease a worker takes on a run before executing one stage. It must exceed the longest
    /// stage (model turn plus tool calls): once it expires another worker may take the run over.
    /// </summary>
    public int StageLeaseSeconds { get; set; } = 600;

    /// <summary>Control Plane rules, bound from <c>Agents:Policy</c>.</summary>
    public PolicyOptions Policy { get; set; } = new();

    /// <summary>Context Plane settings, bound from <c>Agents:Context</c>.</summary>
    public ContextOptions Context { get; set; } = new();

    /// <summary>MCP tool servers, bound from <c>Agents:Mcp</c>.</summary>
    public McpOptions Mcp { get; set; } = new();

    /// <summary>Sandbox settings, bound from <c>Agents:Sandbox</c>.</summary>
    public SandboxOptions Sandbox { get; set; } = new();

    /// <summary>Price table for the cost accountant, bound from <c>Agents:Pricing</c>.</summary>
    public PricingOptions Pricing { get; set; } = new();

    /// <summary>
    /// USD per million tokens per model (plan section 1.5). The defaults are the first-party API rates of
    /// the two models the runtime uses; cache creation is 1.25× the input rate, cache reads 0.1× (0.05× on
    /// Opus 5.5, whose cache reads are priced at $0.20). A model without a row costs nothing and is logged.
    /// </summary>
    public sealed class PricingOptions
    {
        public Dictionary<string, ModelPricing> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5-5"] = new() { InputPerMillion = 4.00m, OutputPerMillion = 20.00m, CacheReadPerMillion = 0.20m, CacheCreationPerMillion = 5.00m },
            ["claude-haiku-5-5"] = new() { InputPerMillion = 0.10m, OutputPerMillion = 0.50m, CacheReadPerMillion = 0.01m, CacheCreationPerMillion = 0.125m },
        };

        /// <summary>The price row for a model id, matched exactly or by prefix (a dated snapshot shares its base model's rates).</summary>
        public ModelPricing? For(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;
            if (Models.TryGetValue(model, out var exact)) return exact;
            return Models
                .Where(p => model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Key.Length)
                .Select(p => p.Value)
                .FirstOrDefault();
        }
    }

    public sealed class ModelPricing
    {
        public decimal InputPerMillion { get; set; }
        public decimal OutputPerMillion { get; set; }
        public decimal CacheReadPerMillion { get; set; }
        public decimal CacheCreationPerMillion { get; set; }
    }

    public sealed class McpOptions
    {
        /// <summary>Servers to connect to at startup; a server that fails to connect is logged and skipped.</summary>
        public List<McpServerOptions> Servers { get; set; } = [];

        /// <summary>
        /// Risk level per tool, keyed by the registered name (<c>server__tool</c>) or the bare tool name.
        /// Tools without an override are <see cref="Tools.ToolRiskLevel.External"/>.
        /// </summary>
        public Dictionary<string, Tools.ToolRiskLevel> RiskOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Separator between the server name and the tool name in the registered name. The provider only
        /// accepts <c>[A-Za-z0-9_-]</c> in tool names, so a dot is not an option.
        /// </summary>
        public string NameSeparator { get; set; } = "__";

        public int ConnectTimeoutSeconds { get; set; } = 30;
        public int CallTimeoutSeconds { get; set; } = 120;
    }

    public sealed class McpServerOptions
    {
        public const string StdioTransport = "stdio";
        public const string HttpTransport = "http";

        public string Name { get; set; } = string.Empty;

        /// <summary><c>stdio</c> (command + args) or <c>http</c> (url).</summary>
        public string Transport { get; set; } = StdioTransport;

        public string? Command { get; set; }
        public List<string> Args { get; set; } = [];
        public string? WorkingDirectory { get; set; }
        public Dictionary<string, string> Environment { get; set; } = new();

        public string? Url { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();

        public bool IsStdio => string.Equals(Transport, StdioTransport, StringComparison.OrdinalIgnoreCase);
        public bool IsHttp => string.Equals(Transport, HttpTransport, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class SandboxOptions
    {
        /// <summary>Container image for lab code; the .NET SDK image runs C# and F# scripts out of the box.</summary>
        public string Image { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

        /// <summary>Docker engine endpoint; null uses the platform default (named pipe on Windows, Unix socket elsewhere).</summary>
        public string? DockerEndpoint { get; set; }

        /// <summary>Pull the image when it is not present locally.</summary>
        public bool PullImage { get; set; } = true;

        public int TimeoutSeconds { get; set; } = 60;
        public long MemoryBytes { get; set; } = 512L * 1024 * 1024;
        public double Cpus { get; set; } = 1.0;
        public int PidsLimit { get; set; } = 256;

        /// <summary>Cap on captured stdout and stderr, each, in bytes.</summary>
        public int MaxOutputBytes { get; set; } = 64 * 1024;

        /// <summary>Path inside the container where the submitted files are mounted and the command runs.</summary>
        public string WorkingDirectory { get; set; } = "/work";

        /// <summary>
        /// Command template per language for <c>run_lab_code</c>; <c>{entrypoint}</c> is replaced by the
        /// submitted entrypoint path. Keys are case-insensitive.
        /// </summary>
        public Dictionary<string, string[]> Languages { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = ["dotnet", "run", "{entrypoint}"],
            ["csproj"] = ["dotnet", "run", "--project", "{entrypoint}"],
            ["fsharp"] = ["dotnet", "fsi", "{entrypoint}"],
            ["shell"] = ["sh", "{entrypoint}"],
            ["python"] = ["python3", "{entrypoint}"],
        };

        public WasmOptions Wasm { get; set; } = new();
    }

    public sealed class WasmOptions
    {
        /// <summary>Fuel (roughly, instructions) a module may consume before it is stopped.</summary>
        public long Fuel { get; set; } = 2_000_000_000;

        /// <summary>Linear memory cap for the module.</summary>
        public long MemoryBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>Wall-clock limit, enforced with epoch interruption.</summary>
        public int TimeoutSeconds { get; set; } = 10;

        public int MaxOutputBytes { get; set; } = 64 * 1024;
    }

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
