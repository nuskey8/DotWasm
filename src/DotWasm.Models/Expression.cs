using System.Collections.Immutable;

namespace DotWasm.Models;

public sealed record Expression
{
    public required ImmutableArray<Instruction> Instructions { get; init; }
}
