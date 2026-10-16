namespace Intelligently.Core.Agents.Tools;

/// <summary>Risk class used by the Control Plane risk gate.</summary>
public enum ToolRiskLevel
{
    ReadOnly,
    Mutating,
    Destructive,
    External,
}
