using System.Collections.Immutable;

namespace DotWasm.Models;

public sealed record WasmModule
{
    public required uint Version { get; init; }
    public required ImmutableArray<RecursiveType> Types { get; init; }
    public required ImmutableArray<Function> Functions { get; init; }
    public required ImmutableArray<TableType> Tables { get; init; }
    public required ImmutableArray<MemoryType> Memories { get; init; }
    public required ImmutableArray<Global> Globals { get; init; }
    public required ImmutableArray<TagType> Tags { get; init; }
    public required ImmutableArray<Element> Elements { get; init; }
    public required ImmutableArray<DataSegment> Data { get; init; }
    public required uint? StartFunctionIndex { get; init; }
    public required ImmutableArray<Import> Imports { get; init; }
    public required ImmutableArray<Export> Exports { get; init; }

    public ImmutableArray<SubType> DefinedTypes =>
        Types.SelectMany(static recursiveType => recursiveType.SubTypes).ToImmutableArray();
}
