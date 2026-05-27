namespace DotWasm.Models;

public sealed record Global
{
    public required GlobalType Type { get; init; }
    public required Expression InitExpression { get; init; }
}
