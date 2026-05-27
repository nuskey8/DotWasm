using System.Runtime.CompilerServices;
using DotWasm.Models;

namespace DotWasm.Runtime;

public readonly struct WasmValue
{
    readonly ulong bits;
    readonly object? reference;

    WasmValue(ulong bits)
    {
        this.bits = bits;
        reference = null;
    }

    WasmValue(WasmV128Value v128)
    {
        bits = 0;
        reference = v128;
    }

    WasmValue(object? reference)
    {
        bits = 0;
        this.reference = reference;
    }

    public static readonly WasmValue NullReference = FromRaw((ulong)(long)FunctionAddress.Null);

    public ulong Bits => bits;
    public object? Reference => reference;
    public bool IsNullReference => reference is null && bits == (ulong)(long)FunctionAddress.Null;

    public int I32 => (int)bits;
    public long I64 => (long)bits;
    public float F32
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<ulong, float>(ref Unsafe.AsRef(in bits));
    }
    public double F64
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<ulong, double>(ref Unsafe.AsRef(in bits));
    }

    public WasmV128Value V128 => (WasmV128Value)reference!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GcArray AsArray() => (GcArray)reference!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public GcStruct AsStruct() => (GcStruct)reference!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalReference AsExternalReference() => (ExternalReference)reference!;

    public static bool ReferenceEquals(WasmValue left, WasmValue right)
    {
        if (left.Reference is not null || right.Reference is not null)
            return ReferenceEquals(left.Reference, right.Reference);
        return left.Bits == right.Bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue Default(WasmValueType type)
    {
        return type switch
        {
            I32Type => FromI32(0),
            I64Type => FromI64(0),
            F32Type => FromF32(0),
            F64Type => FromF64(0),
            V128Type => FromV128(WasmV128Value.Zero),
            RefType => NullReference,
            _ => ThrowHelper.ThrowInvalidOperation<WasmValue>("invalid value type"),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue Default(StorageType type)
    {
        return type switch
        {
            WasmValueType valueType => Default(valueType),
            PackedType packedType => packedType switch
            {
                PackedType.I8 => FromI32(0),
                PackedType.I16 => FromI32(0),
                _ => ThrowHelper.ThrowInvalidOperation<WasmValue>("invalid packed type"),
            },
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromRaw(ulong bits) => new(bits);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromRaw(object? reference) => new(reference);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromI32(int value) => new(unchecked((ulong)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromI64(long value) => new(unchecked((ulong)value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromF32(float value) => new(Unsafe.As<float, ulong>(ref value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromF64(double value) => new(Unsafe.As<double, ulong>(ref value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromV128(WasmV128Value value) => new(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromGcObject(GcObject reference) =>
        reference is null ? FromRaw((ulong)(long)FunctionAddress.Null) : new(reference);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmValue FromExternReference(ExternalReference reference) => new(reference);

    public static implicit operator WasmValue(int value) => FromI32(value);

    public static implicit operator WasmValue(long value) => FromI64(value);

    public static implicit operator WasmValue(float value) => FromF32(value);

    public static implicit operator WasmValue(double value) => FromF64(value);

    public static implicit operator WasmValue(WasmV128Value value) => FromV128(value);

    public static implicit operator WasmValue(GcObject reference) => FromGcObject(reference);

    public static implicit operator WasmValue(ExternalReference reference) =>
        FromExternReference(reference);
}
