namespace Intelligently.Core.Exceptions;

/// <summary>
/// Thrown by the data layer when a unit of work tries to update or delete a row of an append-only
/// table (the evidence trace). Corrections are new rows, never edits.
/// </summary>
public sealed class AppendOnlyViolationException : InvalidOperationException
{
    public AppendOnlyViolationException(string entity, string operation)
        : base($"{entity} rows are append-only; {operation} is not allowed. Record a correction as a new row instead.")
    {
        Entity = entity;
        Operation = operation;
    }

    public string Entity { get; }
    public string Operation { get; }
}
