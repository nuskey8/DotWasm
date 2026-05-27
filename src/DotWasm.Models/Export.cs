namespace DotWasm.Models;

public sealed record Export
{
    public required string Name { get; init; }
    public required ImportExportKind Kind { get; init; }
    public required uint Index { get; init; }
}
