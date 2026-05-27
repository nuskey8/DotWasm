using System.Collections.Immutable;

namespace DotWasm.Models;

public enum ElementMode : byte
{
    Active,
    Passive,
    Declarative,
}

public sealed record Element
{
    public required ElementMode Mode { get; init; }
    public required uint TableIndex { get; init; }
    public required WasmValueType ElementType { get; init; }
    public required Expression Expression { get; init; }
    public required ImmutableArray<Expression> Initializers { get; init; }
}
