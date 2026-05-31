using DotWasm.Models;

namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    readonly struct WasmRefNull<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.valueStack.Push(WasmValue.FromRaw((ulong)(long)FunctionAddress.Null));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmRefIsNull<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var value = context.valueStack.UnsafePop();
            context.valueStack.Push(WasmValue.FromI32(value.IsNullReference ? 1 : 0));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmRefAsNonNull<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var value = context.valueStack.UnsafePop();
            if (value.IsNullReference)
                WasmTrapException.Throw("null reference");
            context.valueStack.Push(value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmRefEq<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var right = context.valueStack.UnsafePop();
            var left = context.valueStack.UnsafePop();
            context.valueStack.Push(
                WasmValue.FromI32(WasmValue.ReferenceEquals(left, right) ? 1 : 0)
            );
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmRefFunc<TFunctionIndex, TNext> : IWasmCompiledOp
        where TFunctionIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var functionAddress = frame.Instance.GetFunctionAddress(TFunctionIndex.Value);
            context.valueStack.Push(WasmValue.FromRaw((ulong)(long)functionAddress));
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmBrOnNull<TLabelIndex, TNext> : IWasmCompiledOp
        where TLabelIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var value = context.valueStack.UnsafePop();
            if (value.IsNullReference)
                return WasmOpResult.Branch(TLabelIndex.Value);

            context.valueStack.Push(value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmBrOnNonNull<TLabelIndex, TNext> : IWasmCompiledOp
        where TLabelIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var value = context.valueStack.UnsafePop();
            if (!value.IsNullReference)
            {
                context.valueStack.Push(value);
                return WasmOpResult.Branch(TLabelIndex.Value);
            }

            return TNext.Run(context, ref frame);
        }
    }
}
