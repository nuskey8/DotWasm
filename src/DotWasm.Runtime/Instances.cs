using DotWasm.Models;

namespace DotWasm.Runtime;

public sealed class GlobalInstance
{
    public required bool Mutable { get; init; }
    public required WasmValueType ValueType { get; init; }
    public WasmValue Value { get; set; }
}

public sealed class MemoryInstance(int initialPages)
{
    const uint PageSize = 65536;

    public AddressType AddressType { get; init; } = AddressType.I32;
    public ulong? Max { get; init; }
    public Span<byte> Data => new(data);

    byte[] data = new byte[initialPages * PageSize];

    public void Grow(int additionalPages)
    {
        var newSize = checked((uint)data.Length + additionalPages * PageSize);
        if (newSize > uint.MaxValue)
            WasmTrapException.Throw("Memory size exceeds maximum limit.");

        Array.Resize(ref data, (int)newSize);
    }
}

public sealed class TableInstance
{
    public WasmValueType ElementType { get; init; }
    public AddressType AddressType { get; init; } = AddressType.I32;
    public ulong? Max { get; init; }

    public Span<WasmValue> References => new(references);

    WasmValue[] references;

    public TableInstance(ulong initialSize)
    {
        references = new WasmValue[checked((int)initialSize)];
        Array.Fill(references, WasmValue.NullReference);
    }

    public void Grow(int additionalSize, WasmValue initialValue)
    {
        var newSize = checked((uint)references.Length + additionalSize);
        if (newSize > uint.MaxValue)
            WasmTrapException.Throw("Table size exceeds maximum limit.");

        Array.Resize(ref references, (int)newSize);
        Array.Fill(references, initialValue, references.Length - additionalSize, additionalSize);
    }
}

public sealed class TagInstance
{
    public required TagType Type { get; init; }
}

public union FunctionInstance(RuntimeFunction, HostFunction);

public sealed class RuntimeFunction
{
    public required WasmInstance Owner { get; init; }
    public required Function Definition { get; init; }
}

public sealed class HostFunction
{
    public required HostFunctionDelegate Delegate { get; init; }
    public required FuncType Type { get; init; }
}

public delegate void HostFunctionDelegate(ReadOnlySpan<WasmValue> arguments, Span<WasmValue> results);
