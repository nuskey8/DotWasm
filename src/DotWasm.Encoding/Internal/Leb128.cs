using System.Runtime.CompilerServices;

namespace DotWasm.Internal;

public static class Leb128
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUnsigned(ref byte data, ref int offset)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = Unsafe.Add(ref data, offset++);
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUnsigned(ReadOnlySpan<byte> bytes, ref int offset)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSigned(ref byte data, ref int offset)
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = Unsafe.Add(ref data, offset++);
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 32 && (b & 0x40) != 0)
            result |= -1 << shift;

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSigned(ReadOnlySpan<byte> bytes, ref int offset)
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = bytes[offset++];
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 32 && (b & 0x40) != 0)
            result |= -1 << shift;

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadSigned64(ref byte data, ref int offset)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = Unsafe.Add(ref data, offset++);
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
            result |= -1L << shift;

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadSigned64(ReadOnlySpan<byte> bytes, ref int offset)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = bytes[offset++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
            result |= -1L << shift;

        return result;
    }
}
