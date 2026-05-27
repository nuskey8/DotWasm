using System.Runtime.CompilerServices;

namespace DotWasm.Runtime;

internal static class TruncHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TruncSatI32S(float value) => TruncSatI32S((double)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TruncSatI32S(double value)
    {
        if (double.IsNaN(value))
            return 0;
        if (value <= int.MinValue)
            return int.MinValue;
        if (value >= int.MaxValue)
            return int.MaxValue;
        return (int)Math.Truncate(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint TruncSatI32U(float value) => TruncSatI32U((double)value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint TruncSatI32U(double value)
    {
        if (double.IsNaN(value) || value <= 0)
            return 0;
        if (value >= uint.MaxValue)
            return uint.MaxValue;
        return (uint)Math.Truncate(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long TruncSatI64S(double value)
    {
        if (double.IsNaN(value))
            return 0;
        if (value <= long.MinValue)
            return long.MinValue;
        if (value >= long.MaxValue)
            return long.MaxValue;
        return (long)Math.Truncate(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong TruncSatI64U(double value)
    {
        if (double.IsNaN(value) || value <= 0)
            return 0;
        if (value >= ulong.MaxValue)
            return ulong.MaxValue;
        return (ulong)Math.Truncate(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfInvalidTrunc(double value, double minExclusive, double maxExclusive)
    {
        if (double.IsNaN(value))
            WasmTrapException.Throw("Invalid conversion to integer");

        if (value <= minExclusive || value >= maxExclusive)
            WasmTrapException.Throw("Integer overflow");
    }
}
