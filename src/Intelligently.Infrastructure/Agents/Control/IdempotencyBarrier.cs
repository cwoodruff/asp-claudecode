using System.Text.Json;
using Intelligently.Core.Agents.Contracts;
using Intelligently.Core.Agents.Entities;
using Intelligently.Core.Agents.Tools;
using Intelligently.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelligently.Infrastructure.Agents.Control;

/// <summary>
/// Idempotency barrier backed by <see cref="IdempotencyRecord"/> rows keyed on
/// (RunId, ToolName, SHA-256 of the canonical argument JSON). Only successful results are stored:
/// a failed mutation is allowed to run again on retry, a succeeded one never is.
/// </summary>
public sealed class IdempotencyBarrier : IIdempotencyBarrier
{
    private readonly IntelligentlyDbContext _db;

    public IdempotencyBarrier(IntelligentlyDbContext db)
    {
        _db = db;
    }

    public string ComputeKey(ToolCall call) => CanonicalJson.Sha256(call.Input);

    public async Task<ToolResult?> FindAsync(Guid runId, string toolName, string key, CancellationToken ct = default)
    {
        // A call executed earlier in the same stage is tracked but not yet committed; check the tracker first.
        var record = _db.IdempotencyRecords.Local.FirstOrDefault(r => r.RunId == runId && r.ToolName == toolName && r.ArgsHash == key)
                     ?? await _db.IdempotencyRecords.AsNoTracking()
                         .FirstOrDefaultAsync(r => r.RunId == runId && r.ToolName == toolName && r.ArgsHash == key, ct);

        return record is null ? null : JsonSerializer.Deserialize<ToolResult>(record.ResultJson);
    }

    public Task RecordAsync(Guid runId, ToolCall call, string key, ToolResult result, CancellationToken ct = default)
    {
        if (result.IsError) return Task.CompletedTask;

        _db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            RunId = runId,
            ToolName = call.Name,
            ArgsHash = key,
            ToolCallId = call.Id,
            ResultJson = JsonSerializer.Serialize(result),
            IsError = result.IsError,
        });
        return Task.CompletedTask;
    }
}
