namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    readonly struct I32WrapI64 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI32((int)context.valueStack.UnsafePop().I64));
    }

    readonly struct I32TruncF32S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F32;
            TruncHelper.ThrowIfInvalidTrunc(value, -2147483649.0, 2147483648.0);
            context.valueStack.Push(WasmValue.FromI32((int)value));
        }
    }

    readonly struct I32TruncF32U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F32;
            TruncHelper.ThrowIfInvalidTrunc(value, -1.0, 4294967296.0);
            context.valueStack.Push(WasmValue.FromI32((int)(uint)value));
        }
    }

    readonly struct I32TruncF64S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F64;
            TruncHelper.ThrowIfInvalidTrunc(value, -2147483649.0, 2147483648.0);
            context.valueStack.Push(WasmValue.FromI32((int)value));
        }
    }

    readonly struct I32TruncF64U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F64;
            TruncHelper.ThrowIfInvalidTrunc(value, -1.0, 4294967296.0);
            context.valueStack.Push(WasmValue.FromI32((int)(uint)value));
        }
    }

    readonly struct I64ExtendI32S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI64(context.valueStack.UnsafePop().I32));
    }

    readonly struct I64ExtendI32U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI64((uint)context.valueStack.UnsafePop().I32));
    }

    readonly struct I64TruncF32S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F32;
            TruncHelper.ThrowIfInvalidTrunc(
                value,
                -9223372036854777856.0,
                9223372036854775808.0
            );
            context.valueStack.Push(WasmValue.FromI64((long)value));
        }
    }

    readonly struct I64TruncF32U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F32;
            TruncHelper.ThrowIfInvalidTrunc(value, -1.0, 18446744073709551616.0);
            context.valueStack.Push(WasmValue.FromI64((long)(ulong)value));
        }
    }

    readonly struct I64TruncF64S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F64;
            TruncHelper.ThrowIfInvalidTrunc(
                value,
                -9223372036854777856.0,
                9223372036854775808.0
            );
            context.valueStack.Push(WasmValue.FromI64((long)value));
        }
    }

    readonly struct I64TruncF64U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context)
        {
            var value = context.valueStack.UnsafePop().F64;
            TruncHelper.ThrowIfInvalidTrunc(value, -1.0, 18446744073709551616.0);
            context.valueStack.Push(WasmValue.FromI64((long)(ulong)value));
        }
    }

    readonly struct F32ConvertI32S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF32(context.valueStack.UnsafePop().I32));
    }

    readonly struct F32ConvertI32U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF32((uint)context.valueStack.UnsafePop().I32));
    }

    readonly struct F32ConvertI64S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF32(context.valueStack.UnsafePop().I64));
    }

    readonly struct F32ConvertI64U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF32((ulong)context.valueStack.UnsafePop().I64));
    }

    readonly struct F32DemoteF64 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF32((float)context.valueStack.UnsafePop().F64));
    }

    readonly struct F64ConvertI32S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF64(context.valueStack.UnsafePop().I32));
    }

    readonly struct F64ConvertI32U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF64((uint)context.valueStack.UnsafePop().I32));
    }

    readonly struct F64ConvertI64S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF64(context.valueStack.UnsafePop().I64));
    }

    readonly struct F64ConvertI64U : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF64((ulong)context.valueStack.UnsafePop().I64));
    }

    readonly struct F64PromoteF32 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromF64(context.valueStack.UnsafePop().F32));
    }

    readonly struct I32ReinterpretF32 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(
                WasmValue.FromI32(BitConverter.SingleToInt32Bits(context.valueStack.UnsafePop().F32))
            );
    }

    readonly struct I64ReinterpretF64 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(
                WasmValue.FromI64(BitConverter.DoubleToInt64Bits(context.valueStack.UnsafePop().F64))
            );
    }

    readonly struct F32ReinterpretI32 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(
                WasmValue.FromF32(BitConverter.Int32BitsToSingle(context.valueStack.UnsafePop().I32))
            );
    }

    readonly struct F64ReinterpretI64 : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(
                WasmValue.FromF64(BitConverter.Int64BitsToDouble(context.valueStack.UnsafePop().I64))
            );
    }

    readonly struct I32Extend8S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI32((sbyte)context.valueStack.UnsafePop().I32));
    }

    readonly struct I32Extend16S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI32((short)context.valueStack.UnsafePop().I32));
    }

    readonly struct I64Extend8S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI64((sbyte)context.valueStack.UnsafePop().I64));
    }

    readonly struct I64Extend16S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI64((short)context.valueStack.UnsafePop().I64));
    }

    readonly struct I64Extend32S : IConversionOperator
    {
        public static void Apply(WasmExecutionContext context) =>
            context.valueStack.Push(WasmValue.FromI64((int)context.valueStack.UnsafePop().I64));
    }
}
