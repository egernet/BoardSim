using BoardSim.Core.Model;

namespace BoardSim.Core.Analysis;

public enum Severity { Info, Warning, Error }

/// <summary>One problem found on the board.</summary>
public sealed record Finding
{
    public required Severity Severity { get; init; }
    /// <summary>Stable rule id, e.g. "strap-pin".</summary>
    public required string Rule { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>Pins ("U1.14") and parts ("R3") involved; the UI highlights them.</summary>
    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Nets { get; init; } = Array.Empty<string>();
    public Vec2? Location { get; init; }

    public override string ToString() => $"{Severity.ToString().ToUpperInvariant(),-7} [{Rule}] {Title}";
}
