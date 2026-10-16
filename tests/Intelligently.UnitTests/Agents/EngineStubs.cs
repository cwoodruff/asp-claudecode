using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Execution;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>Canned reasoning-engine answers for a one-step lookup_knowledge plan.</summary>
internal static class EngineStubs
{
    public static readonly ModelUsage Usage = new("claude-opus-5-5", 120, 40);

    public static AgentPlan LookupPlan(string? tool = LookupKnowledgeTool.ToolName) => new()
    {
        Goal = "Explain the CS0246 build error to the learner.",
        Steps =
        [
            new PlanStep { Index = 0, Description = "Look up CS0246 in the knowledge base.", Tool = tool, SuccessCriteria = "At least one matching entry is returned." },
        ],
    };

    public static ToolCall LookupCall(string query = "CS0246") => new(
        "toolu_01",
        LookupKnowledgeTool.ToolName,
        JsonSerializer.SerializeToElement(new { query, course_id = (int?)null }));

    public static void PlanReturns(Mock<IReasoningEngine> engine, AgentPlan plan) =>
        engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(plan, Usage, "{\"raw\":\"plan\"}"));

    public static void PlanFails(Mock<IReasoningEngine> engine, string reason) =>
        engine.Setup(e => e.PlanAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanResult(null, Usage, "{}", reason));

    public static DecisionResult Decision(params ToolCall[] calls) =>
        new(calls, calls.Length == 0 ? "Here is my answer." : "Looking that up.", calls.Length == 0 ? "end_turn" : "tool_use", Usage, "{\"raw\":\"decision\"}");

    public static void DecideReturns(Mock<IReasoningEngine> engine, params ToolCall[] calls) =>
        engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(calls, calls.Length == 0 ? "Here is my answer." : "Looking that up.", calls.Length == 0 ? "end_turn" : "tool_use", Usage, "{\"raw\":\"decision\"}"));
}
