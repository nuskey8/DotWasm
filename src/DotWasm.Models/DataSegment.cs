namespace DotWasm.Models;

public enum DataSegmentMode : byte
{
    Active,
    Passive,
}

public sealed record DataSegment
{
    public required DataSegmentMode Mode { get; init; }
    public required uint MemoryIndex { get; init; }
    public required Expression OffsetExpression { get; init; }
    public required ReadOnlyMemory<byte> Data { get; init; }
}
