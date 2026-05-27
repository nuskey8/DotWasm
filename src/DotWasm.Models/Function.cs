using System.Collections.Immutable;

namespace DotWasm.Models;

public record Function
{
    public required uint TypeIndex { get; init; }
    public required ImmutableArray<WasmValueType> Locals { get; init; }
    public required Expression Body { get; init; }
}
