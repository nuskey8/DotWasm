using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using DotWasm.Models;

namespace DotWasm.Runtime;

[StructLayout(LayoutKind.Auto)]
internal readonly struct ControlFrame
{
    public required ControlFrameKind Kind { get; init; }
    public required int StartCodeOffset { get; init; }
    public required int EndCodeOffset { get; init; }
    public required int LocalBase { get; init; }
    public required int StackBase { get; init; }
    public required int ParameterCount { get; init; }
    public required int ResultCount { get; init; }
}

internal enum ControlFrameKind : byte
{
    Block,
    Loop,
    If,
    Try,
    TryTable,
}

internal sealed partial class WasmExecutionContext
{
    const int MaxCallStackDepth = 256;

    static readonly ConcurrentStack<WasmExecutionContext> pool = new();

    public static WasmExecutionContext Rent()
    {
        if (pool.TryPop(out var context))
        {
            return context;
        }
        else
        {
            return new WasmExecutionContext();
        }
    }

    public static void Return(WasmExecutionContext context)
    {
        context.Clear();
        pool.Push(context);
    }

    MinimumStackCore<WasmValue> valueStack = new(256);
    MinimumStackCore<WasmValue> localStack = new(256);
    MinimumStackCore<ControlFrame> controlStack;
    MinimumStackCore<FunctionInstance> callStack;
    readonly List<WasmThrownException> exceptionRefs = [];
    WasmThrownException? currentException;

    public ReadOnlySpan<WasmValue> StackSpan => valueStack.AsSpan();

    public void Clear()
    {
        valueStack.Clear();
        localStack.Clear();
        controlStack.Clear();
        callStack.Clear();
        exceptionRefs.Clear();
        currentException = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushValues(ReadOnlySpan<WasmValue> values)
    {
        valueStack.PushRange(values);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TakeValues(Span<WasmValue> destination)
    {
        var span = valueStack.Take(destination.Length);
        span.CopyTo(destination);
    }

    public void ExecuteFunction(WasmInstance instance, FunctionInstance function)
    {
        if (callStack.Count >= MaxCallStackDepth)
            WasmTrapException.Throw("Call stack exhausted.");

        callStack.Push(function);

        try
        {
            while (true)
            {
                switch (function)
                {
                    case RuntimeFunction func:
                    {
                        instance = func.Owner;

                        var type = GetFlatType(instance.Module, func.Definition.TypeIndex);
                        var tailCall = Execute(
                            instance,
                            func.Definition.Body,
                            type.Parameters.Length,
                            func.Definition.Locals.Length,
                            type.Results.Length
                        );

                        if (tailCall is null)
                            return;

                        function = tailCall.Value;
                        break;
                    }
                    case HostFunction hostFunc:
                    {
                        var type = hostFunc.Type;
                        var arguments = valueStack.Take(type.Parameters.Length);
                        Span<WasmValue> results = new WasmValue[type.Results.Length];
                        hostFunc.Delegate(arguments, results);
                        valueStack.PushRange(results);
                        return;
                    }
                }
            }
        }
        finally
        {
            callStack.Pop();
        }
    }

    public FunctionInstance? Execute(
        WasmInstance instance,
        Expression expression,
        int argCount,
        int localCount,
        int resultCount
    )
    {
        var localRegisterCount = localCount + argCount;
        var localStackBase = localStack.Count;
        Span<WasmValue> locals = localStack.Allocate(localRegisterCount);
        var arguments = valueStack.Take(argCount);
        arguments.CopyTo(locals);

        var compiledExpression = WasmCompiledExpression.GetOrCompile(expression);
        if (compiledExpression.IsCompiled)
        {
            var functionStackBase = valueStack.Count - argCount;

            try
            {
                var frame = new WasmExecutionFrame(
                    instance,
                    locals,
                    functionStackBase,
                    resultCount
                );
                var result = compiledExpression.Invoke(this, ref frame);
                switch (result.Kind)
                {
                    case WasmOpResultKind.Continue:
                        return null;
                    case WasmOpResultKind.Return:
                        return null;
                    case WasmOpResultKind.TailCall:
                        return result.TailCall;
                    case WasmOpResultKind.Branch when result.LabelIndex == 0:
                        PreserveValues(functionStackBase, resultCount);
                        return null;
                    case WasmOpResultKind.Branch:
                        WasmTrapException.Throw("Invalid branch target.");
                        return null;
                    default:
                        return null;
                }
            }
            finally
            {
                locals.Clear();
                localStack.Truncate(localStackBase);
            }
        }

        int localBase = 0;
        var instructions = expression.Instructions;
        var controlBase = controlStack.Count;
        controlStack.Push(
            new ControlFrame
            {
                Kind = ControlFrameKind.Block,
                LocalBase = 0,
                StackBase = valueStack.Count - argCount,
                ParameterCount = argCount,
                ResultCount = resultCount,
                StartCodeOffset = 0,
                EndCodeOffset = instructions.Length - 1,
            }
        );

        try
        {
            int ip = 0;
            while (ip < instructions.Length)
            {
                var instr = instructions[ip];
                ip++;
                switch (instr.OpCode)
                {
                    case WasmOpCodes.Nop:
                        break;
                    case WasmOpCodes.Unreachable:
                        WasmTrapException.Throw("Unreachable code executed.");
                        break;
                    case WasmOpCodes.Block:
                    {
                        var block = Unsafe.As<BlockInstruction>(instr);
                        controlStack.Push(
                            new ControlFrame
                            {
                                Kind = ControlFrameKind.Block,
                                LocalBase = localBase,
                                StackBase = valueStack.Count - block.ParameterCount,
                                ParameterCount = block.ParameterCount,
                                ResultCount = block.ResultCount,
                                StartCodeOffset = ip,
                                EndCodeOffset = block.EndIndex,
                            }
                        );
                        break;
                    }
                    case WasmOpCodes.Loop:
                    {
                        var loop = Unsafe.As<LoopInstruction>(instr);
                        controlStack.Push(
                            new ControlFrame
                            {
                                Kind = ControlFrameKind.Loop,
                                StartCodeOffset = ip,
                                EndCodeOffset = loop.EndIndex,
                                LocalBase = localBase,
                                StackBase = valueStack.Count - loop.ParameterCount,
                                ParameterCount = loop.ParameterCount,
                                ResultCount = loop.ResultCount,
                            }
                        );
                        break;
                    }
                    case WasmOpCodes.If:
                    {
                        var ifInstr = Unsafe.As<IfInstruction>(instr);
                        var condition = valueStack.UnsafePop();
                        controlStack.Push(
                            new ControlFrame
                            {
                                Kind = ControlFrameKind.If,
                                StartCodeOffset = ip,
                                EndCodeOffset = ifInstr.EndIndex,
                                LocalBase = localBase,
                                StackBase = valueStack.Count - ifInstr.ParameterCount,
                                ParameterCount = ifInstr.ParameterCount,
                                ResultCount = ifInstr.ResultCount,
                            }
                        );

                        if (condition.I32 == 0)
                        {
                            if (ifInstr.ElseIndex >= 0)
                                ip = ifInstr.ElseIndex + 1;
                            else
                            {
                                ip = ifInstr.EndIndex + 1;
                                controlStack.Pop();
                            }
                        }
                        break;
                    }
                    case WasmOpCodes.Try:
                    {
                        var tryInstr = Unsafe.As<TryInstruction>(instr);
                        controlStack.Push(
                            new ControlFrame
                            {
                                Kind = ControlFrameKind.Try,
                                StartCodeOffset = ip,
                                EndCodeOffset = tryInstr.EndIndex,
                                LocalBase = localBase,
                                StackBase = valueStack.Count - tryInstr.ParameterCount,
                                ParameterCount = tryInstr.ParameterCount,
                                ResultCount = tryInstr.ResultCount,
                            }
                        );
                        break;
                    }
                    case WasmOpCodes.TryTable:
                    {
                        var tryTable = Unsafe.As<TryTableInstruction>(instr);
                        controlStack.Push(
                            new ControlFrame
                            {
                                Kind = ControlFrameKind.TryTable,
                                StartCodeOffset = ip,
                                EndCodeOffset = tryTable.EndIndex,
                                LocalBase = localBase,
                                StackBase = valueStack.Count - tryTable.ParameterCount,
                                ParameterCount = tryTable.ParameterCount,
                                ResultCount = tryTable.ResultCount,
                            }
                        );
                        break;
                    }
                    case WasmOpCodes.Catch:
                    {
                        var frame = controlStack.Pop();
                        localBase = frame.LocalBase;
                        ip = frame.EndCodeOffset + 1;
                        currentException = null;
                        break;
                    }
                    case WasmOpCodes.CatchAll:
                    {
                        var frame = controlStack.Pop();
                        localBase = frame.LocalBase;
                        ip = frame.EndCodeOffset + 1;
                        currentException = null;
                        break;
                    }
                    case WasmOpCodes.Throw:
                    {
                        var throwInstr = Unsafe.As<ThrowInstruction>(instr);
                        var tag = instance.GetTagInstance((int)throwInstr.TagIndex);

                        var parameters = tag.Type.Type.Parameters;
                        var extArgs = new WasmValue[parameters.Length];
                        valueStack.Take(extArgs.Length).CopyTo(extArgs);
                        var exception = new WasmThrownException(
                            instance.GetTagAddress((int)throwInstr.TagIndex),
                            extArgs
                        );

                        if (
                            !HandleWasmException(
                                instructions,
                                instance,
                                ref ip,
                                exception,
                                ref localBase,
                                controlBase
                            )
                        )
                        {
                            throw exception;
                        }
                        break;
                    }
                    case WasmOpCodes.Rethrow:
                    {
                        if (currentException is null)
                            WasmTrapException.Throw("No active WebAssembly exception.");
                        throw currentException;
                    }
                    case WasmOpCodes.ThrowRef:
                    {
                        var value = valueStack.UnsafePop();
                        var index = value.Bits;
                        if (index == 0 || index == (ulong)(long)FunctionAddress.Null)
                            return null;
                        if (index < 0 || index > (uint)exceptionRefs.Count)
                            WasmTrapException.Throw("invalid exception reference");

                        var exception = exceptionRefs[(int)index - 1];
                        if (exception is null)
                            WasmTrapException.Throw("null exception reference");

                        if (
                            !HandleWasmException(
                                instructions,
                                instance,
                                ref ip,
                                exception,
                                ref localBase,
                                controlBase
                            )
                        )
                            throw exception;
                        break;
                    }
                    case WasmOpCodes.Else:
                    {
                        var frame = controlStack.Pop();
                        localBase = frame.LocalBase;
                        ip = frame.EndCodeOffset + 1;
                        break;
                    }
                    case WasmOpCodes.End:
                    {
                        var frame = controlStack.Pop();
                        switch (frame.Kind)
                        {
                            case ControlFrameKind.Block:
                            case ControlFrameKind.If:
                            case ControlFrameKind.Loop:
                            case ControlFrameKind.Try:
                            case ControlFrameKind.TryTable:
                                localBase = frame.LocalBase;
                                break;
                        }
                        break;
                    }
                    case WasmOpCodes.Br:
                    {
                        var br = Unsafe.As<BrInstruction>(instr);
                        BranchToLabel((int)br.LabelIndex, ref localBase, ref ip);
                        break;
                    }
                    case WasmOpCodes.BrIf:
                    {
                        var brIf = Unsafe.As<BrIfInstruction>(instr);
                        var brCondition = valueStack.UnsafePop();
                        if (brCondition.I32 != 0)
                            BranchToLabel((int)brIf.LabelIndex, ref localBase, ref ip);
                        break;
                    }
                    case WasmOpCodes.BrTable:
                    {
                        var brTable = Unsafe.As<BrTableInstruction>(instr);
                        var selector = valueStack.UnsafePop().I32;
                        var labelIndex =
                            selector >= 0 && selector < brTable.LabelIndices.Length
                                ? (int)brTable.LabelIndices[selector]
                                : (int)brTable.DefaultLabelIndex;
                        BranchToLabel(labelIndex, ref localBase, ref ip);
                        break;
                    }
                    case WasmOpCodes.Return:
                    {
                        var target = controlStack.AsSpan()[controlBase];
                        PreserveBranchResults(target);
                        ExitFunctionControlFrames(controlBase);
                        return null;
                    }
                    case WasmOpCodes.Call:
                    {
                        var call = Unsafe.As<CallInstruction>(instr);
                        var callee = instance.GetFunction((int)call.FunctionIndex);
                        try
                        {
                            ExecuteFunction(instance, callee);
                        }
                        catch (WasmThrownException ex)
                        {
                            if (
                                !HandleWasmException(
                                    instructions,
                                    instance,
                                    ref ip,
                                    ex,
                                    ref localBase,
                                    controlBase
                                )
                            )
                                throw;
                        }
                        break;
                    }
                    case WasmOpCodes.CallIndirect:
                    {
                        var callIndirect = Unsafe.As<CallIndirectInstruction>(instr);
                        var callee = ResolveIndirectFunction(
                            instance,
                            callIndirect.TableIndex,
                            callIndirect.TypeIndex
                        );
                        try
                        {
                            ExecuteFunction(instance, callee);
                        }
                        catch (WasmThrownException ex)
                        {
                            if (
                                !HandleWasmException(
                                    instructions,
                                    instance,
                                    ref ip,
                                    ex,
                                    ref localBase,
                                    controlBase
                                )
                            )
                                throw;
                        }
                        break;
                    }
                    case WasmOpCodes.ReturnCall:
                    {
                        var retCall = Unsafe.As<ReturnCallInstruction>(instr);
                        ExitFunctionControlFrames(controlBase);
                        return instance.GetFunction((int)retCall.FunctionIndex);
                    }
                    case WasmOpCodes.ReturnCallIndirect:
                    {
                        var retCallIndirect = Unsafe.As<ReturnCallIndirectInstruction>(instr);
                        ExitFunctionControlFrames(controlBase);
                        return ResolveIndirectFunction(
                            instance,
                            retCallIndirect.TableIndex,
                            retCallIndirect.TypeIndex
                        );
                    }
                    case WasmOpCodes.CallRef:
                    {
                        var callRef = Unsafe.As<CallRefInstruction>(instr);
                        var callee = ResolveFunctionReference(instance, callRef.TypeIndex);
                        try
                        {
                            ExecuteFunction(instance, callee);
                        }
                        catch (WasmThrownException ex)
                        {
                            if (
                                !HandleWasmException(
                                    instructions,
                                    instance,
                                    ref ip,
                                    ex,
                                    ref localBase,
                                    controlBase
                                )
                            )
                                throw;
                        }
                        break;
                    }
                    case WasmOpCodes.ReturnCallRef:
                    {
                        var retCallRef = Unsafe.As<ReturnCallRefInstruction>(instr);
                        ExitFunctionControlFrames(controlBase);
                        return ResolveFunctionReference(instance, retCallRef.TypeIndex);
                    }
                    case WasmOpCodes.Drop:
                        valueStack.UnsafePop();
                        break;
                    case WasmOpCodes.Select:
                    {
                        var selCondition = valueStack.UnsafePop();
                        var selValue2 = valueStack.UnsafePop();
                        var selValue1 = valueStack.UnsafePop();
                        valueStack.Push(selCondition.I32 != 0 ? selValue1 : selValue2);
                        break;
                    }
                    case WasmOpCodes.SelectT:
                    {
                        var selCondition = valueStack.UnsafePop();
                        var selValue2 = valueStack.UnsafePop();
                        var selValue1 = valueStack.UnsafePop();
                        valueStack.Push(selCondition.I32 != 0 ? selValue1 : selValue2);
                        break;
                    }
                    case WasmOpCodes.LocalGet:
                    {
                        var localGet = Unsafe.As<LocalGetInstruction>(instr);
                        valueStack.Push(
                            Unsafe.Add(
                                ref MemoryMarshal.GetReference(locals),
                                localBase + (int)localGet.LocalIndex
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.LocalSet:
                    {
                        var localSet = Unsafe.As<LocalSetInstruction>(instr);
                        Unsafe.Add(
                            ref MemoryMarshal.GetReference(locals),
                            localBase + (int)localSet.LocalIndex
                        ) = valueStack.UnsafePop();
                        break;
                    }
                    case WasmOpCodes.LocalTee:
                    {
                        var localTee = Unsafe.As<LocalTeeInstruction>(instr);
                        var teeValue = valueStack.UnsafePop();
                        Unsafe.Add(
                            ref MemoryMarshal.GetReference(locals),
                            localBase + (int)localTee.LocalIndex
                        ) = teeValue;
                        valueStack.Push(teeValue);
                        break;
                    }
                    case WasmOpCodes.GlobalGet:
                    {
                        var globalGet = Unsafe.As<GlobalGetInstruction>(instr);
                        valueStack.Push(
                            instance.GetGlobalInstance((int)globalGet.GlobalIndex).Value
                        );
                        break;
                    }
                    case WasmOpCodes.GlobalSet:
                    {
                        var globalSet = Unsafe.As<GlobalSetInstruction>(instr);
                        instance.GetGlobalInstance((int)globalSet.GlobalIndex).Value =
                            valueStack.UnsafePop();
                        break;
                    }
                    case WasmOpCodes.TableGet:
                    {
                        var tableGet = Unsafe.As<TableGetInstruction>(instr);
                        var table = instance.GetTableInstance((int)tableGet.TableIndex);
                        var index = PopTableIndex(table);
                        if (index < 0 || table.References.Length <= index)
                            WasmTrapException.Throw($"Invalid table index: {index}");
                        valueStack.Push(table.References[index]);
                        break;
                    }
                    case WasmOpCodes.TableSet:
                    {
                        var tableSet = Unsafe.As<TableSetInstruction>(instr);
                        var table = instance.GetTableInstance((int)tableSet.TableIndex);
                        var reference = valueStack.UnsafePop();
                        var index = PopTableIndex(table);
                        if (index < 0 || table.References.Length <= index)
                            WasmTrapException.Throw($"Invalid table index: {index}");
                        table.References[index] = reference;
                        break;
                    }
                    case WasmOpCodes.I32Load:
                    {
                        var mem = Unsafe.As<I32LoadInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI32(
                                Unsafe.ReadUnaligned<int>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I64Load:
                    {
                        var mem = Unsafe.As<I64LoadInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            8,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI64(BitConverter.ToInt64(memory.Data.Slice(address, 8)))
                        );
                        break;
                    }
                    case WasmOpCodes.F32Load:
                    {
                        var mem = Unsafe.As<F32LoadInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromF32(
                                Unsafe.ReadUnaligned<float>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.F64Load:
                    {
                        var mem = Unsafe.As<F64LoadInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            8,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromF64(
                                Unsafe.ReadUnaligned<double>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I32Load8S:
                    {
                        var mem = Unsafe.As<I32Load8SInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var data = memory.Data;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            1,
                            data.Length
                        );
                        valueStack.Push(WasmValue.FromI32((sbyte)data[address]));
                        break;
                    }
                    case WasmOpCodes.I32Load8U:
                    {
                        var mem = Unsafe.As<I32Load8UInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var data = memory.Data;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            1,
                            data.Length
                        );
                        valueStack.Push(WasmValue.FromI32(data[address]));
                        break;
                    }
                    case WasmOpCodes.I32Load16S:
                    {
                        var mem = Unsafe.As<I32Load16SInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            2,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI32(
                                Unsafe.ReadUnaligned<short>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I32Load16U:
                    {
                        var mem = Unsafe.As<I32Load16UInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            2,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI32(
                                Unsafe.ReadUnaligned<ushort>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I64Load8S:
                    {
                        var mem = Unsafe.As<I64Load8SInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var data = memory.Data;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            1,
                            data.Length
                        );
                        valueStack.Push(WasmValue.FromI64((sbyte)data[address]));
                        break;
                    }
                    case WasmOpCodes.I64Load8U:
                    {
                        var mem = Unsafe.As<I64Load8UInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var data = memory.Data;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            1,
                            data.Length
                        );
                        valueStack.Push(WasmValue.FromI64(data[address]));
                        break;
                    }
                    case WasmOpCodes.I64Load16S:
                    {
                        var mem = Unsafe.As<I64Load16SInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            2,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI64(
                                Unsafe.ReadUnaligned<short>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I64Load16U:
                    {
                        var mem = Unsafe.As<I64Load16UInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            2,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI64(
                                Unsafe.ReadUnaligned<ushort>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I64Load32S:
                    {
                        var mem = Unsafe.As<I64Load32SInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI64(
                                Unsafe.ReadUnaligned<int>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I64Load32U:
                    {
                        var mem = Unsafe.As<I64Load32UInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        valueStack.Push(
                            WasmValue.FromI64(
                                Unsafe.ReadUnaligned<uint>(
                                    ref MemoryMarshal.GetReference(memory.Data[address..])
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I32Store:
                    {
                        var mem = Unsafe.As<I32StoreInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = valueStack.UnsafePop().I32;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.I64Store:
                    {
                        var mem = Unsafe.As<I64StoreInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = valueStack.UnsafePop().I64;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            8,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.F32Store:
                    {
                        var mem = Unsafe.As<F32StoreInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = valueStack.UnsafePop().F32;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.F64Store:
                    {
                        var mem = Unsafe.As<F64StoreInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = valueStack.UnsafePop().F64;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            8,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.I32Store8:
                    {
                        var mem = Unsafe.As<I32Store8Instruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var data = memory.Data;
                        var storeValue = (byte)valueStack.UnsafePop().I32;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            1,
                            data.Length
                        );
                        data[address] = storeValue;
                        break;
                    }
                    case WasmOpCodes.I32Store16:
                    {
                        var mem = Unsafe.As<I32Store16Instruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = (ushort)valueStack.UnsafePop().I32;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            2,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.I64Store8:
                    {
                        var mem = Unsafe.As<I64Store8Instruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var data = memory.Data;
                        var storeValue = (byte)valueStack.UnsafePop().I64;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            1,
                            data.Length
                        );
                        data[address] = storeValue;
                        break;
                    }
                    case WasmOpCodes.I64Store16:
                    {
                        var mem = Unsafe.As<I64Store16Instruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = (ushort)valueStack.UnsafePop().I64;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            2,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.I64Store32:
                    {
                        var mem = Unsafe.As<I64Store32Instruction>(instr);
                        var memory = instance.GetMemoryInstance((int)mem.MemoryIndex);
                        var storeValue = (uint)valueStack.UnsafePop().I64;
                        var address = CalcMemoryAddress(
                            PopMemoryAddress(memory),
                            mem.Offset,
                            4,
                            memory.Data.Length
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(memory.Data[address..]),
                            storeValue
                        );
                        break;
                    }
                    case WasmOpCodes.MemorySize:
                    {
                        var memSize = Unsafe.As<MemorySizeInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)memSize.MemoryIndex);
                        var pages = memory.Data.Length / 65536;
                        if (memory.AddressType == AddressType.I64)
                            valueStack.Push(WasmValue.FromI64(pages));
                        else
                            valueStack.Push(WasmValue.FromI32(pages));
                        break;
                    }
                    case WasmOpCodes.MemoryGrow:
                    {
                        var memGrow = Unsafe.As<MemoryGrowInstruction>(instr);
                        var memory = instance.GetMemoryInstance((int)memGrow.MemoryIndex);
                        var deltaPages = PopPageCount(memory);
                        var oldPages = memory.Data.Length / WasmStore.PageSize;
                        var maxPages = memory.Max ?? 65536u;
                        if (deltaPages < 0 || (ulong)(uint)oldPages + (uint)deltaPages > maxPages)
                        {
                            if (memory.AddressType == AddressType.I64)
                                valueStack.Push(WasmValue.FromI64(-1));
                            else
                                valueStack.Push(WasmValue.FromI32(-1));
                        }
                        else
                        {
                            try
                            {
                                memory.Grow(deltaPages);
                                if (memory.AddressType == AddressType.I64)
                                    valueStack.Push(WasmValue.FromI64(oldPages));
                                else
                                    valueStack.Push(WasmValue.FromI32(oldPages));
                            }
                            catch (Exception ex)
                                when (ex
                                        is InvalidOperationException
                                            or OverflowException
                                            or OutOfMemoryException
                                )
                            {
                                if (memory.AddressType == AddressType.I64)
                                    valueStack.Push(WasmValue.FromI64(-1));
                                else
                                    valueStack.Push(WasmValue.FromI32(-1));
                            }
                        }
                        break;
                    }
                    case WasmOpCodes.I32Const:
                    {
                        var i32 = Unsafe.As<I32ConstInstruction>(instr);
                        valueStack.Push(WasmValue.FromI32(i32.Value));
                        break;
                    }
                    case WasmOpCodes.I64Const:
                    {
                        var i64 = Unsafe.As<I64ConstInstruction>(instr);
                        valueStack.Push(WasmValue.FromI64(i64.Value));
                        break;
                    }
                    case WasmOpCodes.F32Const:
                    {
                        var f32 = Unsafe.As<F32ConstInstruction>(instr);
                        valueStack.Push(WasmValue.FromF32(f32.Value));
                        break;
                    }
                    case WasmOpCodes.F64Const:
                    {
                        var f64 = Unsafe.As<F64ConstInstruction>(instr);
                        valueStack.Push(WasmValue.FromF64(f64.Value));
                        break;
                    }
                    case WasmOpCodes.I32Eqz:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(val == 0 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32Eq:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 == v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32Ne:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 != v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32LtS:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 < v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32LtU:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((uint)v1 < (uint)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32GtS:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 > v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32GtU:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((uint)v1 > (uint)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32LeS:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 <= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32LeU:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((uint)v1 <= (uint)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32GeS:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 >= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32GeU:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((uint)v1 >= (uint)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64Eqz:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(val == 0 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64Eq:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(v1 == v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64Ne:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(v1 != v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64LtS:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(v1 < v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64LtU:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32((ulong)v1 < (ulong)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64GtS:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(v1 > v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64GtU:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32((ulong)v1 > (ulong)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64LeS:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(v1 <= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64LeU:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32((ulong)v1 <= (ulong)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64GeS:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32(v1 >= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I64GeU:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32((ulong)v1 >= (ulong)v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F32Eq:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(v1 == v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F32Ne:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(v1 != v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F32Lt:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(v1 < v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F32Gt:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(v1 > v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F32Le:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(v1 <= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F32Ge:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(v1 >= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F64Eq:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI32(v1 == v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F64Ne:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI32(v1 != v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F64Lt:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI32(v1 < v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F64Gt:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI32(v1 > v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F64Le:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI32(v1 <= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.F64Ge:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI32(v1 >= v2 ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.I32Clz:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(
                            WasmValue.FromI32(BitOperations.LeadingZeroCount((uint)val))
                        );
                        break;
                    }
                    case WasmOpCodes.I32Ctz:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(
                            WasmValue.FromI32(BitOperations.TrailingZeroCount((uint)val))
                        );
                        break;
                    }
                    case WasmOpCodes.I32Popcnt:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(BitOperations.PopCount((uint)val)));
                        break;
                    }
                    case WasmOpCodes.I32Add:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 + v2));
                        break;
                    }
                    case WasmOpCodes.I32Sub:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 - v2));
                        break;
                    }
                    case WasmOpCodes.I32Mul:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 * v2));
                        break;
                    }
                    case WasmOpCodes.I32DivS:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        if (v1 == int.MinValue && v2 == -1)
                            WasmTrapException.Throw("Integer overflow");
                        valueStack.Push(WasmValue.FromI32(v1 / v2));
                        break;
                    }
                    case WasmOpCodes.I32DivU:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        valueStack.Push(WasmValue.FromI32((int)((uint)v1 / (uint)v2)));
                        break;
                    }
                    case WasmOpCodes.I32RemS:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        if (v1 == int.MinValue && v2 == -1)
                            valueStack.Push(WasmValue.FromI32(0));
                        else
                            valueStack.Push(WasmValue.FromI32(v1 % v2));
                        break;
                    }
                    case WasmOpCodes.I32RemU:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        valueStack.Push(WasmValue.FromI32((int)((uint)v1 % (uint)v2)));
                        break;
                    }
                    case WasmOpCodes.I32And:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 & v2));
                        break;
                    }
                    case WasmOpCodes.I32Or:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 | v2));
                        break;
                    }
                    case WasmOpCodes.I32Xor:
                    {
                        var v2 = valueStack.UnsafePop().I32;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 ^ v2));
                        break;
                    }
                    case WasmOpCodes.I32Shl:
                    {
                        var v2 = valueStack.UnsafePop().I32 & 0x1F;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 << v2));
                        break;
                    }
                    case WasmOpCodes.I32ShrS:
                    {
                        var v2 = valueStack.UnsafePop().I32 & 0x1F;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32(v1 >> v2));
                        break;
                    }
                    case WasmOpCodes.I32ShrU:
                    {
                        var v2 = valueStack.UnsafePop().I32 & 0x1F;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((int)((uint)v1 >> v2)));
                        break;
                    }
                    case WasmOpCodes.I32Rotl:
                    {
                        var v2 = valueStack.UnsafePop().I32 & 0x1F;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(
                            WasmValue.FromI32((v1 << v2) | ((int)((uint)v1 >> (32 - v2))))
                        );
                        break;
                    }
                    case WasmOpCodes.I32Rotr:
                    {
                        var v2 = valueStack.UnsafePop().I32 & 0x1F;
                        var v1 = valueStack.UnsafePop().I32;
                        valueStack.Push(
                            WasmValue.FromI32(((int)((uint)v1 >> v2)) | (v1 << (32 - v2)))
                        );
                        break;
                    }
                    case WasmOpCodes.I64Clz:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(
                            WasmValue.FromI64(BitOperations.LeadingZeroCount((ulong)val))
                        );
                        break;
                    }
                    case WasmOpCodes.I64Ctz:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(
                            WasmValue.FromI64(BitOperations.TrailingZeroCount((ulong)val))
                        );
                        break;
                    }
                    case WasmOpCodes.I64Popcnt:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(BitOperations.PopCount((ulong)val)));
                        break;
                    }
                    case WasmOpCodes.I64Add:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 + v2));
                        break;
                    }
                    case WasmOpCodes.I64Sub:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 - v2));
                        break;
                    }
                    case WasmOpCodes.I64Mul:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 * v2));
                        break;
                    }
                    case WasmOpCodes.I64DivS:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        if (v1 == long.MinValue && v2 == -1)
                            WasmTrapException.Throw("Integer overflow");
                        valueStack.Push(WasmValue.FromI64(v1 / v2));
                        break;
                    }
                    case WasmOpCodes.I64DivU:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        valueStack.Push(WasmValue.FromI64((long)((ulong)v1 / (ulong)v2)));
                        break;
                    }
                    case WasmOpCodes.I64RemS:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        if (v1 == long.MinValue && v2 == -1)
                            valueStack.Push(WasmValue.FromI64(0));
                        else
                            valueStack.Push(WasmValue.FromI64(v1 % v2));
                        break;
                    }
                    case WasmOpCodes.I64RemU:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        if (v2 == 0)
                            WasmTrapException.Throw("Division by zero");
                        valueStack.Push(WasmValue.FromI64((long)((ulong)v1 % (ulong)v2)));
                        break;
                    }
                    case WasmOpCodes.I64And:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 & v2));
                        break;
                    }
                    case WasmOpCodes.I64Or:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 | v2));
                        break;
                    }
                    case WasmOpCodes.I64Xor:
                    {
                        var v2 = valueStack.UnsafePop().I64;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 ^ v2));
                        break;
                    }
                    case WasmOpCodes.I64Shl:
                    {
                        var v2 = valueStack.UnsafePop().I64 & 0x3F;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 << (int)v2));
                        break;
                    }
                    case WasmOpCodes.I64ShrS:
                    {
                        var v2 = valueStack.UnsafePop().I64 & 0x3F;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64(v1 >> (int)v2));
                        break;
                    }
                    case WasmOpCodes.I64ShrU:
                    {
                        var v2 = valueStack.UnsafePop().I64 & 0x3F;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64((long)((ulong)v1 >> (int)v2)));
                        break;
                    }
                    case WasmOpCodes.I64Rotl:
                    {
                        var v2 = valueStack.UnsafePop().I64 & 0x3F;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(
                            WasmValue.FromI64(
                                (v1 << (int)v2) | ((long)((ulong)v1 >> (64 - (int)v2)))
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.I64Rotr:
                    {
                        var v2 = valueStack.UnsafePop().I64 & 0x3F;
                        var v1 = valueStack.UnsafePop().I64;
                        valueStack.Push(
                            WasmValue.FromI64(
                                ((long)((ulong)v1 >> (int)v2)) | (v1 << (64 - (int)v2))
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.F32Abs:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Abs(val)));
                        break;
                    }
                    case WasmOpCodes.F32Neg:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(-val));
                        break;
                    }
                    case WasmOpCodes.F32Ceil:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Ceiling(val)));
                        break;
                    }
                    case WasmOpCodes.F32Floor:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Floor(val)));
                        break;
                    }
                    case WasmOpCodes.F32Trunc:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Truncate(val)));
                        break;
                    }
                    case WasmOpCodes.F32Nearest:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Round(val)));
                        break;
                    }
                    case WasmOpCodes.F32Sqrt:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Sqrt(val)));
                        break;
                    }
                    case WasmOpCodes.F32Add:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(v1 + v2));
                        break;
                    }
                    case WasmOpCodes.F32Sub:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(v1 - v2));
                        break;
                    }
                    case WasmOpCodes.F32Mul:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(v1 * v2));
                        break;
                    }
                    case WasmOpCodes.F32Div:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(v1 / v2));
                        break;
                    }
                    case WasmOpCodes.F32Min:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Min(v1, v2)));
                        break;
                    }
                    case WasmOpCodes.F32Max:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.Max(v1, v2)));
                        break;
                    }
                    case WasmOpCodes.F32Copysign:
                    {
                        var v2 = valueStack.UnsafePop().F32;
                        var v1 = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF32(MathF.CopySign(v1, v2)));
                        break;
                    }
                    case WasmOpCodes.F64Abs:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Abs(val)));
                        break;
                    }
                    case WasmOpCodes.F64Neg:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(-val));
                        break;
                    }
                    case WasmOpCodes.F64Ceil:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Ceiling(val)));
                        break;
                    }
                    case WasmOpCodes.F64Floor:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Floor(val)));
                        break;
                    }
                    case WasmOpCodes.F64Trunc:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Truncate(val)));
                        break;
                    }
                    case WasmOpCodes.F64Nearest:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Round(val)));
                        break;
                    }
                    case WasmOpCodes.F64Sqrt:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Sqrt(val)));
                        break;
                    }
                    case WasmOpCodes.F64Add:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(v1 + v2));
                        break;
                    }
                    case WasmOpCodes.F64Sub:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(v1 - v2));
                        break;
                    }
                    case WasmOpCodes.F64Mul:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(v1 * v2));
                        break;
                    }
                    case WasmOpCodes.F64Div:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(v1 / v2));
                        break;
                    }
                    case WasmOpCodes.F64Min:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Min(v1, v2)));
                        break;
                    }
                    case WasmOpCodes.F64Max:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.Max(v1, v2)));
                        break;
                    }
                    case WasmOpCodes.F64Copysign:
                    {
                        var v2 = valueStack.UnsafePop().F64;
                        var v1 = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF64(Math.CopySign(v1, v2)));
                        break;
                    }
                    case WasmOpCodes.I32WrapI64:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI32((int)val));
                        break;
                    }
                    case WasmOpCodes.I32TruncF32S:
                    {
                        var val = valueStack.UnsafePop().F32;
                        TruncHelper.ThrowIfInvalidTrunc(val, -2147483649.0, 2147483648.0);
                        valueStack.Push(WasmValue.FromI32((int)val));
                        break;
                    }
                    case WasmOpCodes.I32TruncF32U:
                    {
                        var val = valueStack.UnsafePop().F32;
                        TruncHelper.ThrowIfInvalidTrunc(val, -1.0, 4294967296.0);
                        valueStack.Push(WasmValue.FromI32((int)(uint)val));
                        break;
                    }
                    case WasmOpCodes.I32TruncF64S:
                    {
                        var val = valueStack.UnsafePop().F64;
                        TruncHelper.ThrowIfInvalidTrunc(val, -2147483649.0, 2147483648.0);
                        valueStack.Push(WasmValue.FromI32((int)val));
                        break;
                    }
                    case WasmOpCodes.I32TruncF64U:
                    {
                        var val = valueStack.UnsafePop().F64;
                        TruncHelper.ThrowIfInvalidTrunc(val, -1.0, 4294967296.0);
                        valueStack.Push(WasmValue.FromI32((int)(uint)val));
                        break;
                    }
                    case WasmOpCodes.I64ExtendI32S:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI64((long)val));
                        break;
                    }
                    case WasmOpCodes.I64ExtendI32U:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI64((long)(uint)val));
                        break;
                    }
                    case WasmOpCodes.I64TruncF32S:
                    {
                        var val = valueStack.UnsafePop().F32;
                        TruncHelper.ThrowIfInvalidTrunc(
                            val,
                            -9223372036854777856.0,
                            9223372036854775808.0
                        );
                        valueStack.Push(WasmValue.FromI64((long)val));
                        break;
                    }
                    case WasmOpCodes.I64TruncF32U:
                    {
                        var val = valueStack.UnsafePop().F32;
                        TruncHelper.ThrowIfInvalidTrunc(val, -1.0, 18446744073709551616.0);
                        valueStack.Push(WasmValue.FromI64((long)(ulong)val));
                        break;
                    }
                    case WasmOpCodes.I64TruncF64S:
                    {
                        var val = valueStack.UnsafePop().F64;
                        TruncHelper.ThrowIfInvalidTrunc(
                            val,
                            -9223372036854777856.0,
                            9223372036854775808.0
                        );
                        valueStack.Push(WasmValue.FromI64((long)val));
                        break;
                    }
                    case WasmOpCodes.I64TruncF64U:
                    {
                        var val = valueStack.UnsafePop().F64;
                        TruncHelper.ThrowIfInvalidTrunc(val, -1.0, 18446744073709551616.0);
                        valueStack.Push(WasmValue.FromI64((long)(ulong)val));
                        break;
                    }
                    case WasmOpCodes.F32ConvertI32S:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromF32(val));
                        break;
                    }
                    case WasmOpCodes.F32ConvertI32U:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromF32((uint)val));
                        break;
                    }
                    case WasmOpCodes.F32ConvertI64S:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromF32(val));
                        break;
                    }
                    case WasmOpCodes.F32ConvertI64U:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromF32((ulong)val));
                        break;
                    }
                    case WasmOpCodes.F32DemoteF64:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromF32((float)val));
                        break;
                    }
                    case WasmOpCodes.F64ConvertI32S:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromF64(val));
                        break;
                    }
                    case WasmOpCodes.F64ConvertI32U:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromF64((uint)val));
                        break;
                    }
                    case WasmOpCodes.F64ConvertI64S:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromF64(val));
                        break;
                    }
                    case WasmOpCodes.F64ConvertI64U:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromF64((ulong)val));
                        break;
                    }
                    case WasmOpCodes.F64PromoteF32:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromF64(val));
                        break;
                    }
                    case WasmOpCodes.I32ReinterpretF32:
                    {
                        var val = valueStack.UnsafePop().F32;
                        valueStack.Push(WasmValue.FromI32(BitConverter.SingleToInt32Bits(val)));
                        break;
                    }
                    case WasmOpCodes.I64ReinterpretF64:
                    {
                        var val = valueStack.UnsafePop().F64;
                        valueStack.Push(WasmValue.FromI64(BitConverter.DoubleToInt64Bits(val)));
                        break;
                    }
                    case WasmOpCodes.F32ReinterpretI32:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromF32(BitConverter.Int32BitsToSingle(val)));
                        break;
                    }
                    case WasmOpCodes.F64ReinterpretI64:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromF64(BitConverter.Int64BitsToDouble(val)));
                        break;
                    }
                    case WasmOpCodes.I32Extend8S:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((sbyte)val));
                        break;
                    }
                    case WasmOpCodes.I32Extend16S:
                    {
                        var val = valueStack.UnsafePop().I32;
                        valueStack.Push(WasmValue.FromI32((short)val));
                        break;
                    }
                    case WasmOpCodes.I64Extend8S:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64((sbyte)val));
                        break;
                    }
                    case WasmOpCodes.I64Extend16S:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64((short)val));
                        break;
                    }
                    case WasmOpCodes.I64Extend32S:
                    {
                        var val = valueStack.UnsafePop().I64;
                        valueStack.Push(WasmValue.FromI64((int)val));
                        break;
                    }
                    case WasmOpCodes.RefNull:
                    {
                        valueStack.Push(WasmValue.FromRaw((ulong)(long)FunctionAddress.Null));
                        break;
                    }
                    case WasmOpCodes.RefIsNull:
                    {
                        var val = valueStack.UnsafePop();
                        valueStack.Push(WasmValue.FromI32(val.IsNullReference ? 1 : 0));
                        break;
                    }
                    case WasmOpCodes.RefAsNonNull:
                    {
                        var val = valueStack.UnsafePop();
                        if (val.IsNullReference)
                            WasmTrapException.Throw("null reference");
                        valueStack.Push(val);
                        break;
                    }
                    case WasmOpCodes.RefEq:
                    {
                        var v2 = valueStack.UnsafePop();
                        var v1 = valueStack.UnsafePop();
                        valueStack.Push(
                            WasmValue.FromI32(WasmValue.ReferenceEquals(v1, v2) ? 1 : 0)
                        );
                        break;
                    }
                    case WasmOpCodes.RefFunc:
                    {
                        var refFunc = Unsafe.As<RefFuncInstruction>(instr);
                        var functionAddress = instance.GetFunctionAddress(
                            (int)refFunc.FunctionIndex
                        );
                        valueStack.Push(WasmValue.FromRaw((ulong)(long)functionAddress));
                        break;
                    }
                    case WasmOpCodes.BrOnNull:
                    {
                        var brOnNull = Unsafe.As<BrOnNullInstruction>(instr);
                        var brVal = valueStack.UnsafePop();
                        if (brVal.IsNullReference)
                            BranchToLabel((int)brOnNull.LabelIndex, ref localBase, ref ip);
                        else
                            valueStack.Push(brVal);
                        break;
                    }
                    case WasmOpCodes.BrOnNonNull:
                    {
                        var brOnNonNull = Unsafe.As<BrOnNonNullInstruction>(instr);
                        var brVal = valueStack.UnsafePop();
                        if (!brVal.IsNullReference)
                        {
                            valueStack.Push(brVal);
                            BranchToLabel((int)brOnNonNull.LabelIndex, ref localBase, ref ip);
                        }
                        break;
                    }
                    case WasmOpCodes.GCExtension:
                        ExecuteGCInstruction(instance, instr, ref localBase, ref ip);
                        break;
                    case WasmOpCodes.FCExtension:
                        ExecuteFCExtensionInstruction(instance, instr);
                        break;
                    case WasmOpCodes.SIMDExtension:
                        ExecuteSimdInstruction(instance, instr, ref localBase, ref ip);
                        break;
                    default:
                        throw new NotImplementedException(
                            $"Unsupported opcode: 0x{instr.OpCode:X2}"
                        );
                }
            }

            return null;
        }
        finally
        {
            locals.Clear();
            localStack.Truncate(localStackBase);
        }
    }

    void ExecuteFCExtensionInstruction(WasmInstance instance, Instruction instr)
    {
        var extCode = Unsafe.As<FCExtensionInstruction>(instr).ExtensionCode;
        switch (extCode)
        {
            case WasmOpCodes.I32TruncSatF32S:
            {
                var val = valueStack.UnsafePop().F32;
                valueStack.Push(WasmValue.FromI32(TruncHelper.TruncSatI32S(val)));
                break;
            }
            case WasmOpCodes.I32TruncSatF32U:
            {
                var val = valueStack.UnsafePop().F32;
                valueStack.Push(
                    WasmValue.FromI32(unchecked((int)TruncHelper.TruncSatI32U(val)))
                );
                break;
            }
            case WasmOpCodes.I32TruncSatF64S:
            {
                var val = valueStack.UnsafePop().F64;
                valueStack.Push(WasmValue.FromI32(TruncHelper.TruncSatI32S(val)));
                break;
            }
            case WasmOpCodes.I32TruncSatF64U:
            {
                var val = valueStack.UnsafePop().F64;
                valueStack.Push(
                    WasmValue.FromI32(unchecked((int)TruncHelper.TruncSatI32U(val)))
                );
                break;
            }
            case WasmOpCodes.I64TruncSatF32S:
            {
                var val = valueStack.UnsafePop().F32;
                valueStack.Push(WasmValue.FromI64(TruncHelper.TruncSatI64S(val)));
                break;
            }
            case WasmOpCodes.I64TruncSatF32U:
            {
                var val = valueStack.UnsafePop().F32;
                valueStack.Push(
                    WasmValue.FromI64(
                        unchecked((long)TruncHelper.TruncSatI64U(val))
                    )
                );
                break;
            }
            case WasmOpCodes.I64TruncSatF64S:
            {
                var val = valueStack.UnsafePop().F64;
                valueStack.Push(WasmValue.FromI64(TruncHelper.TruncSatI64S(val)));
                break;
            }
            case WasmOpCodes.I64TruncSatF64U:
            {
                var val = valueStack.UnsafePop().F64;
                valueStack.Push(
                    WasmValue.FromI64(
                        unchecked((long)TruncHelper.TruncSatI64U(val))
                    )
                );
                break;
            }
            case WasmOpCodes.MemoryInit:
            {
                var memInit = Unsafe.As<MemoryInitInstruction>(instr);
                var length = valueStack.UnsafePop().I32;
                var srcOffset = valueStack.UnsafePop().I32;
                var memMemory = instance.GetMemoryInstance(
                    (int)memInit.MemoryIndex
                );
                var destOffset = PopMemoryAddress(memMemory);
                var dataSegment = instance.Module.Data[(int)memInit.DataIndex];
                var isDropped = instance.IsDataSegmentDropped(
                    (int)memInit.DataIndex
                );
                if (!isDropped)
                    WasmTrapException.ThrowIfNot(
                        dataSegment.Mode == DataSegmentMode.Passive,
                        "Data segment must be passive"
                    );
                var memoryAddress = CalcMemoryAddress(
                    destOffset,
                    0,
                    length,
                    memMemory.Data.Length
                );
                var dataLength = isDropped ? 0 : dataSegment.Data.Length;
                WasmTrapException.ThrowIfNot(
                    srcOffset >= 0
                        && length >= 0
                        && (ulong)(uint)srcOffset + (uint)length
                            <= (uint)dataLength,
                    "Data segment access out of bounds"
                );
                dataSegment
                    .Data.Span.Slice(srcOffset, length)
                    .CopyTo(memMemory.Data.Slice(memoryAddress, length));
                break;
            }
            case WasmOpCodes.DataDrop:
            {
                var dataDrop = Unsafe.As<DataDropInstruction>(instr);
                instance.DropDataSegment((int)dataDrop.DataIndex);
                break;
            }
            case WasmOpCodes.MemoryCopy:
            {
                var memCopy = Unsafe.As<MemoryCopyInstruction>(instr);
                var destinationMemory = instance.GetMemoryInstance(
                    (int)memCopy.DestinationMemoryIndex
                );
                var sourceMemory = instance.GetMemoryInstance(
                    (int)memCopy.SourceMemoryIndex
                );
                var length =
                    destinationMemory.AddressType != sourceMemory.AddressType
                        ? valueStack.UnsafePop().I32
                        : PopPageCount(sourceMemory);
                var sourceOffset = PopMemoryAddress(sourceMemory);
                var destinationOffset = PopMemoryAddress(destinationMemory);
                var destinationAddress = CalcMemoryAddress(
                    destinationOffset,
                    0,
                    length,
                    destinationMemory.Data.Length
                );
                var sourceAddress = CalcMemoryAddress(
                    sourceOffset,
                    0,
                    length,
                    sourceMemory.Data.Length
                );
                sourceMemory
                    .Data.Slice(sourceAddress, length)
                    .CopyTo(
                        destinationMemory.Data.Slice(destinationAddress, length)
                    );
                break;
            }
            case WasmOpCodes.MemoryFill:
            {
                var memFill = Unsafe.As<MemoryFillInstruction>(instr);
                var memMemory = instance.GetMemoryInstance(
                    (int)memFill.MemoryIndex
                );
                var length = PopPageCount(memMemory);
                var fillValue = valueStack.UnsafePop().I32;
                var destinationOffset = PopMemoryAddress(memMemory);
                var destinationAddress = CalcMemoryAddress(
                    destinationOffset,
                    0,
                    length,
                    memMemory.Data.Length
                );
                memMemory
                    .Data.Slice(destinationAddress, length)
                    .Fill((byte)fillValue);
                break;
            }
            case WasmOpCodes.TableInit:
            {
                var tableInit = Unsafe.As<TableInitInstruction>(instr);
                var table = instance.GetTableInstance((int)tableInit.TableIndex);
                var length = valueStack.UnsafePop().I32;
                var sourceOffset = valueStack.UnsafePop().I32;
                var destinationOffset = PopTableIndex(table);
                var element = instance.Module.Elements[(int)tableInit.ElementIndex];
                var isDropped = instance.IsElementSegmentDropped(
                    (int)tableInit.ElementIndex
                );
                if (!isDropped)
                    WasmTrapException.ThrowIfNot(
                        element.Mode == ElementMode.Passive,
                        "Element segment must be passive"
                    );
                var tableAddress = CalcTableAddress(
                    destinationOffset,
                    length,
                    table.References.Length
                );
                var elementValues = instance.GetElementSegment(
                    (int)tableInit.ElementIndex
                );
                var elementLength = isDropped ? 0 : elementValues.Length;
                WasmTrapException.ThrowIfNot(
                    sourceOffset >= 0
                        && length >= 0
                        && (ulong)(uint)sourceOffset + (uint)length
                            <= (uint)elementLength,
                    "Element segment access out of bounds"
                );
                elementValues
                    .Slice(sourceOffset, length)
                    .CopyTo(table.References.Slice(tableAddress, length));
                break;
            }
            case WasmOpCodes.ElemDrop:
            {
                var elemDrop = Unsafe.As<ElemDropInstruction>(instr);
                instance.DropElementSegment((int)elemDrop.ElementIndex);
                break;
            }
            case WasmOpCodes.TableCopy:
            {
                var tableCopy = Unsafe.As<TableCopyInstruction>(instr);
                var destinationTable = instance.GetTableInstance(
                    (int)tableCopy.DestinationTableIndex
                );
                var sourceTable = instance.GetTableInstance(
                    (int)tableCopy.SourceTableIndex
                );
                var length =
                    destinationTable.AddressType != sourceTable.AddressType
                        ? valueStack.UnsafePop().I32
                        : PopTableIndex(sourceTable);
                var sourceOffset = PopTableIndex(sourceTable);
                var destinationOffset = PopTableIndex(destinationTable);
                var dstAddr = CalcTableAddress(
                    destinationOffset,
                    length,
                    destinationTable.References.Length
                );
                var srcAddr = CalcTableAddress(
                    sourceOffset,
                    length,
                    sourceTable.References.Length
                );
                sourceTable
                    .References.Slice(srcAddr, length)
                    .CopyTo(destinationTable.References.Slice(dstAddr, length));
                break;
            }
            case WasmOpCodes.TableGrow:
            {
                var tableGrow = Unsafe.As<TableGrowInstruction>(instr);
                var table = instance.GetTableInstance((int)tableGrow.TableIndex);
                var delta = PopTableIndex(table);
                var growValue = valueStack.UnsafePop();
                var oldSize = table.References.Length;
                ValidateTableReference(instance, table, growValue);
                if (
                    delta < 0
                    || (
                        table.Max.HasValue
                        && (ulong)(uint)oldSize + (uint)delta > table.Max.Value
                    )
                )
                    PushTableIndex(table, -1);
                else
                {
                    try
                    {
                        table.Grow(delta, growValue);
                        PushTableIndex(table, oldSize);
                    }
                    catch (OutOfMemoryException)
                    {
                        PushTableIndex(table, -1);
                    }
                }
                break;
            }
            case WasmOpCodes.TableSize:
            {
                var tableSize = Unsafe.As<TableSizeInstruction>(instr);
                var table = instance.GetTableInstance((int)tableSize.TableIndex);
                PushTableIndex(table, table.References.Length);
                break;
            }
            case WasmOpCodes.TableFill:
            {
                var tableFill = Unsafe.As<TableFillInstruction>(instr);
                var table = instance.GetTableInstance((int)tableFill.TableIndex);
                var length = PopTableIndex(table);
                var fillValue = valueStack.UnsafePop();
                var destinationOffset = PopTableIndex(table);
                var tableAddress = CalcTableAddress(
                    destinationOffset,
                    length,
                    table.References.Length
                );
                ValidateTableReference(instance, table, fillValue);
                table.References.Slice(tableAddress, length).Fill(fillValue);
                break;
            }
            default:
                throw new NotImplementedException(
                    $"Unsupported opcode: 0xFC 0x{extCode:X2}"
                );
        }
    }

    void ExitFunctionControlFrames(int controlBase)
    {
        while (controlStack.Count > controlBase)
            controlStack.Pop();
    }

    FunctionInstance ResolveIndirectFunction(WasmInstance instance, uint tableIndex, uint typeIndex)
    {
        var table = instance.GetTableInstance((int)tableIndex);
        var elementIndex =
            table.AddressType == AddressType.I64
                ? (int)valueStack.UnsafePop().I64
                : valueStack.UnsafePop().I32;
        if (elementIndex < 0 || table.References.Length <= elementIndex)
        {
            WasmTrapException.Throw($"Invalid table index: {elementIndex}");
        }

        var functionIndex = (FunctionAddress)(long)table.References[elementIndex].Bits;
        if (functionIndex == FunctionAddress.Null)
        {
            WasmTrapException.Throw($"Uninitialized table element at index: {elementIndex}");
        }

        var function = instance.Store.GetFunctionInstance((int)functionIndex);
        if (!FunctionTypeMatches(instance, function, typeIndex))
        {
            WasmTrapException.Throw("Indirect call type mismatch.");
        }

        return function;
    }

    void ExecuteGCInstruction(
        WasmInstance instance,
        Instruction instr,
        ref int localBase,
        ref int ip
    )
    {
        var gc = (GCExtensionInstruction)instr;
        switch (gc.ExtensionCode)
        {
            case WasmOpCodes.StructNew:
            {
                var sn = Unsafe.As<StructNewInstruction>(instr);
                valueStack.Push(
                    WasmValue.FromGcObject(
                        NewStruct(instance.Module, sn.TypeIndex, useDefaults: false)
                    )
                );
                break;
            }
            case WasmOpCodes.StructNewDefault:
            {
                var sn = Unsafe.As<StructNewDefaultInstruction>(instr);
                valueStack.Push(
                    WasmValue.FromGcObject(
                        NewStruct(instance.Module, sn.TypeIndex, useDefaults: true)
                    )
                );
                break;
            }
            case WasmOpCodes.StructGet:
            case WasmOpCodes.StructGetS:
            case WasmOpCodes.StructGetU:
            {
                var sg = Unsafe.As<StructGetInstruction>(instr);
                var obj = PopGcObject();
                EnsureObjectType(obj, sg.TypeIndex);
                var field = StructLayoutHelper.GetStructFieldLayout(
                    instance.Module,
                    sg.TypeIndex,
                    sg.FieldIndex
                );
                valueStack.Push(obj.Read(field, sg.Signedness));
                break;
            }
            case WasmOpCodes.StructSet:
            {
                var ss = Unsafe.As<StructSetInstruction>(instr);
                var value = valueStack.UnsafePop();
                var obj = PopGcObject();
                EnsureObjectType(obj, ss.TypeIndex);
                var field = StructLayoutHelper.GetStructFieldLayout(
                    instance.Module,
                    ss.TypeIndex,
                    ss.FieldIndex
                );
                obj.Write(field, value);
                break;
            }
            case WasmOpCodes.ArrayNew:
            {
                var an = Unsafe.As<ArrayNewInstruction>(instr);
                var length = valueStack.UnsafePop().I32;
                if (length < 0)
                    WasmTrapException.Throw("array length is negative");
                var initial = valueStack.UnsafePop();
                valueStack.Push(
                    WasmValue.FromGcObject(
                        GcArray.Create(instance.Module, an.TypeIndex, length, initial)
                    )
                );
                break;
            }
            case WasmOpCodes.ArrayNewDefault:
            {
                var an = Unsafe.As<ArrayNewDefaultInstruction>(instr);
                var length = valueStack.UnsafePop().I32;
                if (length < 0)
                    WasmTrapException.Throw("array length is negative");
                valueStack.Push(
                    WasmValue.FromGcObject(
                        GcArray.Create(
                            instance.Module,
                            an.TypeIndex,
                            length,
                            WasmValue.Default(
                                instance.Module.GetArrayType(an.TypeIndex).Field.StorageType
                            )
                        )
                    )
                );
                break;
            }
            case WasmOpCodes.ArrayNewFixed:
            {
                var an = Unsafe.As<ArrayNewFixedInstruction>(instr);
                var length = checked((int)an.Length);
                var array = GcArray.Create(instance.Module, an.TypeIndex, length, default);
                var field = instance.Module.GetArrayType(an.TypeIndex).Field;
                for (var i = length - 1; i >= 0; i--)
                {
                    array.Write(field, i, valueStack.UnsafePop());
                }
                valueStack.Push(WasmValue.FromGcObject(array));
                break;
            }
            case WasmOpCodes.ArrayNewData:
            {
                var an = Unsafe.As<ArrayNewDataInstruction>(instr);
                var length = valueStack.UnsafePop().I32;
                var sourceOffset = valueStack.UnsafePop().I32;
                if (length < 0)
                    WasmTrapException.Throw("array length is negative");

                var field = instance.Module.GetArrayType(an.TypeIndex).Field;
                if (!field.StorageType.TryGetByteSize(out var elementSize))
                    throw new InvalidOperationException(
                        "array.new_data requires a numeric array type"
                    );

                var byteLength = checked(length * elementSize);
                var dataSegment = instance.Module.Data[(int)an.DataIndex];
                var dataLength = instance.IsDataSegmentDropped((int)an.DataIndex)
                    ? 0
                    : dataSegment.Data.Length;
                WasmTrapException.ThrowIfNot(
                    sourceOffset >= 0
                        && (ulong)(uint)sourceOffset + (uint)byteLength <= (uint)dataLength,
                    "Data segment access out of bounds"
                );

                var array = GcArray.Create(instance.Module, an.TypeIndex, length, default);
                dataSegment.Data.Span.Slice(sourceOffset, byteLength).CopyTo(array.Data);
                valueStack.Push(WasmValue.FromGcObject(array));
                break;
            }
            case WasmOpCodes.ArrayNewElem:
            {
                var an = Unsafe.As<ArrayNewElemInstruction>(instr);
                var length = valueStack.UnsafePop().I32;
                var sourceOffset = valueStack.UnsafePop().I32;
                if (length < 0)
                    WasmTrapException.Throw("array length is negative");

                var field = instance.Module.GetArrayType(an.TypeIndex).Field;
                if (!field.StorageType.IsRefType())
                    throw new InvalidOperationException(
                        "array.new_elem requires a reference array type"
                    );

                var isDropped = instance.IsElementSegmentDropped((int)an.ElementIndex);
                var elementValues = instance.GetElementSegment((int)an.ElementIndex);
                var elementLength = isDropped ? 0 : elementValues.Length;
                WasmTrapException.ThrowIfNot(
                    sourceOffset >= 0
                        && (ulong)(uint)sourceOffset + (uint)length <= (uint)elementLength,
                    "Element segment access out of bounds"
                );

                var array = GcArray.Create(instance.Module, an.TypeIndex, length, default);
                elementValues.Slice(sourceOffset, length).CopyTo(array.References.AsSpan());
                valueStack.Push(WasmValue.FromGcObject(array));
                break;
            }
            case WasmOpCodes.ArrayGet:
            case WasmOpCodes.ArrayGetS:
            case WasmOpCodes.ArrayGetU:
            {
                var ag = Unsafe.As<ArrayGetInstruction>(instr);
                var index = valueStack.UnsafePop().I32;
                var obj = PopGcObject();
                EnsureObjectType(obj, ag.TypeIndex);
                CheckArrayIndex(obj, index);
                valueStack.Push(
                    obj.Read(instance.Module.GetArrayType(ag.TypeIndex).Field, index, ag.Signedness)
                );
                break;
            }
            case WasmOpCodes.ArraySet:
            {
                var aset = Unsafe.As<ArraySetInstruction>(instr);
                var value = valueStack.UnsafePop();
                var index = valueStack.UnsafePop().I32;
                var obj = PopGcObject();
                EnsureObjectType(obj, aset.TypeIndex);
                CheckArrayIndex(obj, index);
                obj.Write(instance.Module.GetArrayType(aset.TypeIndex).Field, index, value);
                break;
            }
            case WasmOpCodes.ArrayLen:
            {
                var obj = PopGcObject();
                if (obj is not GcArray)
                    WasmTrapException.Throw("array operation on non-array reference");
                valueStack.Push(WasmValue.FromI32(((GcArray)obj).Length));
                break;
            }
            case WasmOpCodes.ArrayFill:
            {
                var af = Unsafe.As<ArrayFillInstruction>(instr);
                var count = valueStack.UnsafePop().I32;
                var value = valueStack.UnsafePop();
                var offset = valueStack.UnsafePop().I32;
                var obj = PopGcObject();
                EnsureObjectType(obj, af.TypeIndex);
                CheckArrayRange(obj, offset, count);
                var field = instance.Module.GetArrayType(af.TypeIndex).Field;
                for (var i = 0; i < count; i++)
                    obj.Write(field, offset + i, value);
                break;
            }
            case WasmOpCodes.ArrayCopy:
            {
                var ac = Unsafe.As<ArrayCopyInstruction>(instr);
                var count = valueStack.UnsafePop().I32;
                var srcOffset = valueStack.UnsafePop().I32;
                var src = PopGcObject();
                var dstOffset = valueStack.UnsafePop().I32;
                var dst = PopGcObject();
                EnsureObjectType(src, ac.SourceTypeIndex);
                EnsureObjectType(dst, ac.DestinationTypeIndex);
                CheckArrayRange(src, srcOffset, count);
                CheckArrayRange(dst, dstOffset, count);
                var srcField = instance.Module.GetArrayType(ac.SourceTypeIndex).Field;
                var dstField = instance.Module.GetArrayType(ac.DestinationTypeIndex).Field;
                if (ReferenceEquals(src, dst) && dstOffset > srcOffset)
                {
                    for (var i = count - 1; i >= 0; i--)
                    {
                        dst.Write(dstField, dstOffset + i, src.Read(srcField, srcOffset + i));
                    }
                }
                else
                    for (var i = 0; i < count; i++)
                        dst.Write(dstField, dstOffset + i, src.Read(srcField, srcOffset + i));
                break;
            }
            case WasmOpCodes.ArrayInitData:
            {
                var ai = Unsafe.As<ArrayInitDataInstruction>(instr);
                var count = valueStack.UnsafePop().I32;
                var sourceOffset = valueStack.UnsafePop().I32;
                var destinationOffset = valueStack.UnsafePop().I32;
                var obj = PopGcObject();
                EnsureObjectType(obj, ai.TypeIndex);
                CheckArrayRange(obj, destinationOffset, count);

                var field = instance.Module.GetArrayType(ai.TypeIndex).Field;
                if (!field.Mutable)
                    WasmTrapException.Throw("cannot initialize an immutable array");

                if (!field.StorageType.TryGetByteSize(out var elementSize))
                    throw new InvalidOperationException(
                        "array.init_data requires an array type with a fixed element size"
                    );

                var byteLength = checked(count * elementSize);
                var dataSegment = instance.Module.Data[(int)ai.DataIndex];
                var dataLength = instance.IsDataSegmentDropped((int)ai.DataIndex)
                    ? 0
                    : dataSegment.Data.Length;
                WasmTrapException.ThrowIfNot(
                    sourceOffset >= 0
                        && count >= 0
                        && (ulong)(uint)sourceOffset + (uint)byteLength <= (uint)dataLength,
                    "Data segment access out of bounds"
                );

                dataSegment
                    .Data.Span.Slice(sourceOffset, byteLength)
                    .CopyTo(obj.Data.AsSpan(destinationOffset * elementSize, byteLength));
                break;
            }
            case WasmOpCodes.ArrayInitElem:
            {
                var ai = Unsafe.As<ArrayInitElemInstruction>(instr);
                var count = valueStack.UnsafePop().I32;
                var sourceOffset = valueStack.UnsafePop().I32;
                var destinationOffset = valueStack.UnsafePop().I32;
                var obj = PopGcObject();
                EnsureObjectType(obj, ai.TypeIndex);
                CheckArrayRange(obj, destinationOffset, count);

                var field = instance.Module.GetArrayType(ai.TypeIndex).Field;
                if (!field.Mutable)
                    WasmTrapException.Throw("cannot initialize an immutable array");
                if (!field.StorageType.IsRefType())
                    throw new InvalidOperationException(
                        "array.init_elem requires a reference array type"
                    );

                var elementValues = instance.GetElementSegment((int)ai.ElementIndex);
                var elementLength = instance.IsElementSegmentDropped((int)ai.ElementIndex)
                    ? 0
                    : elementValues.Length;
                WasmTrapException.ThrowIfNot(
                    sourceOffset >= 0
                        && count >= 0
                        && (ulong)(uint)sourceOffset + (uint)count <= (uint)elementLength,
                    "Element segment access out of bounds"
                );

                elementValues
                    .Slice(sourceOffset, count)
                    .CopyTo(obj.References.AsSpan(destinationOffset, count));
                break;
            }
            case WasmOpCodes.RefTest:
            case WasmOpCodes.RefTestNull:
            {
                var rt = Unsafe.As<RefTestInstruction>(instr);
                valueStack.Push(
                    WasmValue.FromI32(
                        ReferenceMatches(instance, valueStack.UnsafePop(), rt.ReferenceType) ? 1 : 0
                    )
                );
                break;
            }
            case WasmOpCodes.RefCast:
            case WasmOpCodes.RefCastNull:
            {
                var rc = Unsafe.As<RefCastInstruction>(instr);
                var value = valueStack.UnsafePop();
                if (!ReferenceMatches(instance, value, rc.ReferenceType))
                    WasmTrapException.Throw("cast failure");
                valueStack.Push(value);
                break;
            }
            case WasmOpCodes.BrOnCast:
            {
                var bc = Unsafe.As<BrOnCastInstruction>(instr);
                var value = valueStack.UnsafePop();
                if (ReferenceMatches(instance, value, bc.TargetReferenceType))
                {
                    valueStack.Push(value);
                    BranchToLabel((int)bc.LabelIndex, ref localBase, ref ip);
                }
                else
                    valueStack.Push(value);
                break;
            }
            case WasmOpCodes.BrOnCastFail:
            {
                var bc = Unsafe.As<BrOnCastFailInstruction>(instr);
                var value = valueStack.UnsafePop();
                if (!ReferenceMatches(instance, value, bc.TargetReferenceType))
                {
                    valueStack.Push(value);
                    BranchToLabel((int)bc.LabelIndex, ref localBase, ref ip);
                }
                else
                    valueStack.Push(value);
                break;
            }
            case WasmOpCodes.RefI31:
                valueStack.Push(WasmValue.FromI32(valueStack.UnsafePop().I32 & 0x7FFF_FFFF));
                break;
            case WasmOpCodes.I31GetS:
            {
                var value = valueStack.UnsafePop();
                if (value.IsNullReference)
                    WasmTrapException.Throw("null i31 reference");
                var i31 = value.I32 & 0x7FFF_FFFF;
                valueStack.Push(WasmValue.FromI32((i31 << 1) >> 1));
                break;
            }
            case WasmOpCodes.I31GetU:
            {
                var value = valueStack.UnsafePop();
                if (value.IsNullReference)
                    WasmTrapException.Throw("null i31 reference");
                valueStack.Push(WasmValue.FromI32(value.I32 & 0x7FFF_FFFF));
                break;
            }
            case WasmOpCodes.AnyConvertExtern:
            {
                var value = valueStack.UnsafePop();
                if (
                    value.Reference is ExternalReference { WrapsAnyReference: true } externReference
                )
                    valueStack.Push(externReference.Value);
                else
                    valueStack.Push(value);
                break;
            }
            case WasmOpCodes.ExternConvertAny:
            {
                var value = valueStack.UnsafePop();
                valueStack.Push(
                    value.IsNullReference
                        ? WasmValue.NullReference
                        : WasmValue.FromExternReference(
                            new ExternalReference { Value = value, WrapsAnyReference = true }
                        )
                );
                break;
            }
            default:
                throw new NotImplementedException(
                    $"GC opcode 0x{gc.ExtensionCode:X} is not implemented."
                );
        }
    }

    void ExecuteSimdInstruction(
        WasmInstance instance,
        Instruction instr,
        ref int localBase,
        ref int ip
    )
    {
        var simdInstr = (SIMDExtensionInstruction)instr;
        var extCode = simdInstr.ExtensionCode;
        switch (extCode)
        {
            case WasmOpCodes.V128Load:
            case WasmOpCodes.V128Load32Zero:
            case WasmOpCodes.V128Load64Zero:
            {
                var loadSimd = Unsafe.As<V128LoadInstruction>(simdInstr);
                var simdMemory = instance.GetMemoryInstance((int)loadSimd.MemoryIndex);
                var loadAddress = CalcMemoryAddress(
                    PopMemoryAddress(simdMemory),
                    loadSimd.Offset,
                    16,
                    simdMemory.Data.Length
                );
                ulong lower,
                    upper;
                if (extCode is WasmOpCodes.V128Load32Zero)
                {
                    lower = Unsafe.ReadUnaligned<uint>(
                        ref MemoryMarshal.GetReference(simdMemory.Data[loadAddress..])
                    );
                    upper = 0;
                }
                else if (extCode is WasmOpCodes.V128Load64Zero)
                {
                    lower = Unsafe.ReadUnaligned<ulong>(
                        ref MemoryMarshal.GetReference(simdMemory.Data[loadAddress..])
                    );
                    upper = 0;
                }
                else
                {
                    lower = Unsafe.ReadUnaligned<ulong>(
                        ref MemoryMarshal.GetReference(simdMemory.Data[loadAddress..])
                    );
                    upper = Unsafe.ReadUnaligned<ulong>(
                        ref MemoryMarshal.GetReference(simdMemory.Data[(loadAddress + 8)..])
                    );
                }
                valueStack.Push(WasmValue.FromV128(new WasmV128Value(lower, upper)));
                break;
            }
            case WasmOpCodes.V128Load8Splat:
            case WasmOpCodes.V128Load16Splat:
            case WasmOpCodes.V128Load32Splat:
            case WasmOpCodes.V128Load64Splat:
            {
                var loadSimd = Unsafe.As<V128LoadInstruction>(simdInstr);
                var simdMemory = instance.GetMemoryInstance((int)loadSimd.MemoryIndex);
                var splatSize = extCode switch
                {
                    WasmOpCodes.V128Load8Splat => 1,
                    WasmOpCodes.V128Load16Splat => 2,
                    WasmOpCodes.V128Load32Splat => 4,
                    _ => 8,
                };
                var loadAddress = CalcMemoryAddress(
                    PopMemoryAddress(simdMemory),
                    loadSimd.Offset,
                    splatSize,
                    simdMemory.Data.Length
                );
                Span<byte> result = stackalloc byte[16];
                var src = simdMemory.Data.Slice(loadAddress, splatSize);
                switch (splatSize)
                {
                    case 1:
                        result.Fill(src[0]);
                        break;
                    case 2:
                    {
                        var val = Unsafe.ReadUnaligned<ushort>(ref MemoryMarshal.GetReference(src));
                        MemoryMarshal.Cast<byte, ushort>(result).Fill(val);
                        break;
                    }
                    case 4:
                    {
                        var val = Unsafe.ReadUnaligned<uint>(ref MemoryMarshal.GetReference(src));
                        MemoryMarshal.Cast<byte, uint>(result).Fill(val);
                        break;
                    }
                    default:
                    {
                        var val = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(src));
                        MemoryMarshal.Cast<byte, ulong>(result).Fill(val);
                        break;
                    }
                }
                valueStack.Push(
                    WasmValue.FromV128(
                        new WasmV128Value(
                            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(result)),
                            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(result[8..]))
                        )
                    )
                );
                break;
            }
            case WasmOpCodes.V128Load8x8S:
            case WasmOpCodes.V128Load8x8U:
            case WasmOpCodes.V128Load16x4S:
            case WasmOpCodes.V128Load16x4U:
            case WasmOpCodes.V128Load32x2S:
            case WasmOpCodes.V128Load32x2U:
            {
                var loadSimd = Unsafe.As<V128LoadInstruction>(simdInstr);
                var simdMemory = instance.GetMemoryInstance((int)loadSimd.MemoryIndex);
                var signed =
                    extCode
                    is WasmOpCodes.V128Load8x8S
                        or WasmOpCodes.V128Load16x4S
                        or WasmOpCodes.V128Load32x2S;
                int sourceLanes;
                int sourceSize;
                if (extCode is WasmOpCodes.V128Load8x8S or WasmOpCodes.V128Load8x8U)
                {
                    sourceLanes = 8;
                    sourceSize = 1;
                }
                else if (extCode is WasmOpCodes.V128Load16x4S or WasmOpCodes.V128Load16x4U)
                {
                    sourceLanes = 4;
                    sourceSize = 2;
                }
                else
                {
                    sourceLanes = 2;
                    sourceSize = 4;
                }
                var sourceBytes = sourceLanes * sourceSize;
                var loadAddress = CalcMemoryAddress(
                    PopMemoryAddress(simdMemory),
                    loadSimd.Offset,
                    sourceBytes,
                    simdMemory.Data.Length
                );
                var source = simdMemory.Data.Slice(loadAddress, sourceBytes);
                Span<byte> result = stackalloc byte[16];
                for (var i = 0; i < sourceLanes; i++)
                {
                    var srcOffset = i * sourceSize;
                    var dstOffset = i * sourceSize * 2;
                    if (sourceSize == 1)
                    {
                        var val = source[srcOffset];
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(result.Slice(dstOffset, 2)),
                            signed ? (short)(sbyte)val : val
                        );
                    }
                    else if (sourceSize == 2)
                    {
                        var val = Unsafe.ReadUnaligned<short>(
                            ref MemoryMarshal.GetReference(source.Slice(srcOffset, 2))
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(result.Slice(dstOffset, 4)),
                            signed ? (int)val : (int)(ushort)val
                        );
                    }
                    else
                    {
                        var val = Unsafe.ReadUnaligned<int>(
                            ref MemoryMarshal.GetReference(source.Slice(srcOffset, 4))
                        );
                        Unsafe.WriteUnaligned(
                            ref MemoryMarshal.GetReference(result.Slice(dstOffset, 8)),
                            signed ? (long)val : (long)(uint)val
                        );
                    }
                }
                valueStack.Push(
                    WasmValue.FromV128(
                        new WasmV128Value(
                            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(result)),
                            Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(result[8..]))
                        )
                    )
                );
                break;
            }
            case WasmOpCodes.V128Load8Lane:
            case WasmOpCodes.V128Load16Lane:
            case WasmOpCodes.V128Load32Lane:
            case WasmOpCodes.V128Load64Lane:
            {
                var (memoryIndex, offset, laneIndex) = GetSimdLaneMemArg(simdInstr);
                var simdMemory = instance.GetMemoryInstance((int)memoryIndex);
                var vector = valueStack.UnsafePop().V128;
                var loadAddress = CalcMemoryAddress(
                    PopMemoryAddress(simdMemory),
                    offset,
                    SimdLaneByteWidth(extCode),
                    simdMemory.Data.Length
                );
                var result = vector.ToBytes().AsSpan();
                simdMemory
                    .Data.Slice(loadAddress, SimdLaneByteWidth(extCode))
                    .CopyTo(result[(laneIndex * SimdLaneByteWidth(extCode))..]);
                valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
                break;
            }
            case WasmOpCodes.V128Store:
            {
                var storeSimd = Unsafe.As<V128StoreInstruction>(simdInstr);
                var simdMemory = instance.GetMemoryInstance((int)storeSimd.MemoryIndex);
                var v128 = valueStack.UnsafePop().V128;
                var storeAddress = CalcMemoryAddress(
                    PopMemoryAddress(simdMemory),
                    storeSimd.Offset,
                    16,
                    simdMemory.Data.Length
                );
                Unsafe.WriteUnaligned(
                    ref MemoryMarshal.GetReference(simdMemory.Data[storeAddress..]),
                    v128.LowerBits
                );
                Unsafe.WriteUnaligned(
                    ref MemoryMarshal.GetReference(simdMemory.Data[(storeAddress + 8)..]),
                    v128.UpperBits
                );
                break;
            }
            case WasmOpCodes.V128Store8Lane:
            case WasmOpCodes.V128Store16Lane:
            case WasmOpCodes.V128Store32Lane:
            case WasmOpCodes.V128Store64Lane:
            {
                var (memoryIndex, offset, laneIndex) = GetSimdLaneMemArg(simdInstr);
                var simdMemory = instance.GetMemoryInstance((int)memoryIndex);
                var vector = valueStack.UnsafePop().V128;
                var storeAddress = CalcMemoryAddress(
                    PopMemoryAddress(simdMemory),
                    offset,
                    SimdLaneByteWidth(extCode),
                    simdMemory.Data.Length
                );
                Span<byte> vectorBytes = stackalloc byte[16];
                vector.ToBytes(vectorBytes);
                vectorBytes
                    .Slice(laneIndex * SimdLaneByteWidth(extCode), SimdLaneByteWidth(extCode))
                    .CopyTo(simdMemory.Data.Slice(storeAddress, SimdLaneByteWidth(extCode)));
                break;
            }
            case WasmOpCodes.V128Const:
            {
                var v128c = Unsafe.As<V128ConstInstruction>(simdInstr);
                valueStack.Push(
                    WasmValue.FromV128(new WasmV128Value(v128c.LowerBits, v128c.UpperBits))
                );
                break;
            }
            case WasmOpCodes.I8x16Shuffle:
            {
                var shuffle = Unsafe.As<I8x16ShuffleInstruction>(simdInstr);
                var b = valueStack.UnsafePop().V128;
                var a = valueStack.UnsafePop().V128;
                Span<byte> aBytes = stackalloc byte[16];
                Span<byte> bBytes = stackalloc byte[16];
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference<byte>(aBytes), a.LowerBits);
                Unsafe.WriteUnaligned(
                    ref MemoryMarshal.GetReference<byte>(aBytes.Slice(8)),
                    a.UpperBits
                );
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference<byte>(bBytes), b.LowerBits);
                Unsafe.WriteUnaligned(
                    ref MemoryMarshal.GetReference<byte>(bBytes.Slice(8)),
                    b.UpperBits
                );
                Span<byte> result = stackalloc byte[16];
                for (var j = 0; j < 16; j++)
                {
                    var lane = shuffle.Lanes[j];
                    result[j] = lane < 16 ? aBytes[lane] : bBytes[lane - 16];
                }
                var resLower = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(result));
                var resUpper = Unsafe.ReadUnaligned<ulong>(
                    ref MemoryMarshal.GetReference(result[8..])
                );
                valueStack.Push(WasmValue.FromV128(new WasmV128Value(resLower, resUpper)));
                break;
            }
            case WasmOpCodes.I8x16Swizzle:
            case WasmOpCodes.I8x16RelaxedSwizzle:
            {
                var s = valueStack.UnsafePop();
                var a = valueStack.UnsafePop();
                var av = a.V128.ToVector128();
                var sv = s.V128.ToVector128();
                Span<byte> result = stackalloc byte[16];
                for (var i = 0; i < 16; i++)
                {
                    var index = sv.AsByte().GetElement(i);
                    result[i] = index < 16 ? av.AsByte().GetElement((int)index) : (byte)0;
                }
                valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
                break;
            }
            case >= WasmOpCodes.I8x16Splat and <= WasmOpCodes.F64x2Splat:
            {
                var result = extCode switch
                {
                    WasmOpCodes.I8x16Splat => Vector128.Create((byte)valueStack.UnsafePop().I32),
                    WasmOpCodes.I16x8Splat => Vector128
                        .Create((ushort)valueStack.UnsafePop().I32)
                        .AsByte(),
                    WasmOpCodes.I32x4Splat => Vector128
                        .Create((uint)valueStack.UnsafePop().I32)
                        .AsByte(),
                    WasmOpCodes.I64x2Splat => Vector128
                        .Create((ulong)valueStack.UnsafePop().I64)
                        .AsByte(),
                    WasmOpCodes.F32x4Splat => Vector128
                        .Create(BitConverter.SingleToUInt32Bits(valueStack.UnsafePop().F32))
                        .AsByte(),
                    _ => Vector128
                        .Create(BitConverter.DoubleToUInt64Bits(valueStack.UnsafePop().F64))
                        .AsByte(),
                };
                valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result)));
                break;
            }
            case >= WasmOpCodes.I8x16ExtractLaneS and <= WasmOpCodes.F64x2ReplaceLane:
                ExecuteSimdLane(extCode, GetSIMDExtractReplaceLaneIndex(simdInstr));
                break;
            case >= WasmOpCodes.I8x16Eq and <= WasmOpCodes.F64x2Ge:
            case >= WasmOpCodes.I64x2Eq and <= WasmOpCodes.I64x2GeS:
            {
                var b = valueStack.UnsafePop().V128.ToVector128();
                var a = valueStack.UnsafePop().V128.ToVector128();
                var result = (extCode, laneBytes: 0) switch
                {
                    (>= WasmOpCodes.I8x16Eq and <= WasmOpCodes.I8x16GeU, _) =>
                        SimdHelper.CompareInt(extCode - WasmOpCodes.I8x16Eq, 1, a, b),
                    (>= WasmOpCodes.I16x8Eq and <= WasmOpCodes.I16x8GeU, _) =>
                        SimdHelper.CompareInt(extCode - WasmOpCodes.I16x8Eq, 2, a, b),
                    (>= WasmOpCodes.I32x4Eq and <= WasmOpCodes.I32x4GeU, _) =>
                        SimdHelper.CompareInt(extCode - WasmOpCodes.I32x4Eq, 4, a, b),
                    (>= WasmOpCodes.F32x4Eq and <= WasmOpCodes.F32x4Ge, _) =>
                        SimdHelper.CompareFloat32(extCode - WasmOpCodes.F32x4Eq, a, b),
                    (>= WasmOpCodes.F64x2Eq and <= WasmOpCodes.F64x2Ge, _) =>
                        SimdHelper.CompareFloat64(extCode - WasmOpCodes.F64x2Eq, a, b),
                    _ => SimdHelper.CompareInt(extCode - WasmOpCodes.I64x2Eq, 8, a, b),
                };
                valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result)));
                break;
            }
            case >= WasmOpCodes.V128Not and <= WasmOpCodes.V128AnyTrue:
            {
                switch (extCode)
                {
                    case WasmOpCodes.V128Not:
                    {
                        var a = valueStack.UnsafePop();
                        valueStack.Push(
                            WasmValue.FromV128(
                                new WasmV128Value(~a.V128.LowerBits, ~a.V128.UpperBits)
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.V128And:
                    case WasmOpCodes.V128AndNot:
                    case WasmOpCodes.V128Or:
                    case WasmOpCodes.V128Xor:
                    {
                        var b = valueStack.UnsafePop();
                        var a = valueStack.UnsafePop();
                        var lower = extCode switch
                        {
                            WasmOpCodes.V128And => a.V128.LowerBits & b.V128.LowerBits,
                            WasmOpCodes.V128AndNot => a.V128.LowerBits & ~b.V128.LowerBits,
                            WasmOpCodes.V128Or => a.V128.LowerBits | b.V128.LowerBits,
                            _ => a.V128.LowerBits ^ b.V128.LowerBits,
                        };
                        var upper = extCode switch
                        {
                            WasmOpCodes.V128And => a.V128.UpperBits & b.V128.UpperBits,
                            WasmOpCodes.V128AndNot => a.V128.UpperBits & ~b.V128.UpperBits,
                            WasmOpCodes.V128Or => a.V128.UpperBits | b.V128.UpperBits,
                            _ => a.V128.UpperBits ^ b.V128.UpperBits,
                        };
                        valueStack.Push(WasmValue.FromV128(new WasmV128Value(lower, upper)));
                        break;
                    }
                    case WasmOpCodes.V128BitSelect:
                    {
                        var c = valueStack.UnsafePop();
                        var b = valueStack.UnsafePop();
                        var a = valueStack.UnsafePop();
                        valueStack.Push(
                            WasmValue.FromV128(
                                new WasmV128Value(
                                    (a.V128.LowerBits & c.V128.LowerBits)
                                        | (b.V128.LowerBits & ~c.V128.LowerBits),
                                    (a.V128.UpperBits & c.V128.UpperBits)
                                        | (b.V128.UpperBits & ~c.V128.UpperBits)
                                )
                            )
                        );
                        break;
                    }
                    case WasmOpCodes.V128AnyTrue:
                    {
                        var a = valueStack.UnsafePop();
                        valueStack.Push(
                            WasmValue.FromI32((a.V128.LowerBits | a.V128.UpperBits) != 0 ? 1 : 0)
                        );
                        break;
                    }
                }
                break;
            }
            case >= WasmOpCodes.I8x16Abs and <= WasmOpCodes.F64x2PMax:
                ExecuteSimdNumeric(extCode);
                break;
            case WasmOpCodes.F32x4DemoteF64x2Zero:
            case WasmOpCodes.F64x2PromoteLowF32x4:
            case >= WasmOpCodes.I32x4TruncSatF32x4S and <= WasmOpCodes.F64x2ConvertLowI32x4U:
                ExecuteSimdConvert(extCode);
                break;
            case >= WasmOpCodes.I8x16RelaxedSwizzle
            and <= WasmOpCodes.I32x4RelaxedDotI8x16I7x16AddS:
                ExecuteSimdRelaxed(extCode);
                break;
            default:
                throw new NotImplementedException(
                    $"SIMD opcode 0xFD 0x{extCode:X} is not implemented."
                );
        }
    }

    void ExecuteSimdLane(uint opcode, byte lane)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (
            opcode
            is WasmOpCodes.I8x16ReplaceLane
                or WasmOpCodes.I16x8ReplaceLane
                or WasmOpCodes.I32x4ReplaceLane
                or WasmOpCodes.I64x2ReplaceLane
                or WasmOpCodes.F32x4ReplaceLane
                or WasmOpCodes.F64x2ReplaceLane
        )
        {
            var scalar = valueStack.UnsafePop();
            var vector = valueStack.UnsafePop();
            vector.V128.ToBytes(bytes);
            switch (opcode)
            {
                case WasmOpCodes.I8x16ReplaceLane:
                    bytes[lane] = (byte)scalar.I32;
                    break;
                case WasmOpCodes.I16x8ReplaceLane:
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes[(lane * 2)..],
                        (ushort)scalar.I32
                    );
                    break;
                case WasmOpCodes.I32x4ReplaceLane:
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[(lane * 4)..], (uint)scalar.I32);
                    break;
                case WasmOpCodes.I64x2ReplaceLane:
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes[(lane * 8)..],
                        (ulong)scalar.I64
                    );
                    break;
                case WasmOpCodes.F32x4ReplaceLane:
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes[(lane * 4)..],
                        BitConverter.SingleToUInt32Bits(scalar.F32)
                    );
                    break;
                case WasmOpCodes.F64x2ReplaceLane:
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes[(lane * 8)..],
                        BitConverter.DoubleToUInt64Bits(scalar.F64)
                    );
                    break;
            }
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(bytes)));
            return;
        }

        valueStack.UnsafePop().V128.ToBytes(bytes);
        switch (opcode)
        {
            case WasmOpCodes.I8x16ExtractLaneS:
                valueStack.Push(WasmValue.FromI32((sbyte)bytes[lane]));
                break;
            case WasmOpCodes.I8x16ExtractLaneU:
                valueStack.Push(WasmValue.FromI32(bytes[lane]));
                break;
            case WasmOpCodes.I16x8ExtractLaneS:
                valueStack.Push(
                    WasmValue.FromI32(BinaryPrimitives.ReadInt16LittleEndian(bytes[(lane * 2)..]))
                );
                break;
            case WasmOpCodes.I16x8ExtractLaneU:
                valueStack.Push(
                    WasmValue.FromI32(BinaryPrimitives.ReadUInt16LittleEndian(bytes[(lane * 2)..]))
                );
                break;
            case WasmOpCodes.I32x4ExtractLane:
                valueStack.Push(
                    WasmValue.FromI32(BinaryPrimitives.ReadInt32LittleEndian(bytes[(lane * 4)..]))
                );
                break;
            case WasmOpCodes.I64x2ExtractLane:
                valueStack.Push(
                    WasmValue.FromI64(BinaryPrimitives.ReadInt64LittleEndian(bytes[(lane * 8)..]))
                );
                break;
            case WasmOpCodes.F32x4ExtractLane:
                valueStack.Push(
                    WasmValue.FromF32(
                        BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32LittleEndian(bytes[(lane * 4)..])
                        )
                    )
                );
                break;
            case WasmOpCodes.F64x2ExtractLane:
                valueStack.Push(
                    WasmValue.FromF64(
                        BitConverter.Int64BitsToDouble(
                            BinaryPrimitives.ReadInt64LittleEndian(bytes[(lane * 8)..])
                        )
                    )
                );
                break;
        }
    }

    void ExecuteSimdNumeric(uint opcode)
    {
        switch (opcode)
        {
            case WasmOpCodes.I8x16Abs:
            case WasmOpCodes.I8x16Neg:
            case WasmOpCodes.I8x16Popcnt:
            case WasmOpCodes.I8x16AllTrue:
            case WasmOpCodes.I8x16Bitmask:
            case WasmOpCodes.I8x16NarrowI16x8S:
            case WasmOpCodes.I8x16NarrowI16x8U:
            case >= WasmOpCodes.I8x16Shl and <= WasmOpCodes.I8x16SubSaturateU:
            case WasmOpCodes.I8x16MinS:
            case WasmOpCodes.I8x16MinU:
            case WasmOpCodes.I8x16MaxS:
            case WasmOpCodes.I8x16MaxU:
            case WasmOpCodes.I8x16AvgrU:
                SimdNumericI8x16(opcode);
                return;
            case WasmOpCodes.I16x8ExtMulLowI8x16S:
            case WasmOpCodes.I16x8ExtMulHighI8x16S:
            case WasmOpCodes.I16x8ExtMulLowI8x16U:
            case WasmOpCodes.I16x8ExtMulHighI8x16U:
                SimdExtMul(opcode, 2, 8);
                return;
            case WasmOpCodes.I32x4DotI16x8S:
                SimdDot();
                return;
            case WasmOpCodes.I32x4ExtMulLowI16x8S:
            case WasmOpCodes.I32x4ExtMulHighI16x8S:
            case WasmOpCodes.I32x4ExtMulLowI16x8U:
            case WasmOpCodes.I32x4ExtMulHighI16x8U:
                SimdExtMul(opcode, 4, 4);
                return;
            case WasmOpCodes.I64x2ExtMulLowI32x4S:
            case WasmOpCodes.I64x2ExtMulHighI32x4S:
            case WasmOpCodes.I64x2ExtMulLowI32x4U:
            case WasmOpCodes.I64x2ExtMulHighI32x4U:
                SimdExtMul(opcode, 8, 2);
                return;
            case WasmOpCodes.I16x8ExtaddPairwiseI8x16S:
            case WasmOpCodes.I16x8ExtaddPairwiseI8x16U:
            case WasmOpCodes.I16x8NarrowI32x4S:
            case WasmOpCodes.I16x8NarrowI32x4U:
            case WasmOpCodes.I16x8Abs:
            case WasmOpCodes.I16x8Neg:
            case WasmOpCodes.I16x8Q15mulrSatS:
            case WasmOpCodes.I16x8AllTrue:
            case WasmOpCodes.I16x8Bitmask:
            case >= WasmOpCodes.I16x8Shl and <= WasmOpCodes.I16x8SubSaturateU:
            case >= WasmOpCodes.I16x8Mul and <= WasmOpCodes.I16x8ExtMulHighI8x16U:
                SimdNumericI16x8(opcode);
                return;
            case WasmOpCodes.I32x4ExtaddPairwiseI16x8S:
            case WasmOpCodes.I32x4ExtaddPairwiseI16x8U:
            case WasmOpCodes.I32x4Abs:
            case WasmOpCodes.I32x4Neg:
            case WasmOpCodes.I32x4AllTrue:
            case WasmOpCodes.I32x4Bitmask:
            case >= WasmOpCodes.I32x4Shl and <= WasmOpCodes.I32x4ExtMulHighI16x8U:
                SimdNumericI32x4(opcode);
                return;
            case WasmOpCodes.I64x2Abs:
            case WasmOpCodes.I64x2Neg:
            case WasmOpCodes.I64x2AllTrue:
            case WasmOpCodes.I64x2Bitmask:
            case >= WasmOpCodes.I64x2Shl and <= WasmOpCodes.I64x2Mul:
                SimdNumericI64x2(opcode);
                return;
            case WasmOpCodes.F32x4Ceil:
            case WasmOpCodes.F32x4Floor:
            case WasmOpCodes.F32x4Trunc:
            case WasmOpCodes.F32x4Nearest:
            case >= WasmOpCodes.F32x4Abs and <= WasmOpCodes.F32x4PMax:
                SimdNumericF32x4(opcode);
                return;
            case WasmOpCodes.F64x2Ceil:
            case WasmOpCodes.F64x2Floor:
            case WasmOpCodes.F64x2Trunc:
            case WasmOpCodes.F64x2Nearest:
            case >= WasmOpCodes.F64x2Abs and <= WasmOpCodes.F64x2PMax:
                SimdNumericF64x2(opcode);
                return;
            default:
                SimdExtend(opcode);
                return;
        }
    }

    void SimdNumericI8x16(uint opcode)
    {
        Span<byte> result = stackalloc byte[16];
        if (
            opcode
            is WasmOpCodes.I8x16Abs
                or WasmOpCodes.I8x16Neg
                or WasmOpCodes.I8x16Popcnt
                or WasmOpCodes.I8x16AllTrue
                or WasmOpCodes.I8x16Bitmask
        )
        {
            var a = valueStack.UnsafePop().V128.ToVector128();
            switch (opcode)
            {
                case WasmOpCodes.I8x16Abs:
                    valueStack.Push(
                        WasmValue.FromV128(
                            WasmV128Value.FromVector128(Vector128.Abs(a.AsSByte()).AsByte())
                        )
                    );
                    return;
                case WasmOpCodes.I8x16Neg:
                    valueStack.Push(
                        WasmValue.FromV128(
                            WasmV128Value.FromVector128(Vector128.Negate(a.AsSByte()).AsByte())
                        )
                    );
                    return;
                case WasmOpCodes.I8x16AllTrue:
                    valueStack.Push(WasmValue.FromI32(SimdHelper.AllTrue(a) ? 1 : 0));
                    return;
                case WasmOpCodes.I8x16Bitmask:
                    valueStack.Push(WasmValue.FromI32(SimdHelper.Bitmask(a)));
                    return;
                default:
                {
                    Span<byte> av = stackalloc byte[16];
                    SimdHelper.ToBytesFromVector(a, av);
                    for (var i = 0; i < 16; i++)
                        result[i] = (byte)BitOperations.PopCount(av[i]);
                    break;
                }
            }
        }
        else if (opcode is WasmOpCodes.I8x16Shl or WasmOpCodes.I8x16ShrS or WasmOpCodes.I8x16ShrU)
        {
            var shift = valueStack.UnsafePop().I32 & 7;
            var a = valueStack.UnsafePop().V128.ToVector128();
            var v = opcode switch
            {
                WasmOpCodes.I8x16Shl => Vector128.ShiftLeft(a, shift),
                WasmOpCodes.I8x16ShrS => Vector128
                    .ShiftRightArithmetic(a.AsSByte(), shift)
                    .AsByte(),
                _ => Vector128.ShiftRightLogical(a, shift),
            };
            var bytes = v.AsByte();
            for (var i = 0; i < 16; i++)
                result[i] = bytes.GetElement(i);
        }
        else if (opcode is WasmOpCodes.I8x16NarrowI16x8S or WasmOpCodes.I8x16NarrowI16x8U)
        {
            var b = valueStack.UnsafePop();
            var a = valueStack.UnsafePop();
            Span<byte> av = stackalloc byte[16];
            Span<byte> bv = stackalloc byte[16];
            a.V128.ToBytes(av);
            b.V128.ToBytes(bv);
            for (var i = 0; i < 8; i++)
                result[i] = (byte)(
                    opcode == WasmOpCodes.I8x16NarrowI16x8S
                        ? Math.Clamp(
                            BinaryPrimitives.ReadInt16LittleEndian(av[(i * 2)..]),
                            sbyte.MinValue,
                            sbyte.MaxValue
                        )
                        : Math.Clamp(
                            BinaryPrimitives.ReadInt16LittleEndian(av[(i * 2)..]),
                            byte.MinValue,
                            byte.MaxValue
                        )
                );
            for (var i = 0; i < 8; i++)
                result[8 + i] = (byte)(
                    opcode == WasmOpCodes.I8x16NarrowI16x8S
                        ? Math.Clamp(
                            BinaryPrimitives.ReadInt16LittleEndian(bv[(i * 2)..]),
                            sbyte.MinValue,
                            sbyte.MaxValue
                        )
                        : Math.Clamp(
                            BinaryPrimitives.ReadInt16LittleEndian(bv[(i * 2)..]),
                            byte.MinValue,
                            byte.MaxValue
                        )
                );
        }
        else
        {
            var b = valueStack.UnsafePop().V128.ToVector128();
            var a = valueStack.UnsafePop().V128.ToVector128();
            if (
                opcode
                is WasmOpCodes.I8x16MinS
                    or WasmOpCodes.I8x16MinU
                    or WasmOpCodes.I8x16MaxS
                    or WasmOpCodes.I8x16MaxU
            )
            {
                var v = opcode switch
                {
                    WasmOpCodes.I8x16MinS => Vector128.Min(a.AsSByte(), b.AsSByte()).AsByte(),
                    WasmOpCodes.I8x16MinU => Vector128.Min(a, b),
                    WasmOpCodes.I8x16MaxS => Vector128.Max(a.AsSByte(), b.AsSByte()).AsByte(),
                    _ => Vector128.Max(a, b),
                };
                var bytes = v.AsByte();
                for (var i = 0; i < 16; i++)
                    result[i] = bytes.GetElement(i);
            }
            else
            {
                var v = opcode switch
                {
                    WasmOpCodes.I8x16Add => Vector128.Add(a, b),
                    WasmOpCodes.I8x16AddSaturateS => Vector128Helper.AddSaturateSignedSByte(a, b),
                    WasmOpCodes.I8x16AddSaturateU => Vector128.AddSaturate(a, b),
                    WasmOpCodes.I8x16Sub => Vector128.Subtract(a, b),
                    WasmOpCodes.I8x16SubSaturateS => Vector128Helper.SubtractSaturateSignedSByte(
                        a,
                        b
                    ),
                    WasmOpCodes.I8x16SubSaturateU => Vector128.SubtractSaturate(a, b),
                    _ => SimdHelper.AverageRoundByteVector(a, b),
                };
                var bytes = v.AsByte();
                for (var i = 0; i < 16; i++)
                    result[i] = bytes.GetElement(i);
            }
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void SimdNumericI16x8(uint opcode)
    {
        SimdNumericGeneric<short, ushort>(opcode, 2, 8);
    }

    void SimdNumericI32x4(uint opcode)
    {
        SimdNumericGeneric<int, uint>(opcode, 4, 4);
    }

    void SimdNumericI64x2(uint opcode)
    {
        SimdNumericGeneric<long, ulong>(opcode, 8, 2);
    }

    void SimdNumericGeneric<TSigned, TUnsigned>(uint opcode, int laneBytes, int lanes)
        where TSigned : unmanaged, IComparable<TSigned>
        where TUnsigned : unmanaged, IComparable<TUnsigned>
    {
        Span<byte> result = stackalloc byte[16];
        if (
            opcode
            is WasmOpCodes.I16x8AllTrue
                or WasmOpCodes.I16x8Bitmask
                or WasmOpCodes.I32x4AllTrue
                or WasmOpCodes.I32x4Bitmask
                or WasmOpCodes.I64x2AllTrue
                or WasmOpCodes.I64x2Bitmask
        )
        {
            var a = valueStack.UnsafePop().V128.ToVector128();
            if (
                opcode
                is WasmOpCodes.I16x8AllTrue
                    or WasmOpCodes.I32x4AllTrue
                    or WasmOpCodes.I64x2AllTrue
            )
                valueStack.Push(WasmValue.FromI32(SimdHelper.AllTrue(a, laneBytes) ? 1 : 0));
            else
                valueStack.Push(WasmValue.FromI32(SimdHelper.Bitmask(a, laneBytes)));
            return;
        }
        else if (
            opcode
            is WasmOpCodes.I16x8ExtaddPairwiseI8x16S
                or WasmOpCodes.I16x8ExtaddPairwiseI8x16U
                or WasmOpCodes.I32x4ExtaddPairwiseI16x8S
                or WasmOpCodes.I32x4ExtaddPairwiseI16x8U
        )
        {
            var a = valueStack.UnsafePop();
            Span<byte> av = stackalloc byte[16];
            a.V128.ToBytes(av);
            if (
                opcode
                is WasmOpCodes.I16x8ExtaddPairwiseI8x16S
                    or WasmOpCodes.I16x8ExtaddPairwiseI8x16U
            )
            {
                for (var i = 0; i < 8; i++)
                {
                    var a0 = av[i * 2];
                    var a1 = av[i * 2 + 1];
                    var sum =
                        opcode == WasmOpCodes.I16x8ExtaddPairwiseI8x16S
                            ? (short)((sbyte)a0 + (sbyte)a1)
                            : (short)(a0 + a1);
                    BinaryPrimitives.WriteInt16LittleEndian(result[(i * 2)..], sum);
                }
            }
            else
            {
                for (var i = 0; i < 4; i++)
                {
                    var a0 = BinaryPrimitives.ReadInt16LittleEndian(av[(i * 4)..]);
                    var a1 = BinaryPrimitives.ReadInt16LittleEndian(av[(i * 4 + 2)..]);
                    var sum =
                        opcode == WasmOpCodes.I32x4ExtaddPairwiseI16x8S
                            ? a0 + a1
                            : (ushort)a0 + (ushort)a1;
                    BinaryPrimitives.WriteInt32LittleEndian(result[(i * 4)..], sum);
                }
            }
        }
        else if (opcode is WasmOpCodes.I16x8NarrowI32x4S or WasmOpCodes.I16x8NarrowI32x4U)
        {
            var b = valueStack.UnsafePop();
            var a = valueStack.UnsafePop();
            Span<byte> av = stackalloc byte[16];
            Span<byte> bv = stackalloc byte[16];
            a.V128.ToBytes(av);
            b.V128.ToBytes(bv);
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    result[(i * 2)..],
                    (ushort)(
                        opcode == WasmOpCodes.I16x8NarrowI32x4S
                            ? Math.Clamp(
                                BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..]),
                                short.MinValue,
                                short.MaxValue
                            )
                            : Math.Clamp(
                                BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..]),
                                ushort.MinValue,
                                ushort.MaxValue
                            )
                    )
                );
            for (var i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    result[((4 + i) * 2)..],
                    (ushort)(
                        opcode == WasmOpCodes.I16x8NarrowI32x4S
                            ? Math.Clamp(
                                BinaryPrimitives.ReadInt32LittleEndian(bv[(i * 4)..]),
                                short.MinValue,
                                short.MaxValue
                            )
                            : Math.Clamp(
                                BinaryPrimitives.ReadInt32LittleEndian(bv[(i * 4)..]),
                                ushort.MinValue,
                                ushort.MaxValue
                            )
                    )
                );
        }
        else if (
            opcode
            is WasmOpCodes.I16x8Abs
                or WasmOpCodes.I16x8Neg
                or WasmOpCodes.I32x4Abs
                or WasmOpCodes.I32x4Neg
                or WasmOpCodes.I64x2Abs
                or WasmOpCodes.I64x2Neg
        )
        {
            var a = valueStack.UnsafePop().V128.ToVector128();
            var resultVector = opcode switch
            {
                WasmOpCodes.I16x8Abs or WasmOpCodes.I32x4Abs or WasmOpCodes.I64x2Abs =>
                    SimdHelper.AbsIntVector(a, laneBytes),
                _ => SimdHelper.NegateIntVector(a, laneBytes),
            };
            var bytes = resultVector.AsByte();
            for (var i = 0; i < 16; i++)
                result[i] = bytes.GetElement(i);
        }
        else if (
            opcode
            is WasmOpCodes.I16x8Shl
                or WasmOpCodes.I16x8ShrS
                or WasmOpCodes.I16x8ShrU
                or WasmOpCodes.I32x4Shl
                or WasmOpCodes.I32x4ShrS
                or WasmOpCodes.I32x4ShrU
                or WasmOpCodes.I64x2Shl
                or WasmOpCodes.I64x2ShrS
                or WasmOpCodes.I64x2ShrU
        )
        {
            var shift = valueStack.UnsafePop().I32 & (laneBytes * 8 - 1);
            var a = valueStack.UnsafePop().V128.ToVector128();
            var v = opcode switch
            {
                WasmOpCodes.I16x8Shl or WasmOpCodes.I32x4Shl or WasmOpCodes.I64x2Shl =>
                    SimdHelper.ShiftLeftIntVector(a, laneBytes, shift),
                WasmOpCodes.I16x8ShrS or WasmOpCodes.I32x4ShrS or WasmOpCodes.I64x2ShrS =>
                    SimdHelper.ShiftRightArithmeticIntVector(a, laneBytes, shift),
                _ => SimdHelper.ShiftRightLogicalIntVector(a, laneBytes, shift),
            };
            var bytes = v.AsByte();
            for (var i = 0; i < 16; i++)
                result[i] = bytes.GetElement(i);
        }
        else if (opcode is WasmOpCodes.I16x8Q15mulrSatS)
        {
            var b = valueStack.UnsafePop();
            var a = valueStack.UnsafePop();
            Span<byte> av = stackalloc byte[16];
            Span<byte> bv = stackalloc byte[16];
            a.V128.ToBytes(av);
            b.V128.ToBytes(bv);
            for (var i = 0; i < lanes; i++)
            {
                var sx = SimdHelper.ReadSigned(av, i, laneBytes);
                var sy = SimdHelper.ReadSigned(bv, i, laneBytes);
                SimdHelper.WriteSigned(result, i, laneBytes, SimdHelper.Q15MulrSat(sx, sy));
            }
        }
        else
        {
            var b = valueStack.UnsafePop().V128.ToVector128();
            var a = valueStack.UnsafePop().V128.ToVector128();
            if (
                opcode
                is WasmOpCodes.I16x8MinS
                    or WasmOpCodes.I16x8MinU
                    or WasmOpCodes.I16x8MaxS
                    or WasmOpCodes.I16x8MaxU
                    or WasmOpCodes.I16x8Add
                    or WasmOpCodes.I16x8Sub
                    or WasmOpCodes.I16x8Mul
                    or WasmOpCodes.I16x8AvgrU
                    or WasmOpCodes.I16x8ExtMulLowI8x16S
                    or WasmOpCodes.I32x4Add
                    or WasmOpCodes.I32x4Sub
                    or WasmOpCodes.I32x4Mul
                    or WasmOpCodes.I32x4MinS
                    or WasmOpCodes.I32x4MinU
                    or WasmOpCodes.I32x4MaxS
                    or WasmOpCodes.I32x4MaxU
                    or WasmOpCodes.I32x4DotI16x8S
                    or WasmOpCodes.I32x4ExtMulLowI16x8S
                    or WasmOpCodes.I32x4ExtMulHighI16x8S
                    or WasmOpCodes.I32x4ExtMulLowI16x8U
                    or WasmOpCodes.I32x4ExtMulHighI16x8U
                    or WasmOpCodes.I64x2Add
                    or WasmOpCodes.I64x2Sub
                    or WasmOpCodes.I64x2Mul
                    or WasmOpCodes.I64x2ExtMulLowI32x4S
                    or WasmOpCodes.I64x2ExtMulHighI32x4S
                    or WasmOpCodes.I64x2ExtMulLowI32x4U
                    or WasmOpCodes.I64x2ExtMulHighI32x4U
            )
            {
                var resultVector = opcode switch
                {
                    WasmOpCodes.I16x8Add or WasmOpCodes.I32x4Add or WasmOpCodes.I64x2Add =>
                        SimdHelper.AddIntVector(a, b, laneBytes),
                    WasmOpCodes.I16x8Sub or WasmOpCodes.I32x4Sub or WasmOpCodes.I64x2Sub =>
                        SimdHelper.SubtractIntVector(a, b, laneBytes),
                    WasmOpCodes.I16x8Mul or WasmOpCodes.I32x4Mul or WasmOpCodes.I64x2Mul =>
                        SimdHelper.MultiplyIntVector(a, b, laneBytes),
                    WasmOpCodes.I16x8MinS or WasmOpCodes.I32x4MinS => SimdHelper.MinSignedIntVector(
                        a,
                        b,
                        laneBytes
                    ),
                    WasmOpCodes.I16x8MinU or WasmOpCodes.I32x4MinU =>
                        SimdHelper.MinUnsignedIntVector(a, b, laneBytes),
                    WasmOpCodes.I16x8MaxS or WasmOpCodes.I32x4MaxS => SimdHelper.MaxSignedIntVector(
                        a,
                        b,
                        laneBytes
                    ),
                    WasmOpCodes.I16x8MaxU or WasmOpCodes.I32x4MaxU =>
                        SimdHelper.MaxUnsignedIntVector(a, b, laneBytes),
                    _ => SimdHelper.AverageRoundIntVector(a, b, laneBytes),
                };
                var bytes = resultVector.AsByte();
                for (var i = 0; i < 16; i++)
                    result[i] = bytes.GetElement(i);
            }
            else
            {
                Span<byte> av = stackalloc byte[16];
                Span<byte> bv = stackalloc byte[16];
                SimdHelper.ToBytesFromVector(a, av);
                SimdHelper.ToBytesFromVector(b, bv);
                for (var i = 0; i < lanes; i++)
                {
                    var sx = SimdHelper.ReadSigned(av, i, laneBytes);
                    var sy = SimdHelper.ReadSigned(bv, i, laneBytes);
                    var ux = SimdHelper.ReadUnsigned(av, i, laneBytes);
                    var uy = SimdHelper.ReadUnsigned(bv, i, laneBytes);
                    var value = opcode switch
                    {
                        WasmOpCodes.I16x8AddSaturateS => Math.Clamp(
                            sx + sy,
                            short.MinValue,
                            short.MaxValue
                        ),
                        WasmOpCodes.I16x8AddSaturateU => (long)Math.Min(ux + uy, ushort.MaxValue),
                        WasmOpCodes.I16x8SubSaturateS => Math.Clamp(
                            sx - sy,
                            short.MinValue,
                            short.MaxValue
                        ),
                        WasmOpCodes.I16x8SubSaturateU => (long)Math.Max((long)ux - (long)uy, 0),
                        _ => sx + sy,
                    };
                    SimdHelper.WriteSigned(result, i, laneBytes, value);
                }
            }
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void SimdNumericF32x4(uint opcode)
    {
        if (
            opcode
            is WasmOpCodes.F32x4Ceil
                or WasmOpCodes.F32x4Floor
                or WasmOpCodes.F32x4Trunc
                or WasmOpCodes.F32x4Nearest
                or WasmOpCodes.F32x4Abs
                or WasmOpCodes.F32x4Neg
                or WasmOpCodes.F32x4Sqrt
        )
        {
            var a = valueStack.UnsafePop().V128.ToVector128().AsSingle();
            var result = opcode switch
            {
                WasmOpCodes.F32x4Ceil => Vector128.Ceiling(a),
                WasmOpCodes.F32x4Floor => Vector128.Floor(a),
                WasmOpCodes.F32x4Trunc => Vector128.Truncate(a),
                WasmOpCodes.F32x4Nearest => Vector128.Round(a),
                WasmOpCodes.F32x4Abs => Vector128.Abs(a),
                WasmOpCodes.F32x4Neg => Vector128.Negate(a),
                _ => Vector128.Sqrt(a),
            };
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result.AsByte())));
        }
        else
        {
            var b = valueStack.UnsafePop().V128.ToVector128().AsSingle();
            var a = valueStack.UnsafePop().V128.ToVector128().AsSingle();
            var result = opcode switch
            {
                WasmOpCodes.F32x4Add => Vector128.Add(a, b),
                WasmOpCodes.F32x4Sub => Vector128.Subtract(a, b),
                WasmOpCodes.F32x4Mul => Vector128.Multiply(a, b),
                WasmOpCodes.F32x4Div => Vector128.Divide(a, b),
                WasmOpCodes.F32x4Min => Vector128.Min(a, b),
                WasmOpCodes.F32x4Max => Vector128.Max(a, b),
                WasmOpCodes.F32x4PMin => SimdHelper.PseudoMinFloat32(a, b),
                _ => SimdHelper.PseudoMaxFloat32(a, b),
            };
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result.AsByte())));
        }
    }

    void SimdNumericF64x2(uint opcode)
    {
        if (
            opcode
            is WasmOpCodes.F64x2Ceil
                or WasmOpCodes.F64x2Floor
                or WasmOpCodes.F64x2Trunc
                or WasmOpCodes.F64x2Nearest
                or WasmOpCodes.F64x2Abs
                or WasmOpCodes.F64x2Neg
                or WasmOpCodes.F64x2Sqrt
        )
        {
            var a = valueStack.UnsafePop().V128.ToVector128().AsDouble();
            var result = opcode switch
            {
                WasmOpCodes.F64x2Ceil => Vector128.Ceiling(a),
                WasmOpCodes.F64x2Floor => Vector128.Floor(a),
                WasmOpCodes.F64x2Trunc => Vector128.Truncate(a),
                WasmOpCodes.F64x2Nearest => Vector128.Round(a),
                WasmOpCodes.F64x2Abs => Vector128.Abs(a),
                WasmOpCodes.F64x2Neg => Vector128.Negate(a),
                _ => Vector128.Sqrt(a),
            };
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result.AsByte())));
        }
        else
        {
            var b = valueStack.UnsafePop().V128.ToVector128().AsDouble();
            var a = valueStack.UnsafePop().V128.ToVector128().AsDouble();
            var result = opcode switch
            {
                WasmOpCodes.F64x2Add => Vector128.Add(a, b),
                WasmOpCodes.F64x2Sub => Vector128.Subtract(a, b),
                WasmOpCodes.F64x2Mul => Vector128.Multiply(a, b),
                WasmOpCodes.F64x2Div => Vector128.Divide(a, b),
                WasmOpCodes.F64x2Min => Vector128.Min(a, b),
                WasmOpCodes.F64x2Max => Vector128.Max(a, b),
                WasmOpCodes.F64x2PMin => SimdHelper.PseudoMinFloat64(a, b),
                _ => SimdHelper.PseudoMaxFloat64(a, b),
            };
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result.AsByte())));
        }
    }

    void ExecuteSimdConvert(uint opcode)
    {
        var a = valueStack.UnsafePop();
        Span<byte> av = stackalloc byte[16];
        Span<byte> result = stackalloc byte[16];
        a.V128.ToBytes(av);
        switch (opcode)
        {
            case WasmOpCodes.F32x4DemoteF64x2Zero:
                for (var i = 0; i < 2; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result[(i * 4)..],
                        BitConverter.SingleToUInt32Bits(
                            (float)
                                BitConverter.Int64BitsToDouble(
                                    BinaryPrimitives.ReadInt64LittleEndian(av[(i * 8)..])
                                )
                        )
                    );
                break;
            case WasmOpCodes.F64x2PromoteLowF32x4:
                for (var i = 0; i < 2; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        result[(i * 8)..],
                        BitConverter.DoubleToUInt64Bits(
                            BitConverter.Int32BitsToSingle(
                                BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..])
                            )
                        )
                    );
                break;
            case WasmOpCodes.I32x4TruncSatF32x4S:
            case WasmOpCodes.I32x4TruncSatF32x4U:
                for (var i = 0; i < 4; i++)
                {
                    var x = BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..])
                    );
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result[(i * 4)..],
                        opcode == WasmOpCodes.I32x4TruncSatF32x4S
                            ? (uint)TruncHelper.TruncSatI32S(x)
                            : TruncHelper.TruncSatI32U(x)
                    );
                }
                break;
            case WasmOpCodes.F32x4ConvertI32x4S:
            case WasmOpCodes.F32x4ConvertI32x4U:
                for (var i = 0; i < 4; i++)
                {
                    var x = BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..]);
                    var f = opcode == WasmOpCodes.F32x4ConvertI32x4S ? (float)x : (float)(uint)x;
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result[(i * 4)..],
                        BitConverter.SingleToUInt32Bits(f)
                    );
                }
                break;
            case WasmOpCodes.I32x4TruncSatF64x2SZero:
            case WasmOpCodes.I32x4TruncSatF64x2UZero:
                for (var i = 0; i < 2; i++)
                {
                    var x = BitConverter.Int64BitsToDouble(
                        BinaryPrimitives.ReadInt64LittleEndian(av[(i * 8)..])
                    );
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result[(i * 4)..],
                        opcode == WasmOpCodes.I32x4TruncSatF64x2SZero
                            ? (uint)TruncHelper.TruncSatI32S(x)
                            : TruncHelper.TruncSatI32U(x)
                    );
                }
                break;
            case WasmOpCodes.F64x2ConvertLowI32x4S:
            case WasmOpCodes.F64x2ConvertLowI32x4U:
                for (var i = 0; i < 2; i++)
                {
                    var x = BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..]);
                    var d = opcode == WasmOpCodes.F64x2ConvertLowI32x4S ? x : (double)(uint)x;
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        result[(i * 8)..],
                        BitConverter.DoubleToUInt64Bits(d)
                    );
                }
                break;
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void SimdExtend(uint opcode)
    {
        Span<byte> result = stackalloc byte[16];
        switch (opcode)
        {
            case WasmOpCodes.I16x8ExtendLowI8x16S:
            case WasmOpCodes.I16x8ExtendHighI8x16S:
            case WasmOpCodes.I16x8ExtendLowI8x16U:
            case WasmOpCodes.I16x8ExtendHighI8x16U:
            {
                var a = valueStack.UnsafePop().V128.ToVector128();
                var high =
                    opcode
                    is WasmOpCodes.I16x8ExtendHighI8x16S
                        or WasmOpCodes.I16x8ExtendHighI8x16U;
                if (opcode is WasmOpCodes.I16x8ExtendLowI8x16S or WasmOpCodes.I16x8ExtendHighI8x16S)
                {
                    var v = high
                        ? Vector128.WidenUpper(a.AsSByte())
                        : Vector128.WidenLower(a.AsSByte());
                    var bytes = v.AsByte();
                    for (var i = 0; i < 16; i++)
                        result[i] = bytes.GetElement(i);
                }
                else
                {
                    var v = high ? Vector128.WidenUpper(a) : Vector128.WidenLower(a);
                    var bytes = v.AsByte();
                    for (var i = 0; i < 16; i++)
                        result[i] = bytes.GetElement(i);
                }
                break;
            }
            case WasmOpCodes.I32x4ExtendLowI16x8S:
            case WasmOpCodes.I32x4ExtendHighI16x8S:
            case WasmOpCodes.I32x4ExtendLowI16x8U:
            case WasmOpCodes.I32x4ExtendHighI16x8U:
            {
                var a = valueStack.UnsafePop().V128.ToVector128();
                var high =
                    opcode
                    is WasmOpCodes.I32x4ExtendHighI16x8S
                        or WasmOpCodes.I32x4ExtendHighI16x8U;
                if (opcode is WasmOpCodes.I32x4ExtendLowI16x8S or WasmOpCodes.I32x4ExtendHighI16x8S)
                {
                    var v = high
                        ? Vector128.WidenUpper(a.AsInt16())
                        : Vector128.WidenLower(a.AsInt16());
                    var bytes = v.AsByte();
                    for (var i = 0; i < 16; i++)
                        result[i] = bytes.GetElement(i);
                }
                else
                {
                    var v = high
                        ? Vector128.WidenUpper(a.AsUInt16())
                        : Vector128.WidenLower(a.AsUInt16());
                    var bytes = v.AsByte();
                    for (var i = 0; i < 16; i++)
                        result[i] = bytes.GetElement(i);
                }
                break;
            }
            case WasmOpCodes.I64x2ExtendLowI32x4S:
            case WasmOpCodes.I64x2ExtendHighI32x4S:
            case WasmOpCodes.I64x2ExtendLowI32x4U:
            case WasmOpCodes.I64x2ExtendHighI32x4U:
            {
                var a = valueStack.UnsafePop().V128.ToVector128();
                var high =
                    opcode
                    is WasmOpCodes.I64x2ExtendHighI32x4S
                        or WasmOpCodes.I64x2ExtendHighI32x4U;
                if (opcode is WasmOpCodes.I64x2ExtendLowI32x4S or WasmOpCodes.I64x2ExtendHighI32x4S)
                {
                    var v = high
                        ? Vector128.WidenUpper(a.AsInt32())
                        : Vector128.WidenLower(a.AsInt32());
                    var bytes = v.AsByte();
                    for (var i = 0; i < 16; i++)
                        result[i] = bytes.GetElement(i);
                }
                else
                {
                    var v = high
                        ? Vector128.WidenUpper(a.AsUInt32())
                        : Vector128.WidenLower(a.AsUInt32());
                    var bytes = v.AsByte();
                    for (var i = 0; i < 16; i++)
                        result[i] = bytes.GetElement(i);
                }
                break;
            }
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void SimdExtMul(uint opcode, int laneBytes, int lanes)
    {
        var vb = valueStack.UnsafePop();
        var va = valueStack.UnsafePop();
        Span<byte> vav = stackalloc byte[16];
        Span<byte> vbv = stackalloc byte[16];
        va.V128.ToBytes(vav);
        vb.V128.ToBytes(vbv);
        var halfBytes = laneBytes / 2;
        var isSigned =
            opcode
            is WasmOpCodes.I16x8ExtMulLowI8x16S
                or WasmOpCodes.I16x8ExtMulHighI8x16S
                or WasmOpCodes.I32x4ExtMulLowI16x8S
                or WasmOpCodes.I32x4ExtMulHighI16x8S
                or WasmOpCodes.I64x2ExtMulLowI32x4S
                or WasmOpCodes.I64x2ExtMulHighI32x4S;
        var isHigh =
            opcode
            is WasmOpCodes.I16x8ExtMulHighI8x16S
                or WasmOpCodes.I16x8ExtMulHighI8x16U
                or WasmOpCodes.I32x4ExtMulHighI16x8S
                or WasmOpCodes.I32x4ExtMulHighI16x8U
                or WasmOpCodes.I64x2ExtMulHighI32x4S
                or WasmOpCodes.I64x2ExtMulHighI32x4U;
        var inputOffset = isHigh ? lanes * halfBytes : 0;
        Span<byte> result = stackalloc byte[16];
        for (var i = 0; i < lanes; i++)
        {
            long lva = isSigned
                ? SimdHelper.ReadSigned(vav, inputOffset / halfBytes + i, halfBytes)
                : (long)SimdHelper.ReadUnsigned(vav, inputOffset / halfBytes + i, halfBytes);
            long lvb = isSigned
                ? SimdHelper.ReadSigned(vbv, inputOffset / halfBytes + i, halfBytes)
                : (long)SimdHelper.ReadUnsigned(vbv, inputOffset / halfBytes + i, halfBytes);
            SimdHelper.WriteSigned(result, i, laneBytes, lva * lvb);
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void SimdDot()
    {
        var vb = valueStack.UnsafePop();
        var va = valueStack.UnsafePop();
        Span<byte> vav = stackalloc byte[16];
        Span<byte> vbv = stackalloc byte[16];
        va.V128.ToBytes(vav);
        vb.V128.ToBytes(vbv);
        Span<byte> result = stackalloc byte[16];
        for (var i = 0; i < 4; i++)
        {
            var a0 = (int)SimdHelper.ReadSigned(vav, i * 2, 2);
            var a1 = (int)SimdHelper.ReadSigned(vav, i * 2 + 1, 2);
            var b0 = (int)SimdHelper.ReadSigned(vbv, i * 2, 2);
            var b1 = (int)SimdHelper.ReadSigned(vbv, i * 2 + 1, 2);
            BinaryPrimitives.WriteInt32LittleEndian(result[(i * 4)..], a0 * b0 + a1 * b1);
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void ExecuteSimdRelaxed(uint opcode)
    {
        switch (opcode)
        {
            case WasmOpCodes.I8x16RelaxedSwizzle:
            {
                var s = valueStack.UnsafePop();
                var a = valueStack.UnsafePop();
                var av = a.V128.ToVector128();
                var sv = s.V128.ToVector128();
                Span<byte> result = stackalloc byte[16];
                for (var i = 0; i < 16; i++)
                {
                    var index = sv.AsByte().GetElement(i);
                    result[i] = index < 16 ? av.AsByte().GetElement((int)index) : (byte)0;
                }
                valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
                return;
            }
            case >= WasmOpCodes.I32x4RelaxedTruncF32x4S
            and <= WasmOpCodes.I32x4RelaxedTruncF64x2UZero:
                ExecuteSimdRelaxedTrunc(opcode);
                return;
            case >= WasmOpCodes.F32x4RelaxedMAdd and <= WasmOpCodes.F64x2RelaxedNMAdd:
                ExecuteSimdRelaxedMAdd(opcode);
                return;
            case >= WasmOpCodes.I8x16RelaxedLaneSelect and <= WasmOpCodes.I64x2RelaxedLaneSelect:
            {
                var c = valueStack.UnsafePop();
                var b = valueStack.UnsafePop();
                var a = valueStack.UnsafePop();
                valueStack.Push(
                    WasmValue.FromV128(
                        WasmV128Value.FromVector128(
                            Vector128.ConditionalSelect(
                                a.V128.ToVector128(),
                                c.V128.ToVector128(),
                                b.V128.ToVector128()
                            )
                        )
                    )
                );
                return;
            }
            case WasmOpCodes.F32x4RelaxedMin:
                SimdNumericF32x4(WasmOpCodes.F32x4Min);
                return;
            case WasmOpCodes.F32x4RelaxedMax:
                SimdNumericF32x4(WasmOpCodes.F32x4Max);
                return;
            case WasmOpCodes.F64x2RelaxedMin:
                SimdNumericF64x2(WasmOpCodes.F64x2Min);
                return;
            case WasmOpCodes.F64x2RelaxedMax:
                SimdNumericF64x2(WasmOpCodes.F64x2Max);
                return;
            case WasmOpCodes.I16x8RelaxedQ15MulrS:
                ExecuteSimdRelaxedQ15Mulr();
                return;
            case WasmOpCodes.I16x8RelaxedDotI8x16I7x16S:
                ExecuteSimdRelaxedDot();
                return;
            case WasmOpCodes.I32x4RelaxedDotI8x16I7x16AddS:
                ExecuteSimdRelaxedDotAdd();
                return;
        }
    }

    void ExecuteSimdRelaxedTrunc(uint opcode)
    {
        var a = valueStack.UnsafePop();
        Span<byte> av = stackalloc byte[16];
        Span<byte> result = stackalloc byte[16];
        a.V128.ToBytes(av);
        switch (opcode)
        {
            case WasmOpCodes.I32x4RelaxedTruncF32x4S:
            case WasmOpCodes.I32x4RelaxedTruncF32x4U:
                for (var i = 0; i < 4; i++)
                {
                    var x = BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(av[(i * 4)..])
                    );
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result[(i * 4)..],
                        opcode == WasmOpCodes.I32x4RelaxedTruncF32x4S
                            ? (uint)TruncHelper.TruncSatI32S(x)
                            : TruncHelper.TruncSatI32U(x)
                    );
                }
                break;
            default:
                for (var i = 0; i < 2; i++)
                {
                    var x = BitConverter.Int64BitsToDouble(
                        BinaryPrimitives.ReadInt64LittleEndian(av[(i * 8)..])
                    );
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        result[(i * 4)..],
                        opcode == WasmOpCodes.I32x4RelaxedTruncF64x2SZero
                            ? (uint)TruncHelper.TruncSatI32S(x)
                            : TruncHelper.TruncSatI32U(x)
                    );
                }
                break;
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void ExecuteSimdRelaxedMAdd(uint opcode)
    {
        var c = valueStack.UnsafePop();
        var b = valueStack.UnsafePop();
        var a = valueStack.UnsafePop();
        if (opcode is WasmOpCodes.F32x4RelaxedMAdd or WasmOpCodes.F32x4RelaxedNMAdd)
        {
            var av = a.V128.ToVector128().AsSingle();
            var bv = b.V128.ToVector128().AsSingle();
            var cv = c.V128.ToVector128().AsSingle();
            var product = Vector128.Multiply(av, bv);
            var result =
                opcode == WasmOpCodes.F32x4RelaxedNMAdd
                    ? Vector128.Add(Vector128.Negate(product), cv)
                    : Vector128.Add(product, cv);
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result.AsByte())));
        }
        else
        {
            var av = a.V128.ToVector128().AsDouble();
            var bv = b.V128.ToVector128().AsDouble();
            var cv = c.V128.ToVector128().AsDouble();
            var product = Vector128.Multiply(av, bv);
            var result =
                opcode == WasmOpCodes.F64x2RelaxedNMAdd
                    ? Vector128.Add(Vector128.Negate(product), cv)
                    : Vector128.Add(product, cv);
            valueStack.Push(WasmValue.FromV128(WasmV128Value.FromVector128(result.AsByte())));
        }
    }

    void ExecuteSimdRelaxedQ15Mulr()
    {
        var b = valueStack.UnsafePop().V128.ToVector128().AsInt16();
        var a = valueStack.UnsafePop().V128.ToVector128().AsInt16();
        var low = SimdHelper.Q15MulrVector(Vector128.WidenLower(a), Vector128.WidenLower(b));
        var high = SimdHelper.Q15MulrVector(Vector128.WidenUpper(a), Vector128.WidenUpper(b));
        valueStack.Push(
            WasmValue.FromV128(WasmV128Value.FromVector128(Vector128.Narrow(low, high).AsByte()))
        );
    }

    void ExecuteSimdRelaxedDot()
    {
        Span<int> partial = stackalloc int[8];
        SimdHelper.RelaxedDotI8x16I7x16(valueStack.UnsafePop(), valueStack.UnsafePop(), partial);
        Span<byte> result = stackalloc byte[16];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteInt16LittleEndian(result[(i * 2)..], (short)partial[i]);
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    void ExecuteSimdRelaxedDotAdd()
    {
        var c = valueStack.UnsafePop();
        var b = valueStack.UnsafePop();
        var a = valueStack.UnsafePop();
        Span<int> partial = stackalloc int[8];
        SimdHelper.RelaxedDotI8x16I7x16(b, a, partial);
        Span<byte> cv = stackalloc byte[16];
        Span<byte> result = stackalloc byte[16];
        c.V128.ToBytes(cv);
        for (var i = 0; i < 4; i++)
        {
            var sum =
                partial[i * 2]
                + partial[i * 2 + 1]
                + BinaryPrimitives.ReadInt32LittleEndian(cv[(i * 4)..]);
            BinaryPrimitives.WriteInt32LittleEndian(result[(i * 4)..], sum);
        }
        valueStack.Push(WasmValue.FromV128(WasmV128Value.FromBytes(result)));
    }

    GcStruct NewStruct(WasmModule module, uint typeIndex, bool useDefaults)
    {
        var structType = module.GetStructType(typeIndex);
        var layout = StructLayoutHelper.GetStructLayoutInfo(structType);
        var obj = new GcStruct
        {
            Module = module,
            TypeIndex = typeIndex,
            FieldCount = layout.FieldCount,
            Data = new byte[layout.DataSize],
            References = new WasmValue[layout.ReferenceCount],
        };

        var dataOffset = layout.DataSize;
        var referenceIndex = layout.ReferenceCount;
        for (var i = structType.Fields.Length - 1; i >= 0; i--)
        {
            var storageType = structType.Fields[i].StorageType;
            FieldLayout field;
            if (storageType.TryGetByteSize(out var byteSize))
            {
                field = new FieldLayout(storageType, dataOffset -= byteSize, -1);
            }
            else
            {
                field = new FieldLayout(storageType, -1, --referenceIndex);
            }
            obj.Write(field, useDefaults ? WasmValue.Default(storageType) : valueStack.UnsafePop());
        }

        return obj;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void EnsureObjectType(GcObject obj, uint typeIndex)
    {
        if (obj.TypeIndex != typeIndex)
            WasmTrapException.Throw("object type mismatch");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    GcObject PopGcObject()
    {
        var value = valueStack.UnsafePop();
        if (value.IsNullReference)
            WasmTrapException.Throw("null reference");
        return (GcObject)value.Reference!;
    }

    static void CheckArrayIndex(GcObject obj, int index)
    {
        if (obj is not GcArray)
            WasmTrapException.Throw("array operation on non-array reference");
        if ((uint)index >= (uint)((GcArray)obj).Length)
            WasmTrapException.Throw("out of bounds array access");
    }

    static void CheckArrayRange(GcObject obj, int offset, int count)
    {
        if (obj is not GcArray)
            WasmTrapException.Throw("array operation on non-array reference");
        if (offset < 0 || count < 0 || offset > ((GcArray)obj).Length - count)
            WasmTrapException.Throw("out of bounds array access");
    }

    static bool ReferenceMatches(WasmInstance instance, WasmValue value, WasmValueType type)
    {
        if (value.IsNullReference)
            return type.IsNullable || type is NoneRef or NoFuncRef or NoExternRef or NoExnRef;
        var module = instance.Module;
        return type switch
        {
            ConcreteType concrete => value.Reference is GcObject obj
                ? IsRuntimeSubtype(module, obj.TypeIndex, concrete.TypeIndex)
                : value.Reference is null
                    && FunctionReferenceMatches(instance, value, concrete.TypeIndex),
            NoneRef => false,
            I31Ref => value.Reference is null,
            NoFuncRef => false,
            FuncRef => value.Reference is null && value.Bits != (ulong)(long)FunctionAddress.Null,
            NoExternRef => false,
            ExternRef => value.Reference is ExternalReference,
            StructRef => value.Reference is GcStruct,
            ArrayRef => value.Reference is GcArray,
            EqRef => value.Reference is GcObject || value.Reference is null,
            AnyRef => value.Reference is not null
                || value.Bits != (ulong)(long)FunctionAddress.Null,
            _ => false,
        };
    }

    static bool FunctionReferenceMatches(WasmInstance instance, WasmValue value, uint typeIndex)
    {
        var functionAddress = (FunctionAddress)(long)value.Bits;
        if (functionAddress == FunctionAddress.Null)
            return false;

        var function = instance.Store.GetFunctionInstance(functionAddress);
        return FunctionTypeMatches(instance, function, typeIndex);
    }

    static bool IsRuntimeSubtype(WasmModule module, uint actualTypeIndex, uint expectedTypeIndex) =>
        WasmTypes
            .ConcreteType(actualTypeIndex, isNullable: false)
            .IsSubtypeOf(
                WasmTypes.ConcreteType(expectedTypeIndex, isNullable: false),
                module.Types.AsSpan()
            );

    static bool IsNominalRuntimeSubtype(
        WasmModule module,
        uint actualTypeIndex,
        uint expectedTypeIndex
    )
    {
        if (actualTypeIndex == expectedTypeIndex)
            return true;

        var definedTypes = module.DefinedTypes;
        if (actualTypeIndex >= definedTypes.Length)
            return false;

        foreach (var superTypeIndex in definedTypes[(int)actualTypeIndex].SuperTypes)
            if (IsNominalRuntimeSubtype(module, superTypeIndex, expectedTypeIndex))
                return true;

        return false;
    }

    static bool HasEquivalentNominalSupertype(
        WasmModule module,
        uint actualTypeIndex,
        uint expectedTypeIndex
    )
    {
        if (RuntimeTypesAreEquivalent(module, actualTypeIndex, expectedTypeIndex, []))
            return true;

        var definedTypes = module.DefinedTypes;
        if (actualTypeIndex >= definedTypes.Length)
            return false;

        foreach (var superTypeIndex in definedTypes[(int)actualTypeIndex].SuperTypes)
            if (HasEquivalentNominalSupertype(module, superTypeIndex, expectedTypeIndex))
                return true;

        return false;
    }

    static bool RuntimeTypesAreEquivalent(
        WasmModule module,
        uint leftTypeIndex,
        uint rightTypeIndex,
        HashSet<(uint Left, uint Right)> visited
    )
    {
        if (leftTypeIndex == rightTypeIndex)
            return true;

        if (!visited.Add((leftTypeIndex, rightTypeIndex)))
            return true;

        var definedTypes = module.DefinedTypes;
        if (leftTypeIndex >= definedTypes.Length || rightTypeIndex >= definedTypes.Length)
            return false;

        var left = definedTypes[(int)leftTypeIndex];
        var right = definedTypes[(int)rightTypeIndex];
        if (left.IsFinal != right.IsFinal || left.SuperTypes.Length != right.SuperTypes.Length)
            return false;

        for (var i = 0; i < left.SuperTypes.Length; i++)
            if (
                !RuntimeTypesAreEquivalent(module, left.SuperTypes[i], right.SuperTypes[i], visited)
            )
                return false;

        return RuntimeCompositeTypesAreEquivalent(left.CompositeType, right.CompositeType);
    }

    static bool RuntimeCompositeTypesAreEquivalent(CompositeType left, CompositeType right) =>
        (left.Value, right.Value) switch
        {
            (FuncType leftFunc, FuncType rightFunc) => leftFunc.Equals(rightFunc),
            (StructType leftStruct, StructType rightStruct) => RuntimeFieldsAreEquivalent(
                leftStruct.Fields.AsSpan(),
                rightStruct.Fields.AsSpan()
            ),
            (ArrayType leftArray, ArrayType rightArray) => leftArray.Field == rightArray.Field,
            _ => false,
        };

    static bool RuntimeFieldsAreEquivalent(
        ReadOnlySpan<FieldType> left,
        ReadOnlySpan<FieldType> right
    )
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    FunctionInstance ResolveFunctionReference(WasmInstance instance, uint typeIndex)
    {
        var functionAddress = (FunctionAddress)valueStack.UnsafePop().I64;
        if (functionAddress == FunctionAddress.Null)
        {
            WasmTrapException.Throw("Null function reference.");
        }

        var function = instance.Store.GetFunctionInstance(functionAddress);
        if (!FunctionTypeMatches(instance, function, typeIndex))
        {
            WasmTrapException.Throw("Function reference call type mismatch.");
        }

        return function;
    }

    static bool FunctionTypeMatches(
        WasmInstance caller,
        FunctionInstance function,
        uint expectedTypeIndex
    )
    {
        if (function is RuntimeFunction func)
        {
            if (!ReferenceEquals(func.Owner, caller))
                return GetFlatType(func.Owner.Module, func.Definition.TypeIndex)
                    == GetFlatType(caller.Module, expectedTypeIndex);

            return IsRuntimeSubtype(caller.Module, func.Definition.TypeIndex, expectedTypeIndex);
        }

        if (function is HostFunction hostFunc)
            return hostFunc.Type == GetFlatType(caller.Module, expectedTypeIndex);

        throw new InvalidOperationException("Invalid function instance type.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int CalcMemoryAddress(ulong address, uint offset, int width, int memoryLength)
    {
        var effectiveAddress = address + offset;
        WasmTrapException.ThrowIfNot(
            effectiveAddress + (uint)width <= (uint)memoryLength,
            "Memory access out of bounds"
        );
        return (int)effectiveAddress;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ulong PopMemoryAddress(MemoryInstance memory) =>
        memory.AddressType == AddressType.I64
            ? unchecked((ulong)valueStack.UnsafePop().I64)
            : (uint)valueStack.UnsafePop().I32;

    int PopPageCount(MemoryInstance memory)
    {
        if (memory.AddressType == AddressType.I32)
            return valueStack.UnsafePop().I32;

        var value = valueStack.UnsafePop().I64;
        return value is < 0 or > int.MaxValue ? -1 : (int)value;
    }

    static byte GetSIMDExtractReplaceLaneIndex(SIMDExtensionInstruction instr) =>
        instr.ExtensionCode switch
        {
            WasmOpCodes.I8x16ExtractLaneS => Unsafe
                .As<I8x16ExtractLaneSInstruction>(instr)
                .LaneIndex,
            WasmOpCodes.I8x16ExtractLaneU => Unsafe
                .As<I8x16ExtractLaneUInstruction>(instr)
                .LaneIndex,
            WasmOpCodes.I8x16ReplaceLane => Unsafe.As<I8x16ReplaceLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.I16x8ExtractLaneS => Unsafe
                .As<I16x8ExtractLaneSInstruction>(instr)
                .LaneIndex,
            WasmOpCodes.I16x8ExtractLaneU => Unsafe
                .As<I16x8ExtractLaneUInstruction>(instr)
                .LaneIndex,
            WasmOpCodes.I16x8ReplaceLane => Unsafe.As<I16x8ReplaceLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.I32x4ExtractLane => Unsafe.As<I32x4ExtractLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.I32x4ReplaceLane => Unsafe.As<I32x4ReplaceLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.I64x2ExtractLane => Unsafe.As<I64x2ExtractLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.I64x2ReplaceLane => Unsafe.As<I64x2ReplaceLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.F32x4ExtractLane => Unsafe.As<F32x4ExtractLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.F32x4ReplaceLane => Unsafe.As<F32x4ReplaceLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.F64x2ExtractLane => Unsafe.As<F64x2ExtractLaneInstruction>(instr).LaneIndex,
            WasmOpCodes.F64x2ReplaceLane => Unsafe.As<F64x2ReplaceLaneInstruction>(instr).LaneIndex,
            _ => 0,
        };

    static (uint MemoryIndex, uint Offset, int LaneIndex) GetSimdLaneMemArg(
        SIMDExtensionInstruction instruction
    ) =>
        instruction.ExtensionCode switch
        {
            WasmOpCodes.V128Load8Lane => (
                Unsafe.As<V128Load8LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Load8LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Load8LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Load16Lane => (
                Unsafe.As<V128Load16LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Load16LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Load16LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Load32Lane => (
                Unsafe.As<V128Load32LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Load32LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Load32LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Load64Lane => (
                Unsafe.As<V128Load64LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Load64LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Load64LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Store8Lane => (
                Unsafe.As<V128Store8LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Store8LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Store8LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Store16Lane => (
                Unsafe.As<V128Store16LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Store16LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Store16LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Store32Lane => (
                Unsafe.As<V128Store32LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Store32LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Store32LaneInstruction>(instruction).LaneIndex
            ),
            WasmOpCodes.V128Store64Lane => (
                Unsafe.As<V128Store64LaneInstruction>(instruction).MemoryIndex,
                Unsafe.As<V128Store64LaneInstruction>(instruction).Offset,
                Unsafe.As<V128Store64LaneInstruction>(instruction).LaneIndex
            ),
            _ => throw new InvalidOperationException("Invalid SIMD lane memory instruction."),
        };

    static int SimdLaneByteWidth(uint opcode) =>
        opcode switch
        {
            WasmOpCodes.V128Load8Lane or WasmOpCodes.V128Store8Lane => 1,
            WasmOpCodes.V128Load16Lane or WasmOpCodes.V128Store16Lane => 2,
            WasmOpCodes.V128Load32Lane or WasmOpCodes.V128Store32Lane => 4,
            WasmOpCodes.V128Load64Lane or WasmOpCodes.V128Store64Lane => 8,
            _ => throw new InvalidOperationException("Invalid SIMD lane memory instruction."),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int CalcTableAddress(int address, int length, int tableLength)
    {
        WasmTrapException.ThrowIfNot(
            address >= 0 && length >= 0 && (ulong)(uint)address + (uint)length <= (uint)tableLength,
            "Table access out of bounds"
        );
        return address;
    }

    int PopTableIndex(TableInstance table)
    {
        if (table.AddressType == AddressType.I32)
            return valueStack.UnsafePop().I32;

        var value = valueStack.UnsafePop().I64;
        return value is < 0 or > int.MaxValue ? -1 : (int)value;
    }

    void PushTableIndex(TableInstance table, int value)
    {
        if (table.AddressType == AddressType.I64)
            valueStack.Push(WasmValue.FromI64(value));
        else
            valueStack.Push(WasmValue.FromI32(value));
    }

    static void ValidateTableReference(
        WasmInstance instance,
        in TableInstance table,
        WasmValue value
    )
    {
        if (table.ElementType != WasmTypes.FuncRef(true))
            return;

        var functionAddress = (FunctionAddress)(long)value.Bits;
        if (
            functionAddress != FunctionAddress.Null
            && (functionAddress < 0 || instance.Store.FunctionCount <= functionAddress)
        )
            WasmTrapException.Throw($"Invalid function index: {functionAddress}");
    }

    void BranchToLabel(int labelIndex, ref int localBase, ref int ip)
    {
        var target = controlStack.UnsafeGet(controlStack.Count - 1 - labelIndex);
        switch (target.Kind)
        {
            case ControlFrameKind.Block:
            case ControlFrameKind.If:
            case ControlFrameKind.Try:
            case ControlFrameKind.TryTable:
            {
                PreserveBranchResults(target);
                for (var i = 0; i < labelIndex + 1; i++)
                    controlStack.Pop();
                localBase = target.LocalBase;
                ip = target.EndCodeOffset + 1;
                break;
            }
            case ControlFrameKind.Loop:
            {
                if (target.ParameterCount == 0)
                {
                    if (valueStack.Count != target.StackBase)
                        valueStack.Truncate(target.StackBase);
                }
                else
                {
                    PreserveBranchResults(target);
                }
                for (var i = 0; i < labelIndex; i++)
                    controlStack.Pop();
                ip = target.StartCodeOffset;
                break;
            }
        }
    }

    bool HandleWasmException(
        ImmutableArray<Instruction> instructions,
        WasmInstance instance,
        ref int ip,
        WasmThrownException exception,
        ref int localBase,
        int controlBase
    )
    {
        for (var labelIndex = 0; labelIndex < controlStack.Count - controlBase; labelIndex++)
        {
            var frame = controlStack.AsSpan()[controlStack.Count - 1 - labelIndex];
            if (frame.Kind == ControlFrameKind.TryTable)
            {
                if (
                    !TryFindCatchTable(
                        instructions,
                        instance,
                        frame.StartCodeOffset - 1,
                        exception.TagAddress,
                        out var catchLabelIndex,
                        out var pushExceptionRef
                    )
                )
                    continue;

                valueStack.Truncate(frame.StackBase);
                valueStack.PushRange(exception.Arguments);
                if (pushExceptionRef)
                    valueStack.Push(CreateExceptionRef(exception));
                localBase = frame.LocalBase;
                for (var i = 0; i < labelIndex + 1; i++)
                    controlStack.Pop();
                BranchToLabel(catchLabelIndex, ref localBase, ref ip);
                currentException = exception;
                return true;
            }

            if (frame.Kind != ControlFrameKind.Try)
                continue;

            if (
                !TryFindCatch(
                    instructions,
                    instance,
                    frame.StartCodeOffset,
                    frame.EndCodeOffset,
                    exception.TagAddress,
                    out var handlerIndex
                )
            )
                continue;

            valueStack.Truncate(frame.StackBase);
            valueStack.PushRange(exception.Arguments);
            localBase = frame.LocalBase;
            for (var i = 0; i < labelIndex + 1; i++)
                controlStack.Pop();
            controlStack.Push(
                new ControlFrame
                {
                    Kind = ControlFrameKind.Block,
                    StartCodeOffset = handlerIndex + 1,
                    EndCodeOffset = frame.EndCodeOffset,
                    LocalBase = frame.LocalBase,
                    StackBase = frame.StackBase,
                    ParameterCount = frame.ParameterCount,
                    ResultCount = frame.ResultCount,
                }
            );
            ip = handlerIndex + 1;
            currentException = exception;
            return true;
        }

        return false;
    }

    static bool TryFindCatch(
        ImmutableArray<Instruction> instructions,
        WasmInstance instance,
        int startIndex,
        int endIndex,
        TagAddress thrownTag,
        out int handlerIndex
    )
    {
        var depth = 1;
        for (var i = startIndex; i <= endIndex; i++)
        {
            var instr = instructions[i];
            switch (instr.OpCode)
            {
                case WasmOpCodes.Block:
                case WasmOpCodes.Loop:
                case WasmOpCodes.If:
                case WasmOpCodes.Try:
                case WasmOpCodes.TryTable:
                    depth++;
                    break;
                case WasmOpCodes.Catch when depth == 1:
                {
                    var catchInstr = Unsafe.As<CatchInstruction>(instr);
                    if (thrownTag == instance.GetTagAddress((int)catchInstr.TagIndex))
                    {
                        handlerIndex = i;
                        return true;
                    }
                    break;
                }
                case WasmOpCodes.CatchAll when depth == 1:
                    handlerIndex = i;
                    return true;
                case WasmOpCodes.End:
                    depth--;
                    break;
            }
        }

        handlerIndex = 0;
        return false;
    }

    static bool TryFindCatchTable(
        ImmutableArray<Instruction> instructions,
        WasmInstance instance,
        int tryTableIndex,
        TagAddress thrownTag,
        out int labelIndex,
        out bool pushExceptionRef
    )
    {
        var tryInstr = Unsafe.As<TryTableInstruction>(instructions[tryTableIndex]);
        foreach (var clause in tryInstr.CatchTable)
        {
            switch (clause.Kind)
            {
                case 0:
                case 1:
                {
                    if (thrownTag == instance.GetTagAddress((int)clause.TagIndex))
                    {
                        labelIndex = (int)clause.LabelIndex;
                        pushExceptionRef = clause.Kind == 1;
                        return true;
                    }
                    break;
                }
                case 2:
                case 3:
                    labelIndex = (int)clause.LabelIndex;
                    pushExceptionRef = clause.Kind == 3;
                    return true;
                default:
                    WasmTrapException.Throw("Invalid catch clause.");
                    break;
            }
        }

        labelIndex = 0;
        pushExceptionRef = false;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    WasmValue CreateExceptionRef(WasmThrownException exception)
    {
        exceptionRefs.Add(exception);
        return WasmValue.FromRaw((uint)exceptionRefs.Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PreserveBranchResults(in ControlFrame target)
    {
        var valueCount =
            target.Kind == ControlFrameKind.Loop ? target.ParameterCount : target.ResultCount;
        PreserveValues(target.StackBase, valueCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void PreserveValues(int stackBase, int valueCount)
    {
        if (valueCount == 0)
        {
            valueStack.Truncate(stackBase);
            return;
        }

        var buffer = ArrayPool<WasmValue>.Shared.Rent(valueCount);
        try
        {
            var results = buffer.AsSpan(0, valueCount);
            valueStack.Take(valueCount).CopyTo(results);
            valueStack.Truncate(stackBase);
            valueStack.PushRange(results);
        }
        finally
        {
            ArrayPool<WasmValue>.Shared.Return(buffer, clearArray: true);
        }
    }

    static FuncType GetFlatType(WasmModule module, uint typeIndex)
    {
        var remaining = typeIndex;
        foreach (var rt in module.Types.AsSpan())
        {
            if (remaining >= rt.SubTypes.Length)
            {
                remaining -= checked((uint)rt.SubTypes.Length);
                continue;
            }
            var subType = rt.SubTypes[checked((int)remaining)];
            if (subType.CompositeType is FuncType ft)
                return ft;
            WasmTrapException.Throw("Expected function type.");
            return default;
        }
        WasmTrapException.Throw($"Type index {typeIndex} is out of bounds.");
        return default;
    }
}
