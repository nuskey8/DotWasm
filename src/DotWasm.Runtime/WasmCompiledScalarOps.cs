using System.Numerics;
using System.Runtime.CompilerServices;

namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    interface IUnaryOperator<TValue, TResult>
    {
        abstract static TResult Apply(TValue value);
    }

    interface IBinaryOperator<TValue, TResult>
    {
        abstract static TResult Apply(TValue left, TValue right);
    }

    interface ICompareOperator<TValue>
    {
        abstract static bool Apply(TValue left, TValue right);
    }

    readonly struct WasmConst<TValue, TLiteral, TNext> : IWasmCompiledOp
        where TValue : INumberBase<TValue>
        where TLiteral : ILiteral<TValue>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            Push(context, TLiteral.Value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmUnary<TValue, TResult, TOp, TNext> : IWasmCompiledOp
        where TOp : IUnaryOperator<TValue, TResult>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            Push(context, TOp.Apply(Pop<TValue>(context)));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmBinary<TValue, TResult, TOp, TNext> : IWasmCompiledOp
        where TOp : IBinaryOperator<TValue, TResult>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var right = Pop<TValue>(context);
            var left = Pop<TValue>(context);
            Push(context, TOp.Apply(left, right));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmCompare<TValue, TOp, TNext> : IWasmCompiledOp
        where TOp : ICompareOperator<TValue>
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var right = Pop<TValue>(context);
            var left = Pop<TValue>(context);
            context.valueStack.Push(WasmValue.FromI32(TOp.Apply(left, right) ? 1 : 0));
            return TNext.Run(context, ref frame);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static TValue Pop<TValue>(WasmExecutionContext context)
    {
        if (typeof(TValue) == typeof(int))
        {
            var value = context.valueStack.UnsafePop().I32;
            return Unsafe.As<int, TValue>(ref value);
        }

        if (typeof(TValue) == typeof(long))
        {
            var value = context.valueStack.UnsafePop().I64;
            return Unsafe.As<long, TValue>(ref value);
        }

        if (typeof(TValue) == typeof(float))
        {
            var value = context.valueStack.UnsafePop().F32;
            return Unsafe.As<float, TValue>(ref value);
        }

        if (typeof(TValue) == typeof(double))
        {
            var value = context.valueStack.UnsafePop().F64;
            return Unsafe.As<double, TValue>(ref value);
        }

        return ThrowHelper.ThrowInvalidOperation<TValue>("Unsupported numeric stack type.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Push<TValue>(WasmExecutionContext context, TValue value)
    {
        if (typeof(TValue) == typeof(int))
        {
            context.valueStack.Push(WasmValue.FromI32(Unsafe.As<TValue, int>(ref value)));
            return;
        }

        if (typeof(TValue) == typeof(long))
        {
            context.valueStack.Push(WasmValue.FromI64(Unsafe.As<TValue, long>(ref value)));
            return;
        }

        if (typeof(TValue) == typeof(float))
        {
            context.valueStack.Push(WasmValue.FromF32(Unsafe.As<TValue, float>(ref value)));
            return;
        }

        if (typeof(TValue) == typeof(double))
        {
            context.valueStack.Push(WasmValue.FromF64(Unsafe.As<TValue, double>(ref value)));
            return;
        }

        ThrowHelper.ThrowInvalidOperation<object>("Unsupported numeric stack type.");
    }

    readonly struct Eqz<T> : IUnaryOperator<T, int>
        where T : INumberBase<T>
    {
        public static int Apply(T value) => T.IsZero(value) ? 1 : 0;
    }

    readonly struct Eq<T> : ICompareOperator<T>
        where T : IEqualityOperators<T, T, bool>
    {
        public static bool Apply(T left, T right) => left == right;
    }

    readonly struct Ne<T> : ICompareOperator<T>
        where T : IEqualityOperators<T, T, bool>
    {
        public static bool Apply(T left, T right) => left != right;
    }

    readonly struct Lt<T> : ICompareOperator<T>
        where T : IComparisonOperators<T, T, bool>
    {
        public static bool Apply(T left, T right) => left < right;
    }

    readonly struct Gt<T> : ICompareOperator<T>
        where T : IComparisonOperators<T, T, bool>
    {
        public static bool Apply(T left, T right) => left > right;
    }

    readonly struct Le<T> : ICompareOperator<T>
        where T : IComparisonOperators<T, T, bool>
    {
        public static bool Apply(T left, T right) => left <= right;
    }

    readonly struct Ge<T> : ICompareOperator<T>
        where T : IComparisonOperators<T, T, bool>
    {
        public static bool Apply(T left, T right) => left >= right;
    }

    readonly struct Add<T> : IBinaryOperator<T, T>
        where T : IAdditionOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left + right;
    }

    readonly struct Sub<T> : IBinaryOperator<T, T>
        where T : ISubtractionOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left - right;
    }

    readonly struct Mul<T> : IBinaryOperator<T, T>
        where T : IMultiplyOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left * right;
    }

    readonly struct Div<T> : IBinaryOperator<T, T>
        where T : IDivisionOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left / right;
    }

    readonly struct Neg<T> : IUnaryOperator<T, T>
        where T : IUnaryNegationOperators<T, T>
    {
        public static T Apply(T value) => -value;
    }

    readonly struct Abs<T> : IUnaryOperator<T, T>
        where T : INumber<T>
    {
        public static T Apply(T value) => T.Abs(value);
    }

    readonly struct Sqrt<T> : IUnaryOperator<T, T>
        where T : IRootFunctions<T>
    {
        public static T Apply(T value) => T.Sqrt(value);
    }

    readonly struct BitAnd<T> : IBinaryOperator<T, T>
        where T : IBitwiseOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left & right;
    }

    readonly struct BitOr<T> : IBinaryOperator<T, T>
        where T : IBitwiseOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left | right;
    }

    readonly struct BitXor<T> : IBinaryOperator<T, T>
        where T : IBitwiseOperators<T, T, T>
    {
        public static T Apply(T left, T right) => left ^ right;
    }

    readonly struct Ceil<T> : IUnaryOperator<T, T>
        where T : IFloatingPoint<T>
    {
        public static T Apply(T value) => T.Ceiling(value);
    }

    readonly struct Floor<T> : IUnaryOperator<T, T>
        where T : IFloatingPoint<T>
    {
        public static T Apply(T value) => T.Floor(value);
    }

    readonly struct Trunc<T> : IUnaryOperator<T, T>
        where T : IFloatingPoint<T>
    {
        public static T Apply(T value) => T.Truncate(value);
    }

    readonly struct Nearest<T> : IUnaryOperator<T, T>
        where T : IFloatingPoint<T>
    {
        public static T Apply(T value) => T.Round(value);
    }

    readonly struct Min<T> : IBinaryOperator<T, T>
        where T : INumber<T>
    {
        public static T Apply(T left, T right) => T.Min(left, right);
    }

    readonly struct Max<T> : IBinaryOperator<T, T>
        where T : INumber<T>
    {
        public static T Apply(T left, T right) => T.Max(left, right);
    }

    readonly struct CopySign<T> : IBinaryOperator<T, T>
        where T : INumber<T>
    {
        public static T Apply(T left, T right) => T.CopySign(left, right);
    }

    readonly struct I32LtU : ICompareOperator<int>
    {
        public static bool Apply(int left, int right) => (uint)left < (uint)right;
    }

    readonly struct I32GtU : ICompareOperator<int>
    {
        public static bool Apply(int left, int right) => (uint)left > (uint)right;
    }

    readonly struct I32LeU : ICompareOperator<int>
    {
        public static bool Apply(int left, int right) => (uint)left <= (uint)right;
    }

    readonly struct I32GeU : ICompareOperator<int>
    {
        public static bool Apply(int left, int right) => (uint)left >= (uint)right;
    }

    readonly struct I32Clz : IUnaryOperator<int, int>
    {
        public static int Apply(int value) => BitOperations.LeadingZeroCount((uint)value);
    }

    readonly struct I32Ctz : IUnaryOperator<int, int>
    {
        public static int Apply(int value) => BitOperations.TrailingZeroCount((uint)value);
    }

    readonly struct I32Popcnt : IUnaryOperator<int, int>
    {
        public static int Apply(int value) => BitOperations.PopCount((uint)value);
    }

    readonly struct I32DivS : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            if (left == int.MinValue && right == -1)
                WasmTrapException.Throw("Integer overflow");
            return left / right;
        }
    }

    readonly struct I32DivU : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            return (int)((uint)left / (uint)right);
        }
    }

    readonly struct I32RemS : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            return left == int.MinValue && right == -1 ? 0 : left % right;
        }
    }

    readonly struct I32RemU : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            return (int)((uint)left % (uint)right);
        }
    }

    readonly struct I32Shl : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right) => left << (right & 0x1F);
    }

    readonly struct I32ShrS : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right) => left >> (right & 0x1F);
    }

    readonly struct I32ShrU : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right) => (int)((uint)left >> (right & 0x1F));
    }

    readonly struct I32Rotl : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right) =>
            (int)BitOperations.RotateLeft((uint)left, right & 0x1F);
    }

    readonly struct I32Rotr : IBinaryOperator<int, int>
    {
        public static int Apply(int left, int right) =>
            (int)BitOperations.RotateRight((uint)left, right & 0x1F);
    }

    readonly struct I64LtU : ICompareOperator<long>
    {
        public static bool Apply(long left, long right) => (ulong)left < (ulong)right;
    }

    readonly struct I64GtU : ICompareOperator<long>
    {
        public static bool Apply(long left, long right) => (ulong)left > (ulong)right;
    }

    readonly struct I64LeU : ICompareOperator<long>
    {
        public static bool Apply(long left, long right) => (ulong)left <= (ulong)right;
    }

    readonly struct I64GeU : ICompareOperator<long>
    {
        public static bool Apply(long left, long right) => (ulong)left >= (ulong)right;
    }

    readonly struct I64Clz : IUnaryOperator<long, long>
    {
        public static long Apply(long value) => BitOperations.LeadingZeroCount((ulong)value);
    }

    readonly struct I64Ctz : IUnaryOperator<long, long>
    {
        public static long Apply(long value) => BitOperations.TrailingZeroCount((ulong)value);
    }

    readonly struct I64Popcnt : IUnaryOperator<long, long>
    {
        public static long Apply(long value) => BitOperations.PopCount((ulong)value);
    }

    readonly struct I64DivS : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            if (left == long.MinValue && right == -1)
                WasmTrapException.Throw("Integer overflow");
            return left / right;
        }
    }

    readonly struct I64DivU : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            return (long)((ulong)left / (ulong)right);
        }
    }

    readonly struct I64RemS : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            return left == long.MinValue && right == -1 ? 0 : left % right;
        }
    }

    readonly struct I64RemU : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right)
        {
            if (right == 0)
                WasmTrapException.Throw("Division by zero");
            return (long)((ulong)left % (ulong)right);
        }
    }

    readonly struct I64Shl : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right) => left << (int)(right & 0x3F);
    }

    readonly struct I64ShrS : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right) => left >> (int)(right & 0x3F);
    }

    readonly struct I64ShrU : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right) => (long)((ulong)left >> (int)(right & 0x3F));
    }

    readonly struct I64Rotl : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right) =>
            (long)BitOperations.RotateLeft((ulong)left, (int)(right & 0x3F));
    }

    readonly struct I64Rotr : IBinaryOperator<long, long>
    {
        public static long Apply(long left, long right) =>
            (long)BitOperations.RotateRight((ulong)left, (int)(right & 0x3F));
    }

    interface IConversionOperator
    {
        abstract static void Apply(WasmExecutionContext context);
    }

    readonly struct WasmConvert<TOp, TNext> : IWasmCompiledOp
        where TOp : IConversionOperator
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            TOp.Apply(context);
            return TNext.Run(context, ref frame);
        }
    }
}
