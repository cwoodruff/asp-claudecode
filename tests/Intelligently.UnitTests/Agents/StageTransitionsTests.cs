using FluentAssertions;
using Intelligently.Core.Agents.StateGraph;

namespace Intelligently.UnitTests.Agents;

public class StageTransitionsTests
{
    /// <summary>Every row of the transition table in docs/autonomous-agent-plan.md section 2.2.</summary>
    public static TheoryData<AgentStage, AgentStage> TableRows => new()
    {
        { AgentStage.Interpret, AgentStage.Plan },
        { AgentStage.Interpret, AgentStage.Failed },
        { AgentStage.Plan, AgentStage.Act },
        { AgentStage.Plan, AgentStage.Recover },
        { AgentStage.Act, AgentStage.Observe },
        { AgentStage.Act, AgentStage.AwaitingApproval },
        { AgentStage.AwaitingApproval, AgentStage.Act },
        { AgentStage.AwaitingApproval, AgentStage.Recover },
        { AgentStage.Observe, AgentStage.Verify },
        { AgentStage.Verify, AgentStage.Act },
        { AgentStage.Verify, AgentStage.Learn },
        { AgentStage.Verify, AgentStage.Recover },
        { AgentStage.Recover, AgentStage.Plan },
        { AgentStage.Recover, AgentStage.Act },
        { AgentStage.Recover, AgentStage.Failed },
        { AgentStage.Learn, AgentStage.Completed },
    };

    [Theory]
    [MemberData(nameof(TableRows))]
    public void Every_row_of_the_table_is_allowed(AgentStage from, AgentStage to)
    {
        StageTransitions.IsAllowed(from, to).Should().BeTrue();
    }

    [Fact]
    public void The_table_contains_exactly_the_sixteen_documented_edges()
    {
        StageTransitions.Allowed.Should().HaveCount(16);
    }

    [Theory]
    [InlineData(AgentStage.Interpret, AgentStage.Act)]
    [InlineData(AgentStage.Interpret, AgentStage.Learn)]
    [InlineData(AgentStage.Plan, AgentStage.Completed)]
    [InlineData(AgentStage.Act, AgentStage.Verify)]
    [InlineData(AgentStage.Observe, AgentStage.Act)]
    [InlineData(AgentStage.Verify, AgentStage.Completed)]
    [InlineData(AgentStage.Recover, AgentStage.Learn)]
    [InlineData(AgentStage.Learn, AgentStage.Act)]
    [InlineData(AgentStage.Completed, AgentStage.Interpret)]
    [InlineData(AgentStage.Failed, AgentStage.Plan)]
    [InlineData(AgentStage.Act, AgentStage.Act)]
    public void Edges_outside_the_table_are_rejected(AgentStage from, AgentStage to)
    {
        StageTransitions.IsAllowed(from, to).Should().BeFalse();
    }

    [Fact]
    public void Completed_and_Failed_are_terminal()
    {
        StageTransitions.IsTerminal(AgentStage.Completed).Should().BeTrue();
        StageTransitions.IsTerminal(AgentStage.Failed).Should().BeTrue();
        StageTransitions.IsTerminal(AgentStage.AwaitingApproval).Should().BeFalse();
        StageTransitions.Allowed.Should().NotContain(t => t.From == AgentStage.Completed || t.From == AgentStage.Failed);
    }
}
