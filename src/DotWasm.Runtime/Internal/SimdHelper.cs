using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotWasm.Runtime;

internal static class SimdHelper
{
    public const int VectorByteLength = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToBytesFromVector(Vector128<byte> value, Span<byte> bytes)
    {
        var v = value.AsUInt64();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, v.GetElement(0));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], v.GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<byte> Read(Span<byte> data, int address, uint offset, int width) =>
        data.Slice(CalcMemoryAddress((uint)address, offset, width, data.Length), width);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcMemoryAddress(uint address, uint offset, int width, int memoryLength)
    {
        var effectiveAddress = (ulong)address + offset;
        WasmTrapException.ThrowIfNot(
            effectiveAddress + (uint)width <= (uint)memoryLength,
            "Memory access out of bounds"
        );
        return (int)effectiveAddress;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadSigned(ReadOnlySpan<byte> bytes, int lane, int laneBytes) =>
        laneBytes switch
        {
            1 => (sbyte)bytes[lane],
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes[(lane * 2)..]),
            4 => BinaryPrimitives.ReadInt32LittleEndian(bytes[(lane * 4)..]),
            _ => BinaryPrimitives.ReadInt64LittleEndian(bytes[(lane * 8)..]),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUnsigned(ReadOnlySpan<byte> bytes, int lane, int laneBytes) =>
        laneBytes switch
        {
            1 => bytes[lane],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes[(lane * 2)..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[(lane * 4)..]),
            _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes[(lane * 8)..]),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSigned(Span<byte> bytes, int lane, int laneBytes, long value) =>
        WriteUnsigned(bytes, lane, laneBytes, (ulong)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnsigned(Span<byte> bytes, int lane, int laneBytes, ulong value)
    {
        switch (laneBytes)
        {
            case 1:
                bytes[lane] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes[(lane * 2)..], (ushort)value);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[(lane * 4)..], (uint)value);
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes[(lane * 8)..], value);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AllTrue(Vector128<byte> v, int laneBytes)
    {
        var bits = laneBytes switch
        {
            1 => Vector128.Equals(v, Vector128<byte>.Zero).ExtractMostSignificantBits(),
            2 => Vector128
                .Equals(v.AsUInt16(), Vector128<ushort>.Zero)
                .ExtractMostSignificantBits(),
            4 => Vector128.Equals(v.AsUInt32(), Vector128<uint>.Zero).ExtractMostSignificantBits(),
            _ => Vector128.Equals(v.AsUInt64(), Vector128<ulong>.Zero).ExtractMostSignificantBits(),
        };
        return bits == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AllTrue(Vector128<byte> v) => AllTrue(v, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bitmask(Vector128<byte> v, int laneBytes)
    {
        var mask = laneBytes switch
        {
            1 => v.ExtractMostSignificantBits(),
            2 => v.AsUInt16().ExtractMostSignificantBits(),
            4 => v.AsUInt32().ExtractMostSignificantBits(),
            _ => v.AsUInt64().ExtractMostSignificantBits(),
        };
        return (int)mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bitmask(Vector128<byte> v) => Bitmask(v, 1);

    public static Vector128<byte> CompareInt(
        uint op,
        int laneBytes,
        Vector128<byte> a,
        Vector128<byte> b
    )
    {
        return laneBytes switch
        {
            1 => op switch
            {
                0 => Vector128.Equals(a, b),
                1 => ~Vector128.Equals(a, b),
                2 => Vector128.LessThan(a.AsSByte(), b.AsSByte()).AsByte(),
                3 => Vector128.LessThan(a, b),
                4 => Vector128.GreaterThan(a.AsSByte(), b.AsSByte()).AsByte(),
                5 => Vector128.GreaterThan(a, b),
                6 => Vector128.LessThanOrEqual(a.AsSByte(), b.AsSByte()).AsByte(),
                7 => Vector128.LessThanOrEqual(a, b),
                8 => Vector128.GreaterThanOrEqual(a.AsSByte(), b.AsSByte()).AsByte(),
                _ => Vector128.GreaterThanOrEqual(a, b),
            },
            2 => op switch
            {
                0 => Vector128.Equals(a.AsUInt16(), b.AsUInt16()).AsByte(),
                1 => ~Vector128.Equals(a.AsUInt16(), b.AsUInt16()).AsByte(),
                2 => Vector128.LessThan(a.AsInt16(), b.AsInt16()).AsByte(),
                3 => Vector128.LessThan(a.AsUInt16(), b.AsUInt16()).AsByte(),
                4 => Vector128.GreaterThan(a.AsInt16(), b.AsInt16()).AsByte(),
                5 => Vector128.GreaterThan(a.AsUInt16(), b.AsUInt16()).AsByte(),
                6 => Vector128.LessThanOrEqual(a.AsInt16(), b.AsInt16()).AsByte(),
                7 => Vector128.LessThanOrEqual(a.AsUInt16(), b.AsUInt16()).AsByte(),
                8 => Vector128.GreaterThanOrEqual(a.AsInt16(), b.AsInt16()).AsByte(),
                _ => Vector128.GreaterThanOrEqual(a.AsUInt16(), b.AsUInt16()).AsByte(),
            },
            4 => op switch
            {
                0 => Vector128.Equals(a.AsUInt32(), b.AsUInt32()).AsByte(),
                1 => ~Vector128.Equals(a.AsUInt32(), b.AsUInt32()).AsByte(),
                2 => Vector128.LessThan(a.AsInt32(), b.AsInt32()).AsByte(),
                3 => Vector128.LessThan(a.AsUInt32(), b.AsUInt32()).AsByte(),
                4 => Vector128.GreaterThan(a.AsInt32(), b.AsInt32()).AsByte(),
                5 => Vector128.GreaterThan(a.AsUInt32(), b.AsUInt32()).AsByte(),
                6 => Vector128.LessThanOrEqual(a.AsInt32(), b.AsInt32()).AsByte(),
                7 => Vector128.LessThanOrEqual(a.AsUInt32(), b.AsUInt32()).AsByte(),
                8 => Vector128.GreaterThanOrEqual(a.AsInt32(), b.AsInt32()).AsByte(),
                _ => Vector128.GreaterThanOrEqual(a.AsUInt32(), b.AsUInt32()).AsByte(),
            },
            _ => op switch
            {
                0 => Vector128.Equals(a.AsUInt64(), b.AsUInt64()).AsByte(),
                1 => ~Vector128.Equals(a.AsUInt64(), b.AsUInt64()).AsByte(),
                2 => Vector128.LessThan(a.AsInt64(), b.AsInt64()).AsByte(),
                3 => Vector128.GreaterThan(a.AsInt64(), b.AsInt64()).AsByte(),
                4 => Vector128.LessThanOrEqual(a.AsInt64(), b.AsInt64()).AsByte(),
                _ => Vector128.GreaterThanOrEqual(a.AsInt64(), b.AsInt64()).AsByte(),
            },
        };
    }

    public static Vector128<byte> CompareFloat32(uint op, Vector128<byte> a, Vector128<byte> b)
    {
        var va = a.AsSingle();
        var vb = b.AsSingle();
        var cmp = op switch
        {
            0 => Vector128.Equals(va, vb),
            1 => ~Vector128.Equals(va, vb),
            2 => Vector128.LessThan(va, vb),
            3 => Vector128.GreaterThan(va, vb),
            4 => Vector128.LessThanOrEqual(va, vb),
            _ => Vector128.GreaterThanOrEqual(va, vb),
        };
        return cmp.AsByte();
    }

    public static Vector128<byte> CompareFloat64(uint op, Vector128<byte> a, Vector128<byte> b)
    {
        var va = a.AsDouble();
        var vb = b.AsDouble();
        var cmp = op switch
        {
            0 => Vector128.Equals(va, vb),
            1 => ~Vector128.Equals(va, vb),
            2 => Vector128.LessThan(va, vb),
            3 => Vector128.GreaterThan(va, vb),
            4 => Vector128.LessThanOrEqual(va, vb),
            _ => Vector128.GreaterThanOrEqual(va, vb),
        };
        return cmp.AsByte();
    }

    public static Vector128<float> PseudoMinFloat32(Vector128<float> a, Vector128<float> b)
    {
        Span<float> result = stackalloc float[4];
        for (var i = 0; i < 4; i++)
        {
            var x = a.GetElement(i);
            var y = b.GetElement(i);
            result[i] = float.IsNaN(x) || float.IsNaN(y) || x <= y ? x : y;
        }
        return MemoryMarshal.Read<Vector128<float>>(MemoryMarshal.AsBytes(result));
    }

    public static Vector128<float> PseudoMaxFloat32(Vector128<float> a, Vector128<float> b)
    {
        Span<float> result = stackalloc float[4];
        for (var i = 0; i < 4; i++)
        {
            var x = a.GetElement(i);
            var y = b.GetElement(i);
            result[i] = float.IsNaN(x) || float.IsNaN(y) || x >= y ? x : y;
        }
        return MemoryMarshal.Read<Vector128<float>>(MemoryMarshal.AsBytes(result));
    }

    public static Vector128<double> PseudoMinFloat64(Vector128<double> a, Vector128<double> b)
    {
        Span<double> result = stackalloc double[2];
        for (var i = 0; i < 2; i++)
        {
            var x = a.GetElement(i);
            var y = b.GetElement(i);
            result[i] = double.IsNaN(x) || double.IsNaN(y) || x <= y ? x : y;
        }
        return MemoryMarshal.Read<Vector128<double>>(MemoryMarshal.AsBytes(result));
    }

    public static Vector128<double> PseudoMaxFloat64(Vector128<double> a, Vector128<double> b)
    {
        Span<double> result = stackalloc double[2];
        for (var i = 0; i < 2; i++)
        {
            var x = a.GetElement(i);
            var y = b.GetElement(i);
            result[i] = double.IsNaN(x) || double.IsNaN(y) || x >= y ? x : y;
        }
        return MemoryMarshal.Read<Vector128<double>>(MemoryMarshal.AsBytes(result));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AverageRoundByteVector(Vector128<byte> a, Vector128<byte> b)
    {
        Span<byte> result = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
            result[i] = (byte)((a.GetElement(i) + b.GetElement(i) + 1) >> 1);
        return MemoryMarshal.Read<Vector128<byte>>(result);
    }

    public static Vector128<byte> AverageRoundUInt16Vector(Vector128<byte> a, Vector128<byte> b)
    {
        Span<byte> av = stackalloc byte[16];
        Span<byte> bv = stackalloc byte[16];
        Span<byte> result = stackalloc byte[16];
        ToBytesFromVector(a, av);
        ToBytesFromVector(b, bv);
        for (var i = 0; i < 8; i++)
        {
            var x = BinaryPrimitives.ReadUInt16LittleEndian(av[(i * 2)..]);
            var y = BinaryPrimitives.ReadUInt16LittleEndian(bv[(i * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(result[(i * 2)..], (ushort)((x + y + 1) >> 1));
        }
        return MemoryMarshal.Read<Vector128<byte>>(result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AverageRoundIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    )
    {
        if (laneBytes == 2)
            return AverageRoundUInt16Vector(a, b);
        if (laneBytes == 4)
        {
            var sum = Vector128.Add(a.AsUInt32(), b.AsUInt32());
            var withOne = Vector128.Add(sum, Vector128<uint>.One);
            return Vector128.ShiftRightLogical(withOne, 1).AsByte();
        }
        var sum64 = Vector128.Add(a.AsUInt64(), b.AsUInt64());
        var withOne64 = Vector128.Add(sum64, Vector128<ulong>.One);
        return Vector128.ShiftRightLogical(withOne64, 1).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AbsIntVector(Vector128<byte> v, int laneBytes) =>
        laneBytes switch
        {
            2 => Vector128.Abs(v.AsInt16()).AsByte(),
            4 => Vector128.Abs(v.AsInt32()).AsByte(),
            _ => Vector128.Abs(v.AsInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> NegateIntVector(Vector128<byte> v, int laneBytes) =>
        laneBytes switch
        {
            2 => Vector128.Negate(v.AsInt16()).AsByte(),
            4 => Vector128.Negate(v.AsInt32()).AsByte(),
            _ => Vector128.Negate(v.AsInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftLeftIntVector(Vector128<byte> v, int laneBytes, int shift) =>
        laneBytes switch
        {
            2 => Vector128.ShiftLeft(v.AsUInt16(), shift).AsByte(),
            4 => Vector128.ShiftLeft(v.AsUInt32(), shift).AsByte(),
            _ => Vector128.ShiftLeft(v.AsUInt64(), shift).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightArithmeticIntVector(
        Vector128<byte> v,
        int laneBytes,
        int shift
    ) =>
        laneBytes switch
        {
            2 => Vector128.ShiftRightArithmetic(v.AsInt16(), shift).AsByte(),
            4 => Vector128.ShiftRightArithmetic(v.AsInt32(), shift).AsByte(),
            _ => Vector128.ShiftRightArithmetic(v.AsInt64(), shift).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> ShiftRightLogicalIntVector(
        Vector128<byte> v,
        int laneBytes,
        int shift
    ) =>
        laneBytes switch
        {
            2 => Vector128.ShiftRightLogical(v.AsUInt16(), shift).AsByte(),
            4 => Vector128.ShiftRightLogical(v.AsUInt32(), shift).AsByte(),
            _ => Vector128.ShiftRightLogical(v.AsUInt64(), shift).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Add(a.AsUInt16(), b.AsUInt16()).AsByte(),
            4 => Vector128.Add(a.AsUInt32(), b.AsUInt32()).AsByte(),
            _ => Vector128.Add(a.AsUInt64(), b.AsUInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SubtractIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Subtract(a.AsUInt16(), b.AsUInt16()).AsByte(),
            4 => Vector128.Subtract(a.AsUInt32(), b.AsUInt32()).AsByte(),
            _ => Vector128.Subtract(a.AsUInt64(), b.AsUInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MultiplyIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Multiply(a.AsUInt16(), b.AsUInt16()).AsByte(),
            4 => Vector128.Multiply(a.AsUInt32(), b.AsUInt32()).AsByte(),
            _ => Vector128.Multiply(a.AsUInt64(), b.AsUInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MinSignedIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Min(a.AsInt16(), b.AsInt16()).AsByte(),
            4 => Vector128.Min(a.AsInt32(), b.AsInt32()).AsByte(),
            _ => Vector128.Min(a.AsInt64(), b.AsInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MinUnsignedIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Min(a.AsUInt16(), b.AsUInt16()).AsByte(),
            4 => Vector128.Min(a.AsUInt32(), b.AsUInt32()).AsByte(),
            _ => Vector128.Min(a.AsUInt64(), b.AsUInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MaxSignedIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Max(a.AsInt16(), b.AsInt16()).AsByte(),
            4 => Vector128.Max(a.AsInt32(), b.AsInt32()).AsByte(),
            _ => Vector128.Max(a.AsInt64(), b.AsInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> MaxUnsignedIntVector(
        Vector128<byte> a,
        Vector128<byte> b,
        int laneBytes
    ) =>
        laneBytes switch
        {
            2 => Vector128.Max(a.AsUInt16(), b.AsUInt16()).AsByte(),
            4 => Vector128.Max(a.AsUInt32(), b.AsUInt32()).AsByte(),
            _ => Vector128.Max(a.AsUInt64(), b.AsUInt64()).AsByte(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Q15MulrSat(long x, long y)
    {
        var result = (x * y + 0x4000) >> 15;
        return Math.Clamp(result, short.MinValue, short.MaxValue);
    }

    public static Vector128<int> Q15MulrVector(Vector128<int> a, Vector128<int> b) =>
        Vector128.ShiftRightArithmetic(
            Vector128.Add(Vector128.Multiply(a, b), Vector128.Create(0x4000)),
            15
        );

    public static void RelaxedDotI8x16I7x16(WasmValue b, WasmValue a, Span<int> result)
    {
        Span<short> products = stackalloc short[16];
        var av = a.V128.ToVector128().AsSByte();
        var bv = b.V128.ToVector128().AsSByte();
        var low = Vector128.Multiply(Vector128.WidenLower(av), Vector128.WidenLower(bv));
        var high = Vector128.Multiply(Vector128.WidenUpper(av), Vector128.WidenUpper(bv));
        low.CopyTo(products);
        high.CopyTo(products[8..]);
        for (var i = 0; i < 8; i++)
            result[i] = products[i * 2] + products[i * 2 + 1];
    }
}
