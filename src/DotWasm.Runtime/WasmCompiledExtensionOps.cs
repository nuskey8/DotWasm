using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using DotWasm.Models;

namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    interface IInstructionLiteral
    {
        abstract static Instruction Value { get; }
    }

    readonly struct InstructionLiteral<TId> : IInstructionLiteral
        where TId : ILiteral<int>
    {
        public static Instruction Value => InstructionLiteralFactory.Get(TId.Value);
    }

    static class InstructionLiteralFactory
    {
        static readonly Lock gate = new();
        static readonly List<Instruction> instructions = [];

        public static Type Create(Instruction instruction)
        {
            lock (gate)
            {
                var id = instructions.Count;
                instructions.Add(instruction);
                return typeof(InstructionLiteral<>).MakeGenericType(TypeLiteralFactory.CreateInt32(id));
            }
        }

        public static Instruction Get(int id) => instructions[id];
    }

    readonly struct WasmFCExtension<TInstruction, TNext> : IWasmCompiledOp
        where TInstruction : IInstructionLiteral
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            context.ExecuteFCExtensionInstruction(frame.Instance, TInstruction.Value);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmSIMDExtension<TInstruction, TNext> : IWasmCompiledOp
        where TInstruction : IInstructionLiteral
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var localBase = 0;
            var ip = 0;
            context.ExecuteSimdInstruction(frame.Instance, TInstruction.Value, ref localBase, ref ip);
            return TNext.Run(context, ref frame);
        }
    }

    readonly struct WasmGCExtension<TInstruction, TNext> : IWasmCompiledOp
        where TInstruction : IInstructionLiteral
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var instruction = TInstruction.Value;
            var gc = Unsafe.As<GCExtensionInstruction>(instruction);
            switch (gc.ExtensionCode)
            {
                case WasmOpCodes.BrOnCast:
                {
                    var br = Unsafe.As<BrOnCastInstruction>(instruction);
                    var value = context.valueStack.UnsafePop();
                    context.valueStack.Push(value);
                    return ReferenceMatches(frame.Instance, value, br.TargetReferenceType)
                        ? WasmOpResult.Branch((int)br.LabelIndex)
                        : TNext.Run(context, ref frame);
                }
                case WasmOpCodes.BrOnCastFail:
                {
                    var br = Unsafe.As<BrOnCastFailInstruction>(instruction);
                    var value = context.valueStack.UnsafePop();
                    context.valueStack.Push(value);
                    return !ReferenceMatches(frame.Instance, value, br.TargetReferenceType)
                        ? WasmOpResult.Branch((int)br.LabelIndex)
                        : TNext.Run(context, ref frame);
                }
                default:
                {
                    var localBase = 0;
                    var ip = 0;
                    context.ExecuteGCInstruction(frame.Instance, instruction, ref localBase, ref ip);
                    return TNext.Run(context, ref frame);
                }
            }
        }
    }

    readonly struct WasmThrow<TTagIndex, TNext> : IWasmCompiledOp
        where TTagIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var tag = frame.Instance.GetTagInstance(TTagIndex.Value);
            var arguments = new WasmValue[tag.Type.Type.Parameters.Length];
            context.valueStack.Take(arguments.Length).CopyTo(arguments);
            throw new WasmThrownException(frame.Instance.GetTagAddress(TTagIndex.Value), arguments);
        }
    }

    readonly struct WasmRethrow<TLabelIndex, TNext> : IWasmCompiledOp
        where TLabelIndex : ILiteral<int>
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            if (context.currentException is null)
                WasmTrapException.Throw("No active WebAssembly exception.");
            throw context.currentException;
        }
    }

    readonly struct WasmThrowRef<TNext> : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var value = context.valueStack.UnsafePop();
            var index = value.Bits;
            if (index == 0 || index == (ulong)(long)FunctionAddress.Null)
                return WasmOpResult.Return;
            if (index > (uint)context.exceptionRefs.Count)
                WasmTrapException.Throw("invalid exception reference");

            var exception = context.exceptionRefs[(int)index - 1];
            if (exception is null)
                WasmTrapException.Throw("null exception reference");
            throw exception;
        }
    }

    interface IWasmCatchHandler
    {
        abstract static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        );
    }

    readonly struct WasmNoCatch : IWasmCatchHandler
    {
        public static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        )
        {
            result = default;
            return false;
        }
    }

    readonly struct WasmCatch<TTagIndex, TBody, TNextHandler> : IWasmCatchHandler
        where TTagIndex : ILiteral<int>
        where TBody : IWasmCompiledOp
        where TNextHandler : IWasmCatchHandler
    {
        public static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        )
        {
            if (exception.TagAddress != frame.Instance.GetTagAddress(TTagIndex.Value))
                return TNextHandler.TryHandle(context, ref frame, stackBase, exception, out result);

            result = context.RunCatchBody<TBody>(ref frame, stackBase, exception, pushExceptionRef: false);
            return true;
        }
    }

    readonly struct WasmCatchAll<TBody, TNextHandler> : IWasmCatchHandler
        where TBody : IWasmCompiledOp
        where TNextHandler : IWasmCatchHandler
    {
        public static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        )
        {
            result = context.RunCatchBody<TBody>(ref frame, stackBase, exception, pushExceptionRef: false);
            return true;
        }
    }

    readonly struct WasmTry<TParameterCount, TResultCount, TBody, TCatches, TNext> : IWasmCompiledOp
        where TParameterCount : ILiteral<int>
        where TResultCount : ILiteral<int>
        where TBody : IWasmCompiledOp
        where TCatches : IWasmCatchHandler
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var stackBase = context.valueStack.Count - TParameterCount.Value;
            try
            {
                var result = TBody.Run(context, ref frame);
                result = context.ResolveBlockResult(result, stackBase, TResultCount.Value);
                return result.Kind == WasmOpResultKind.Continue
                    ? TNext.Run(context, ref frame)
                    : result;
            }
            catch (WasmThrownException exception)
            {
                if (!TCatches.TryHandle(context, ref frame, stackBase, exception, out var result))
                    throw;

                result = context.ResolveBlockResult(result, stackBase, TResultCount.Value);
                return result.Kind == WasmOpResultKind.Continue
                    ? TNext.Run(context, ref frame)
                    : result;
            }
        }
    }

    interface ITryTableCatchHandler
    {
        abstract static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        );
    }

    readonly struct WasmNoTryTableCatch : ITryTableCatchHandler
    {
        public static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        )
        {
            result = default;
            return false;
        }
    }

    readonly struct WasmTryTableCatch<TKind, TTagIndex, TLabelIndex, TNextHandler>
        : ITryTableCatchHandler
        where TKind : ILiteral<int>
        where TTagIndex : ILiteral<int>
        where TLabelIndex : ILiteral<int>
        where TNextHandler : ITryTableCatchHandler
    {
        public static bool TryHandle(
            WasmExecutionContext context,
            ref WasmExecutionFrame frame,
            int stackBase,
            WasmThrownException exception,
            out WasmOpResult result
        )
        {
            var kind = TKind.Value;
            if (
                kind is 0 or 1
                && exception.TagAddress != frame.Instance.GetTagAddress(TTagIndex.Value)
            )
                return TNextHandler.TryHandle(context, ref frame, stackBase, exception, out result);

            if (kind is < 0 or > 3)
                WasmTrapException.Throw("Invalid catch clause.");

            context.valueStack.Truncate(stackBase);
            context.valueStack.PushRange(exception.Arguments);
            if (kind is 1 or 3)
                context.valueStack.Push(context.CreateExceptionRef(exception));
            context.currentException = exception;
            result = WasmOpResult.Branch(TLabelIndex.Value);
            return true;
        }
    }

    readonly struct WasmTryTable<TParameterCount, TResultCount, TBody, TCatches, TNext>
        : IWasmCompiledOp
        where TParameterCount : ILiteral<int>
        where TResultCount : ILiteral<int>
        where TBody : IWasmCompiledOp
        where TCatches : ITryTableCatchHandler
        where TNext : IWasmCompiledOp
    {
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var stackBase = context.valueStack.Count - TParameterCount.Value;
            try
            {
                var result = TBody.Run(context, ref frame);
                result = context.ResolveBlockResult(result, stackBase, TResultCount.Value);
                return result.Kind == WasmOpResultKind.Continue
                    ? TNext.Run(context, ref frame)
                    : result;
            }
            catch (WasmThrownException exception)
            {
                if (!TCatches.TryHandle(context, ref frame, stackBase, exception, out var result))
                    throw;
                return result;
            }
        }
    }

    WasmOpResult RunCatchBody<TBody>(
        ref WasmExecutionFrame frame,
        int stackBase,
        WasmThrownException exception,
        bool pushExceptionRef
    )
        where TBody : IWasmCompiledOp
    {
        valueStack.Truncate(stackBase);
        valueStack.PushRange(exception.Arguments);
        if (pushExceptionRef)
            valueStack.Push(CreateExceptionRef(exception));

        var previousException = currentException;
        currentException = exception;
        try
        {
            return TBody.Run(this, ref frame);
        }
        finally
        {
            currentException = previousException;
        }
    }
}
