using System.Net;
using System.Text.Json;
using Anthropic;
using FluentAssertions;
using Intelligently.Core.Agents;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.StateGraph;
using Intelligently.Infrastructure.Agents.Context;
using Intelligently.UnitTests.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Intelligently.UnitTests.Agents;

/// <summary>The budgeter trims episodic entries, then knowledge entries, one at a time, and stops as soon as the envelope fits.</summary>
public class TokenBudgeterTests
{
    /// <summary>Deterministic counter: one token per text character, so trimming effects are exact.</summary>
    private sealed class CharCounter : IPromptTokenCounter
    {
        public int Calls { get; private set; }

        public Task<int> CountAsync(PromptEnvelope envelope, CancellationToken ct = default)
        {
            Calls++;
            var chars = envelope.System.Sum(b => b.Text.Length)
                        + envelope.Messages.Sum(m => m.Content.Sum(c => c.Text?.Length ?? 0));
            return Task.FromResult(chars);
        }
    }

    private static PromptMessage Section(PromptSection section, string header, params string[] entries) =>
        new(PromptMessage.UserRole, [PromptContent.FromText(header), .. entries.Select(PromptContent.FromText)], section);

    // system 10 + knowledge (6 + 10 + 10 + 10) + episodic (6 + 20 + 20) + transcript 10 + instruction 10 = 112
    private static PromptEnvelope Envelope() => new(
        [new PromptBlock("0123456789", CacheBreakpoint: true)],
        [],
        [
            Section(PromptSection.Knowledge, "KNOWLE", "k1k1k1k1k1", "k2k2k2k2k2", "k3k3k3k3k3"),
            Section(PromptSection.Episodic, "EPISOD", "e1e1e1e1e1e1e1e1e1e1", "e2e2e2e2e2e2e2e2e2e2"),
            new PromptMessage(PromptMessage.UserRole, "task-task-", PromptSection.Transcript),
            new PromptMessage(PromptMessage.SystemRole, "instructio", PromptSection.Instruction),
        ])
    { Stage = AgentStage.Act };

    private static TokenBudgeter Budgeter(IPromptTokenCounter counter, int promptTokens = 1000, Dictionary<string, int>? perStage = null) =>
        new(counter, Options.Create(new AgentsOptions
        {
            Budgets = new AgentsOptions.BudgetOptions { PromptTokens = promptTokens, StagePromptTokens = perStage ?? new() },
        }), NullLogger<TokenBudgeter>.Instance);

    [Fact]
    public async Task Under_budget_nothing_is_trimmed_and_the_count_is_recorded()
    {
        var counter = new CharCounter();
        var fitted = await Budgeter(counter).FitAsync(Envelope(), AgentStage.Act);

        fitted.InputTokens.Should().Be(112);
        fitted.Messages.Should().HaveCount(4);
        counter.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Trims_episodic_entries_first_then_the_block_then_knowledge_entries_and_stops_when_under_budget()
    {
        var counter = new CharCounter();
        // 112 → drop e2 (92) → drop episodic block entirely, e1 was its last entry (66) → drop k3 (56) ≤ 60: stop.
        var fitted = await Budgeter(counter, promptTokens: 60).FitAsync(Envelope(), AgentStage.Act);

        fitted.InputTokens.Should().Be(56);
        fitted.Messages.Select(m => m.Section).Should().Equal(PromptSection.Knowledge, PromptSection.Transcript, PromptSection.Instruction);
        fitted.Messages[0].Content.Select(c => c.Text).Should().Equal("KNOWLE", "k1k1k1k1k1", "k2k2k2k2k2");
        counter.Calls.Should().Be(4, "one count before trimming and one after each of three trims");
    }

    [Fact]
    public async Task Trim_order_is_episodic_entry_episodic_block_knowledge_entry_knowledge_block_then_nothing()
    {
        var envelope = Envelope();
        var steps = new List<string>();
        for (var trimmed = TokenBudgeter.Trim(envelope); trimmed is not null; trimmed = TokenBudgeter.Trim(trimmed))
        {
            steps.Add(string.Join("|", trimmed.Messages.Select(m => $"{m.Section}:{m.Content.Count}")));
            envelope = trimmed;
        }

        steps.Should().Equal(
            "Knowledge:4|Episodic:2|Transcript:1|Instruction:1",
            "Knowledge:4|Transcript:1|Instruction:1",
            "Knowledge:3|Transcript:1|Instruction:1",
            "Knowledge:2|Transcript:1|Instruction:1",
            "Transcript:1|Instruction:1");
        TokenBudgeter.Trim(envelope).Should().BeNull("the system prompt, transcript and instruction are never trimmed");
    }

    [Fact]
    public async Task Over_budget_with_nothing_left_to_trim_returns_the_envelope_with_its_count()
    {
        var counter = new CharCounter();
        var fitted = await Budgeter(counter, promptTokens: 5).FitAsync(Envelope(), AgentStage.Act);

        fitted.Messages.Select(m => m.Section).Should().Equal(PromptSection.Transcript, PromptSection.Instruction);
        fitted.InputTokens.Should().Be(30);
    }

    [Fact]
    public async Task Stage_budget_overrides_the_default_budget()
    {
        var counter = new CharCounter();
        var budgeter = Budgeter(counter, promptTokens: 1000, perStage: new() { ["Act"] = 100 });

        var act = await budgeter.FitAsync(Envelope(), AgentStage.Act);
        var plan = await budgeter.FitAsync(Envelope(), AgentStage.Plan);

        act.InputTokens.Should().Be(92, "one episodic entry was dropped to fit the Act budget of 100");
        plan.InputTokens.Should().Be(112);
    }

    [Fact]
    public async Task AnthropicTokenCounter_calls_count_tokens_with_the_mapped_envelope()
    {
        var handler = new FakeAnthropicHandler(_ => FakeAnthropicHandler.Json("""{"input_tokens": 1234}"""));
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        var counter = new AnthropicTokenCounter(client, Options.Create(new AgentsOptions()), NullLogger<AnthropicTokenCounter>.Instance);

        var count = await counter.CountAsync(AnthropicReasoningEngineTests.Envelope());

        count.Should().Be(1234);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("/v1/messages/count_tokens");
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("claude-opus-5-5");
        root.GetProperty("system").EnumerateArray().Single().GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
        root.GetProperty("tools").EnumerateArray().Single().GetProperty("name").GetString().Should().Be("lookup_knowledge");
        root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "system");
        root.TryGetProperty("max_tokens", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AnthropicTokenCounter_falls_back_to_an_estimate_when_the_provider_fails()
    {
        var handler = new FakeAnthropicHandler(_ => FakeAnthropicHandler.Json("""{"type":"error","error":{"type":"api_error","message":"down"}}""", HttpStatusCode.InternalServerError));
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        var counter = new AnthropicTokenCounter(client, Options.Create(new AgentsOptions()), NullLogger<AnthropicTokenCounter>.Instance);
        var envelope = AnthropicReasoningEngineTests.Envelope();

        var count = await counter.CountAsync(envelope);

        count.Should().Be(EstimatingTokenCounter.Estimate(envelope)).And.BeGreaterThan(0);
    }
}
