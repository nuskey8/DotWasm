using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace DotWasm.Runtime;

static class Vector128Helper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> AddSaturateSignedSByte(Vector128<byte> a, Vector128<byte> b)
    {
        var sa = a.AsSByte();
        var sb = b.AsSByte();
        var sum = Vector128.Add(sa, sb);
        var posOverflow =
            Vector128.GreaterThanOrEqual(sa, Vector128<sbyte>.Zero)
            & Vector128.GreaterThanOrEqual(sb, Vector128<sbyte>.Zero)
            & Vector128.LessThan(sum, Vector128<sbyte>.Zero);
        var negOverflow =
            Vector128.LessThan(sa, Vector128<sbyte>.Zero)
            & Vector128.LessThan(sb, Vector128<sbyte>.Zero)
            & Vector128.GreaterThanOrEqual(sum, Vector128<sbyte>.Zero);
        return Vector128
            .ConditionalSelect(
                posOverflow,
                Vector128.Create(sbyte.MaxValue),
                Vector128.ConditionalSelect(negOverflow, Vector128.Create(sbyte.MinValue), sum)
            )
            .AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SubtractSaturateSignedSByte(Vector128<byte> a, Vector128<byte> b)
    {
        var sa = a.AsSByte();
        var sb = b.AsSByte();
        var diff = Vector128.Subtract(sa, sb);
        var posOverflow =
            Vector128.GreaterThanOrEqual(sa, Vector128<sbyte>.Zero)
            & Vector128.LessThan(sb, Vector128<sbyte>.Zero)
            & Vector128.LessThan(diff, Vector128<sbyte>.Zero);
        var negOverflow =
            Vector128.LessThan(sa, Vector128<sbyte>.Zero)
            & Vector128.GreaterThanOrEqual(sb, Vector128<sbyte>.Zero)
            & Vector128.GreaterThanOrEqual(diff, Vector128<sbyte>.Zero);
        return Vector128
            .ConditionalSelect(
                posOverflow,
                Vector128.Create(sbyte.MaxValue),
                Vector128.ConditionalSelect(negOverflow, Vector128.Create(sbyte.MinValue), diff)
            )
            .AsByte();
    }
}
