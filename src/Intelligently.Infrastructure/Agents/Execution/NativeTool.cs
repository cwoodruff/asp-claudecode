using System.Text.Json;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Agents.Intelligence;

namespace Intelligently.Infrastructure.Agents.Execution;

/// <summary>
/// Base for native tools whose input and output are C# records: exports both schemas from the records,
/// deserializes the call's input into <typeparamref name="TInput"/> and serializes the
/// <typeparamref name="TOutput"/> as the result content and payload.
/// </summary>
public abstract class NativeTool<TInput, TOutput> : IToolHandler
{
    private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(AgentJsonSchema.ForNode<TInput>());
    private static readonly JsonElement OutputSchema = JsonSerializer.SerializeToElement(AgentJsonSchema.ForNode<TOutput>());

    /// <param name="exclusive">True for a tool that writes through the stage's DbContext; it then runs alone, after the concurrent calls.</param>
    protected NativeTool(string name, string description, ToolRiskLevel riskLevel, string? courseScopeArgument, bool exclusive = false)
    {
        Descriptor = new ToolDescriptor(name, description, InputSchema, riskLevel, courseScopeArgument, OutputSchema, Exclusive: exclusive);
    }

    public ToolDescriptor Descriptor { get; }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct = default)
    {
        TInput input;
        try
        {
            input = JsonSerializer.Deserialize<TInput>(call.Input, AgentJsonSchema.SerializerOptions)
                    ?? throw new JsonException("The input is null.");
        }
        catch (JsonException ex)
        {
            return ToolResult.Error(call, $"Invalid input for '{Descriptor.Name}': {ex.Message}");
        }

        var output = await ExecuteAsync(input, context, ct);
        return ToolResult.Ok(call, JsonSerializer.SerializeToElement(output, AgentJsonSchema.SerializerOptions));
    }

    protected abstract Task<TOutput> ExecuteAsync(TInput input, ToolContext context, CancellationToken ct);
}
