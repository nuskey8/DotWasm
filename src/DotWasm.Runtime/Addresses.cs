namespace DotWasm.Runtime;

public readonly record struct GlobalAddress(long Value)
{
    public static implicit operator long(GlobalAddress address) => address.Value;

    public static implicit operator GlobalAddress(long value) => new(value);
}

public readonly record struct FunctionAddress(long Value)
{
    public static readonly FunctionAddress Null = new(-1);

    public static implicit operator long(FunctionAddress address) => address.Value;

    public static implicit operator FunctionAddress(long value) => new(value);
}

public readonly record struct MemoryAddress(long Value)
{
    public static implicit operator long(MemoryAddress address) => address.Value;

    public static implicit operator MemoryAddress(long value) => new(value);
}

public readonly record struct TableAddress(long Value)
{
    public static implicit operator long(TableAddress address) => address.Value;

    public static implicit operator TableAddress(long value) => new(value);
}

public readonly record struct TagAddress(long Value)
{
    public static implicit operator long(TagAddress address) => address.Value;

    public static implicit operator TagAddress(long value) => new(value);
}
