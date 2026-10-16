using System.Text.Json;
using FluentAssertions;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Core.Agents.Tasks;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Intelligently.UnitTests.Agents;

/// <summary>
/// The escalation rule of the pilot task: a sandbox call whose files change a course-provided file is a
/// change to protected material. The Control Plane's <c>course-material-approval</c> rule parks the run
/// for an instructor; a denial goes to Recover, which escalates instead of replanning.
/// </summary>
public class CourseMaterialPolicyTests
{
    private const string ChangedCourseFile = "public static class Helpers\n{\n    public static int Twice(int x) => x + x;\n}\n";

    private static readonly LabErrorDiagnosisContract Contract = new();

    private static AgentRun RunWithCourseFile(string? expectedOutput = null) => new()
    {
        UserId = "learner-1", CourseId = 7, TaskType = LabErrorDiagnosisSpec.TaskType,
        TaskSpec = LabSpecs.Json(expectedOutput: expectedOutput, courseFiles: LabSpecs.WithCourseFile), Principal = RunPrincipal.Learner("learner-1", 7),
    };

    private static (AgentTestHost Host, FakeSandbox Sandbox, RecordingRunQueue Queue) HostWithQueue()
    {
        var sandbox = new FakeSandbox(_ => LabSpecs.Success(LabSpecs.ExpectedOutput + "\n"));
        RecordingRunQueue queue = null!;
        var host = new AgentTestHost(s =>
        {
            s.RemoveAll<ISandbox>();
            s.AddSingleton<ISandbox>(sandbox);
            queue = s.UseRecordingQueue();
        });
        return (host, sandbox, queue);
    }

    private static async Task<AgentApproval> DecideAsync(AgentTestHost host, Guid runId, bool approved)
    {
        using var scope = host.Provider.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<IApprovalGate>();
        var approval = await scope.ServiceProvider.GetRequiredService<Intelligently.Infrastructure.Data.IntelligentlyDbContext>()
            .AgentApprovals.AsNoTracking().SingleAsync(a => a.RunId == runId && a.Status == AgentApproval.StatusPending);
        return await gate.DecideAsync(approval.Id, approved, "instructor-1", approved ? "Fine for this lab." : "Starter code stays as published.");
    }

    [Fact]
    public void The_contract_recognises_a_changed_course_file_and_ignores_unchanged_or_absent_ones()
    {
        var run = RunWithCourseFile();
        var changed = LabSpecs.RunCall("t1", LabSpecs.FixedProgram, courseFiles: [new LabSpecFile(LabSpecs.CourseFilePath, ChangedCourseFile)]);
        var unchanged = LabSpecs.RunCall("t2", LabSpecs.FixedProgram, courseFiles: LabSpecs.WithCourseFile);
        var omitted = LabSpecs.RunCall("t3", LabSpecs.FixedProgram);

        Contract.DescribeProtectedChange(run, changed).Should().Contain("course-provided file(s) 'Helpers.cs'");
        Contract.DescribeProtectedChange(run, unchanged).Should().BeNull();
        Contract.DescribeProtectedChange(run, omitted).Should().BeNull();
        Contract.DescribeProtectedChange(run, EngineStubs.LookupCall()).Should().BeNull("only sandbox calls submit files");
        Contract.DescribeProtectedChange(new AgentRun { TaskSpec = LabSpecs.Default, CourseId = 7 }, changed).Should().BeNull("a spec without course files protects nothing");
    }

    [Fact]
    public void The_default_rule_set_carries_the_course_material_rule_between_identity_and_risk()
    {
        var kinds = PolicyRule.Defaults.Select(r => r.Kind).ToList();

        kinds.Should().ContainInOrder(PolicyRuleKind.Identity, PolicyRuleKind.CourseMaterial, PolicyRuleKind.Risk, PolicyRuleKind.RateLimit, PolicyRuleKind.Idempotency);
        PolicyRule.Defaults.Should().ContainSingle(r => r.Kind == PolicyRuleKind.CourseMaterial)
            .Which.Should().Match<PolicyRule>(r => r.Name == "course-material-approval" && r.Effect == PolicyEffect.RequireApproval && r.Tool == "*");
    }

    [Fact]
    public async Task The_policy_decision_point_requires_approval_for_a_changed_course_file_and_allows_the_rest()
    {
        using var host = new AgentTestHost();
        using var scope = host.Provider.CreateScope();
        var pdp = scope.ServiceProvider.GetRequiredService<IPolicyDecisionPoint>();
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        registry.TryGetHandler("run_lab_code", out var handler).Should().BeTrue();
        var run = RunWithCourseFile();

        var changed = await pdp.EvaluateAsync(new PolicyRequest(run, LabSpecs.RunCall("t1", LabSpecs.FixedProgram, courseFiles: [new LabSpecFile(LabSpecs.CourseFilePath, ChangedCourseFile)]), handler.Descriptor, run.Principal));
        changed.Verdict.Should().Be(PolicyVerdict.RequireApproval);
        changed.RuleKind.Should().Be(PolicyRuleKind.CourseMaterial);
        changed.Rule.Should().Be("course-material-approval");
        changed.Reason.Should().Contain("course-provided file(s) 'Helpers.cs'");

        var unchanged = await pdp.EvaluateAsync(new PolicyRequest(run, LabSpecs.RunCall("t2", LabSpecs.FixedProgram, courseFiles: LabSpecs.WithCourseFile), handler.Descriptor, run.Principal));
        unchanged.Verdict.Should().Be(PolicyVerdict.Allow);
        unchanged.RuleKind.Should().Be(PolicyRuleKind.Risk, "the read-only risk rule decides when no course material changes");
    }

    [Fact]
    public async Task A_fix_in_a_course_file_parks_the_run_and_a_denial_escalates_instead_of_replanning()
    {
        var (host, sandbox, queue) = HostWithQueue();
        using var _ = host;
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Rerun));
        EngineStubs.DecideReturns(host.Engine, LabSpecs.RunCall("toolu_fix", LabSpecs.BrokenProgram, courseFiles: [new LabSpecFile(LabSpecs.CourseFilePath, ChangedCourseFile)]));
        var run = await host.CreateRunAsync(taskSpec: LabSpecs.Json(expectedOutput: LabSpecs.ExpectedOutput, courseFiles: LabSpecs.WithCourseFile));

        var (parked, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(AgentStage.Interpret, AgentStage.Plan, AgentStage.Act, AgentStage.AwaitingApproval);
        parked.Status.Should().Be(AgentRun.StatusWaiting);
        parked.StatusReason.Should().Contain("course-provided file(s) 'Helpers.cs'");
        sandbox.Requests.Should().BeEmpty("nothing runs before the instructor decides");
        var approval = await host.QueryAsync(db => db.AgentApprovals.SingleAsync(a => a.RunId == run.Id));
        approval.RuleKind.Should().Be(nameof(PolicyRuleKind.CourseMaterial));
        approval.Rule.Should().Be("course-material-approval");
        approval.Reason.Should().Contain("course-provided file(s)");
        var escalationBefore = await host.QueryAsync(db => db.AIEscalations.SingleAsync(e => e.AgentRunId == run.Id));
        escalationBefore.EscalationReason.Should().Contain("needs approval for tool 'run_lab_code'");

        var decided = await DecideAsync(host, run.Id, approved: false);
        decided.Status.Should().Be(AgentApproval.StatusDenied);
        queue.Enqueued.Should().Equal(run.Id, run.Id);

        var (final, after) = await host.RunToEndAsync(run.Id);

        after.Should().Equal(AgentStage.AwaitingApproval, AgentStage.Recover, AgentStage.Failed);
        final.Status.Should().Be(AgentRun.StatusFailed);
        final.PlanRevision.Should().Be(0, "a denied course-material change is not worked around by replanning");
        final.Budget.ReplansLeft.Should().Be(3);
        final.StatusReason.Should().StartWith("Escalated to instructor:").And.Contain("denied the change to course-provided material");
        var escalations = await host.QueryAsync(db => db.AIEscalations.Where(e => e.AgentRunId == run.Id).OrderBy(e => e.Id).ToListAsync());
        escalations.Should().HaveCount(2, "the approval request and the final escalation");
        escalations[1].EscalationReason.Should().Contain("could not recover").And.Contain("course-provided material");
        (await host.QueryAsync(db => db.AgentTraces.CountAsync(t => t.RunId == run.Id && t.Kind == AgentTrace.KindEscalation))).Should().Be(1);
        host.Engine.Verify(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_approved_course_file_change_resumes_the_same_step_and_runs_the_fix()
    {
        var (host, sandbox, _) = HostWithQueue();
        using var __ = host;
        EngineStubs.PlanReturns(host.Engine, LabSpecs.ShortPlan(LabPhases.Rerun));
        var turn = 0;
        host.Engine.Setup(e => e.DecideAsync(It.IsAny<PromptEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++turn == 1
                ? new DecisionResult([LabSpecs.RunCall("toolu_fix", LabSpecs.FixedProgram, LabSpecs.ExpectedOutput, [new LabSpecFile(LabSpecs.CourseFilePath, ChangedCourseFile)])], "Fixing the helper.", "tool_use", EngineStubs.Usage, "{}")
                : new DecisionResult([], "The helper added x to itself instead of doubling it; the fix is in Helpers.cs, which the instructor approved.", "end_turn", EngineStubs.Usage, "{}"));
        var run = await host.CreateRunAsync(taskSpec: LabSpecs.Json(expectedOutput: LabSpecs.ExpectedOutput, courseFiles: LabSpecs.WithCourseFile));
        await host.RunToEndAsync(run.Id);

        await DecideAsync(host, run.Id, approved: true);
        var (final, stages) = await host.RunToEndAsync(run.Id);

        stages.Should().Equal(AgentStage.AwaitingApproval, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Act, AgentStage.Observe, AgentStage.Verify, AgentStage.Learn, AgentStage.Completed);
        final.Status.Should().Be(AgentRun.StatusCompleted);
        sandbox.Requests.Should().ContainSingle().Which.Files.Should().Contain(f => f.Path == LabSpecs.CourseFilePath && f.Content == ChangedCourseFile);
        final.FixDiff.Should().Contain("--- a/Helpers.cs").And.Contain("+    public static int Twice(int x) => x + x;")
            .And.Contain("--- a/Program.cs", "the learner file changed too");
        final.FinalExplanation.Should().Contain("the instructor approved");
        var traces = await host.QueryAsync(db => db.AgentTraces.Where(t => t.RunId == run.Id && t.Kind == AgentTrace.KindToolCall).ToListAsync());
        traces.Should().ContainSingle().Which.Payload.Should().Contain("\"decision\":\"Allow\"");
    }
}
