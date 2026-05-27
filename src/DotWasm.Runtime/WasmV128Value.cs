using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace DotWasm.Runtime;

public record WasmV128Value(ulong LowerBits, ulong UpperBits)
{
    public static readonly WasmV128Value Zero = new(0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmV128Value FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException(
                "Invalid byte length for WasmV128Value. Expected 16 bytes.",
                nameof(bytes)
            );

        var lowerBits = BitConverter.ToUInt64(bytes[..8]);
        var upperBits = BitConverter.ToUInt64(bytes[8..16]);
        return new WasmV128Value(lowerBits, upperBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static WasmV128Value FromVector128(Vector128<byte> value)
    {
        var v = value.AsUInt64();
        return new WasmV128Value(v.GetElement(0), v.GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ToBytes(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, LowerBits);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], UpperBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] ToBytes()
    {
        var bytes = new byte[16];
        ToBytes(bytes);
        return bytes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector128<byte> ToVector128() => Vector128.Create(LowerBits, UpperBits).AsByte();
}
