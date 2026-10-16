using System.Text.Json;
using Intelligently.Core.Agents.Contracts;

namespace Intelligently.Infrastructure.Agents.Context;

/// <summary>
/// Offline estimate (about four characters per token) used when the provider count is unavailable,
/// and by the test hosts.
/// </summary>
public sealed class EstimatingTokenCounter : IPromptTokenCounter
{
    public const int CharsPerToken = 4;

    public Task<int> CountAsync(PromptEnvelope envelope, CancellationToken ct = default) =>
        Task.FromResult(Estimate(envelope));

    public static int Estimate(PromptEnvelope envelope)
    {
        long chars = 0;
        foreach (var block in envelope.System) chars += block.Text.Length;
        foreach (var tool in envelope.Tools)
            chars += tool.Name.Length + tool.Description.Length + tool.InputSchema.GetRawText().Length;
        foreach (var message in envelope.Messages)
        {
            foreach (var content in message.Content)
            {
                chars += content.Text?.Length ?? 0;
                chars += content.ToolName?.Length ?? 0;
                if (content.Input is { } input) chars += input.GetRawText().Length;
                if (content.Raw is { } raw) chars += raw.GetRawText().Length;
                chars += 8; // block framing
            }
        }
        return (int)Math.Min(int.MaxValue, (chars + CharsPerToken - 1) / CharsPerToken);
    }
}
