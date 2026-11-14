using BoardSim.Core.KiCad;
using BoardSim.Core.Model;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Analysis;

/// <summary>Everything BoardSim knows about one loaded board.</summary>
public sealed class BoardReport
{
    public required Board Board { get; init; }
    public required Circuit Circuit { get; init; }
    public required Connectivity Connectivity { get; init; }
    public Expectations? Expectations { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }

    public int Errors => Findings.Count(f => f.Severity == Severity.Error);
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);
}

public static class Checker
{
    public static IReadOnlyList<IRule> AllRules() => new IRule[]
    {
        new UnroutedRule(),
        new ExpectationRule(),
        new PowerPinRule(),
        new OvervoltageRule(),
        new LogicLevelRule(),
        new StrapPinRule(),
        new FloatingPinRule(),
        new PullRule(),
        new CapacitanceRule(),
        new FloatingOutputRule(),
        new DecouplingRule(),
        new SinglePinNetRule(),
        new ConnectorProtectionRule(),
    };

    /// <summary>Loads a board, its part models and expectations, and runs every rule.</summary>
    public static BoardReport Run(string boardPath, PartLibrary? library = null)
    {
        var board = KiCadPcbLoader.Load(boardPath);
        library ??= PartLibrary.BuiltIn();
        library.AddFolder(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(boardPath))!, "boardsim-parts"));
        return Run(board, library, Expectations.FindFor(boardPath));
    }

    public static BoardReport Run(Board board, PartLibrary library, Expectations? expectations)
    {
        var circuit = Circuit.Build(board, library);
        var connectivity = Connectivity.Analyze(board);
        var ctx = new CheckContext { Circuit = circuit, Connectivity = connectivity, Expectations = expectations };
        var findings = AllRules().SelectMany(r => r.Check(ctx))
            .OrderByDescending(f => f.Severity).ThenBy(f => f.Rule).ThenBy(f => f.Title, StringComparer.Ordinal)
            .ToList();
        return new BoardReport
        {
            Board = board, Circuit = circuit, Connectivity = connectivity,
            Expectations = expectations, Findings = findings,
        };
    }
}
