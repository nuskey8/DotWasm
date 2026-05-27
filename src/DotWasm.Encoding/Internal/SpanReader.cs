using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace DotWasm.Encoding;

internal ref struct SpanReader(ReadOnlySpan<byte> bytes)
{
    readonly ReadOnlySpan<byte> bytes = bytes;
    int offset;

    public readonly int Position => offset;
    public readonly bool IsEmpty => offset == bytes.Length;
    public readonly int Length => bytes.Length - offset;

    public readonly byte PeekByte()
    {
        if ((uint)offset >= (uint)bytes.Length)
            WasmDecodeException.Throw("Unexpected end of input.");
        return bytes[offset];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if ((uint)offset >= (uint)bytes.Length)
            WasmDecodeException.Throw("Unexpected end of input.");
        return bytes[offset++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > bytes.Length - offset)
            WasmDecodeException.Throw("Unexpected end of input.");
        var result = bytes.Slice(offset, count);
        offset += count;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Slice(int start, int length)
    {
        if (start < 0 || length < 0 || start > bytes.Length - length)
            WasmDecodeException.Throw("Unexpected end of input.");
        return bytes.Slice(start, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => ReadBytes(count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32LittleEndian() =>
        BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64LittleEndian() =>
        BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32Leb128()
    {
        uint result = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = ReadByte();
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                if (i == 4 && (b & 0xf0) != 0)
                    WasmDecodeException.Throw("Unsigned LEB128 value exceeds 32 bits.");
                return result;
            }

            shift += 7;
        }

        WasmDecodeException.Throw("Unsigned LEB128 value exceeds 32 bits.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64Leb128()
    {
        ulong result = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                if (i == 9 && (b & 0x7e) != 0)
                    WasmDecodeException.Throw("Unsigned LEB128 value exceeds 64 bits.");
                return result;
            }

            shift += 7;
        }

        WasmDecodeException.Throw("Unsigned LEB128 value exceeds 64 bits.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32Leb128()
    {
        var result = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = ReadByte();
            result |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                if (i == 4 && (b & 0x78) is not (0x00 or 0x78))
                    WasmDecodeException.Throw("Signed LEB128 value exceeds 32 bits.");
                shift += 7;
                if (shift < 32 && (b & 0x40) != 0)
                    result |= -1 << shift;
                return result;
            }

            shift += 7;
        }

        WasmDecodeException.Throw("Signed LEB128 value exceeds 32 bits.");
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64Leb128()
    {
        long result = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = ReadByte();
            result |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                if (i == 9 && (b & 0x7f) is not (0x00 or 0x7f))
                    WasmDecodeException.Throw("Signed LEB128 value exceeds 64 bits.");
                shift += 7;
                if (shift < 64 && (b & 0x40) != 0)
                    result |= -1L << shift;
                return result;
            }

            shift += 7;
        }

        WasmDecodeException.Throw("Signed LEB128 value exceeds 64 bits.");
        return 0;
    }
}
