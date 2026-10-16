using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Intelligently.IntegrationTests.Evals;

/// <summary>
/// Evaluation harness (plan section 1.5, Phase 5): replays the stored trace fixtures under
/// <c>Evals/Fixtures</c> through the rubric and fails when the pass rate drops below the configured
/// threshold (<c>Evals:PassRateThreshold</c> in <c>Evals/evals.json</c>, overridable with the
/// <c>INTELLIGENTLY_Evals__PassRateThreshold</c> environment variable). Run alone with
/// <c>dotnet test --filter Category=Eval</c>.
/// </summary>
[Trait("Category", "Eval")]
public class TraceReplayEvalTests
{
    private readonly ITestOutputHelper _output;

    public TraceReplayEvalTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static string EvalsDirectory => Path.Combine(AppContext.BaseDirectory, "Evals");
    public static string FixturesDirectory => Path.Combine(EvalsDirectory, "Fixtures");

    public static IConfiguration Configuration { get; } = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(EvalsDirectory, "evals.json"), optional: false)
        .AddEnvironmentVariables("INTELLIGENTLY_")
        .Build();

    public static double PassRateThreshold => Configuration.GetValue<double>("Evals:PassRateThreshold");

    public static IEnumerable<object[]> Fixtures() =>
        TraceFixture.FixturePaths(FixturesDirectory).Select(p => new object[] { Path.GetFileName(p) });

    [Fact]
    public void The_fixture_set_covers_a_completed_a_recovered_and_an_escalated_run()
    {
        var fixtures = TraceFixture.FixturePaths(FixturesDirectory).Select(TraceFixture.Load).ToList();

        fixtures.Should().HaveCountGreaterThanOrEqualTo(3);
        fixtures.Select(f => f.Expect.Outcome.ToLowerInvariant()).Should().Contain(["completed", "recovered", "escalated"]);
        fixtures.Select(f => f.Name).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Each_fixture_meets_the_pass_rate_threshold(string fileName)
    {
        var fixture = TraceFixture.Load(Path.Combine(FixturesDirectory, fileName));

        var score = RubricEvaluator.Evaluate(fixture);

        Report(score);
        score.Criteria.Should().NotBeEmpty();
        score.PassRate.Should().BeGreaterThanOrEqualTo(PassRateThreshold,
            $"fixture '{fixture.Name}' failed: {string.Join("; ", score.Failures.Select(f => $"{f.Criterion} ({f.Detail})"))}");
    }

    [Fact]
    public void The_whole_set_meets_the_pass_rate_threshold()
    {
        var scores = TraceFixture.FixturePaths(FixturesDirectory).Select(TraceFixture.Load).Select(RubricEvaluator.Evaluate).ToList();

        var criteria = scores.Sum(s => s.Criteria.Count);
        var passed = scores.Sum(s => s.Passed);
        var passRate = (double)passed / criteria;
        _output.WriteLine($"Pass rate {passRate:P1} ({passed}/{criteria} criteria over {scores.Count} fixtures); threshold {PassRateThreshold:P0}");
        foreach (var score in scores) Report(score);

        passRate.Should().BeGreaterThanOrEqualTo(PassRateThreshold);
    }

    [Fact]
    public void The_rubric_detects_a_regressed_trace()
    {
        // A fixture whose run skipped Verify and whose cost is unaccounted for must score below a healthy run.
        var healthy = TraceFixture.Load(Path.Combine(FixturesDirectory, "completed-lab-diagnosis.json"));
        var regressed = healthy with
        {
            Name = "regressed",
            Stages = ["Interpret", "Plan", "Act", "Learn", "Completed"],
            Run = healthy.Run with { TotalCostUsd = 0m },
        };

        var healthyScore = RubricEvaluator.Evaluate(healthy);
        var regressedScore = RubricEvaluator.Evaluate(regressed);

        healthyScore.PassRate.Should().Be(1.0);
        regressedScore.PassRate.Should().BeLessThan(PassRateThreshold);
        regressedScore.Failures.Select(f => f.Criterion).Should().Contain(["transitions.legal", "totals.cost_matches_rows"]);
    }

    private void Report(FixtureScore score)
    {
        _output.WriteLine($"{score.Fixture}: {score.Passed}/{score.Criteria.Count} ({score.PassRate:P0})");
        foreach (var criterion in score.Criteria)
            _output.WriteLine($"  [{(criterion.Passed ? "pass" : "FAIL")}] {criterion.Criterion} — {criterion.Detail}");
    }
}
