namespace DotWasm.Models;

public sealed record Import
{
    public required string Module { get; init; }
    public required string Name { get; init; }
    public required ImportExportKind Kind { get; init; }
    public required uint Index { get; init; }
    public required ExternalType Type { get; init; }
}
