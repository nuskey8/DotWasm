using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DotWasm.Models;

namespace DotWasm.Runtime;

internal sealed partial class WasmExecutionContext
{
    delegate WasmOpResult WasmCompiledExpressionEntry(WasmExecutionContext context, ref WasmExecutionFrame frame);

    sealed class WasmCompiledExpression(Type? code, WasmCompiledExpressionEntry? entryPoint)
    {
        private static readonly ConditionalWeakTable<Expression, WasmCompiledExpression> cache = new();

        public static readonly WasmCompiledExpression Unsupported = new(null, null);

        public Type? Code => code;
        public WasmCompiledExpressionEntry? EntryPoint => entryPoint;
        public bool IsCompiled => EntryPoint is not null;

        public static WasmCompiledExpression GetOrCompile(Expression expression) =>
            cache.GetValue(expression, static expression => Compile(expression.Instructions));

        public WasmOpResult Invoke(WasmExecutionContext context, ref WasmExecutionFrame frame) => EntryPoint!(context, ref frame);

        static WasmCompiledExpression Compile(ImmutableArray<Instruction> instructions)
        {
            if (!RuntimeFeature.IsDynamicCodeCompiled)
            {
                return Unsupported;
            }

            if (instructions.IsDefaultOrEmpty)
                return Create(typeof(WasmStop));

            var compiler = new WasmExpressionCompiler(instructions);
            var endExclusive =
                instructions[^1].OpCode == WasmOpCodes.End
                    ? instructions.Length - 1
                    : instructions.Length;
            var code = compiler.EmitSequence(0, endExclusive);
            return code is null ? Unsupported : Create(code);
        }

        static WasmCompiledExpression Create(Type code)
        {
            if (!code.IsAssignableTo(typeof(IWasmCompiledOp)))
                throw new InvalidProgramException();

            var entryPoint = (WasmCompiledExpressionEntry)Delegate.CreateDelegate(
                typeof(WasmCompiledExpressionEntry),
                code.GetMethod(nameof(IWasmCompiledOp.Run), BindingFlags.Public | BindingFlags.Static)!
            );
            return new WasmCompiledExpression(code, entryPoint);
        }
    }

    sealed class WasmExpressionCompiler(ImmutableArray<Instruction> instructions)
    {
        public Type? EmitSequence(int start, int endExclusive)
        {
            var nodes = new List<Func<Type, Type>>();

            for (var i = start; i < endExclusive;)
            {
                var instr = instructions[i];
                switch (instr.OpCode)
                {
                    case WasmOpCodes.Block:
                    {
                        var block = Unsafe.As<BlockInstruction>(instr);
                        var body = EmitSequence(i + 1, block.EndIndex);
                        if (body is null)
                            return null;
                        nodes.Add(next =>
                            typeof(WasmBlock<,,,>).MakeGenericType(
                                TypeLiteralFactory.CreateInt32(block.ParameterCount),
                                TypeLiteralFactory.CreateInt32(block.ResultCount),
                                body,
                                next
                            )
                        );
                        i = block.EndIndex + 1;
                        break;
                    }
                    case WasmOpCodes.Loop:
                    {
                        var loop = Unsafe.As<LoopInstruction>(instr);
                        var body = EmitSequence(i + 1, loop.EndIndex);
                        if (body is null)
                            return null;
                        nodes.Add(next =>
                            typeof(WasmLoop<,,,>).MakeGenericType(
                                TypeLiteralFactory.CreateInt32(loop.ParameterCount),
                                TypeLiteralFactory.CreateInt32(loop.ResultCount),
                                body,
                                next
                            )
                        );
                        i = loop.EndIndex + 1;
                        break;
                    }
                    case WasmOpCodes.If:
                    {
                        var ifInstr = Unsafe.As<IfInstruction>(instr);
                        var thenEnd = ifInstr.ElseIndex >= 0 ? ifInstr.ElseIndex : ifInstr.EndIndex;
                        var thenBody = EmitSequence(i + 1, thenEnd);
                        var elseBody =
                            ifInstr.ElseIndex >= 0
                                ? EmitSequence(ifInstr.ElseIndex + 1, ifInstr.EndIndex)
                                : typeof(WasmStop);
                        if (thenBody is null || elseBody is null)
                            return null;
                        nodes.Add(next =>
                            typeof(WasmIf<,,,,>).MakeGenericType(
                                TypeLiteralFactory.CreateInt32(ifInstr.ParameterCount),
                                TypeLiteralFactory.CreateInt32(ifInstr.ResultCount),
                                thenBody,
                                elseBody,
                                next
                            )
                        );
                        i = ifInstr.EndIndex + 1;
                        break;
                    }
                    case WasmOpCodes.Try:
                    {
                        var tryInstr = Unsafe.As<TryInstruction>(instr);
                        var handlerIndices = FindTopLevelHandlers(i + 1, tryInstr.EndIndex);
                        var bodyEnd =
                            handlerIndices.IsDefaultOrEmpty
                                ? tryInstr.EndIndex
                                : handlerIndices[0];
                        var body = EmitSequence(i + 1, bodyEnd);
                        var catches = EmitCatchHandlers(handlerIndices, tryInstr.EndIndex);
                        if (body is null || catches is null)
                            return null;
                        nodes.Add(next =>
                            typeof(WasmTry<,,,,>).MakeGenericType(
                                TypeLiteralFactory.CreateInt32(tryInstr.ParameterCount),
                                TypeLiteralFactory.CreateInt32(tryInstr.ResultCount),
                                body,
                                catches,
                                next
                            )
                        );
                        i = tryInstr.EndIndex + 1;
                        break;
                    }
                    case WasmOpCodes.TryTable:
                    {
                        var tryTable = Unsafe.As<TryTableInstruction>(instr);
                        var body = EmitSequence(i + 1, tryTable.EndIndex);
                        if (body is null)
                            return null;
                        nodes.Add(next =>
                            typeof(WasmTryTable<,,,,>).MakeGenericType(
                                TypeLiteralFactory.CreateInt32(tryTable.ParameterCount),
                                TypeLiteralFactory.CreateInt32(tryTable.ResultCount),
                                body,
                                EmitTryTableHandlers(tryTable.CatchTable),
                                next
                            )
                        );
                        i = tryTable.EndIndex + 1;
                        break;
                    }
                    case WasmOpCodes.Catch:
                    case WasmOpCodes.CatchAll:
                        return null;
                    case WasmOpCodes.Delegate:
                    case WasmOpCodes.End:
                    case WasmOpCodes.Else:
                        i++;
                        break;
                    default:
                    {
                        var node = EmitInstruction(instr);
                        if (node is null)
                            return null;
                        nodes.Add(node);
                        i++;
                        break;
                    }
                }
            }

            var code = typeof(WasmStop);
            for (var i = nodes.Count - 1; i >= 0; i--)
                code = nodes[i](code);
            return code;
        }

        ImmutableArray<int> FindTopLevelHandlers(int start, int endExclusive)
        {
            var handlers = ImmutableArray.CreateBuilder<int>();
            var depth = 0;
            for (var i = start; i < endExclusive; i++)
            {
                switch (instructions[i].OpCode)
                {
                    case WasmOpCodes.Block:
                    case WasmOpCodes.Loop:
                    case WasmOpCodes.If:
                    case WasmOpCodes.Try:
                    case WasmOpCodes.TryTable:
                        depth++;
                        break;
                    case WasmOpCodes.End when depth > 0:
                    case WasmOpCodes.Delegate when depth > 0:
                        depth--;
                        break;
                    case WasmOpCodes.Catch when depth == 0:
                    case WasmOpCodes.CatchAll when depth == 0:
                        handlers.Add(i);
                        break;
                }
            }

            return handlers.ToImmutable();
        }

        Type? EmitCatchHandlers(ImmutableArray<int> handlerIndices, int tryEndIndex)
        {
            var handlerType = typeof(WasmNoCatch);
            for (var i = handlerIndices.Length - 1; i >= 0; i--)
            {
                var handlerIndex = handlerIndices[i];
                var bodyStart = handlerIndex + 1;
                var bodyEnd = i == handlerIndices.Length - 1 ? tryEndIndex : handlerIndices[i + 1];
                var body = EmitSequence(bodyStart, bodyEnd);
                if (body is null)
                    return null;

                handlerType =
                    instructions[handlerIndex].OpCode == WasmOpCodes.Catch
                        ? typeof(WasmCatch<,,>).MakeGenericType(
                            TypeLiteralFactory.CreateInt32(
                                (int)Unsafe.As<CatchInstruction>(instructions[handlerIndex]).TagIndex
                            ),
                            body,
                            handlerType
                        )
                        : typeof(WasmCatchAll<,>).MakeGenericType(body, handlerType);
            }

            return handlerType;
        }

        static Type EmitTryTableHandlers(ReadOnlySpan<CatchClause> clauses)
        {
            var handlerType = typeof(WasmNoTryTableCatch);
            for (var i = clauses.Length - 1; i >= 0; i--)
            {
                var clause = clauses[i];
                handlerType = typeof(WasmTryTableCatch<,,,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32(clause.Kind),
                    TypeLiteralFactory.CreateInt32((int)clause.TagIndex),
                    TypeLiteralFactory.CreateInt32((int)clause.LabelIndex),
                    handlerType
                );
            }

            return handlerType;
        }

        static Func<Type, Type>? EmitInstruction(Instruction instr) =>
            instr.OpCode switch
            {
                WasmOpCodes.Unreachable => next => typeof(WasmUnreachable<>).MakeGenericType(next),
                WasmOpCodes.Nop => next => typeof(WasmNop<>).MakeGenericType(next),
                WasmOpCodes.Br => next => typeof(WasmBr<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<BrInstruction>(instr).LabelIndex),
                    next
                ),
                WasmOpCodes.BrIf => next => typeof(WasmBrIf<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<BrIfInstruction>(instr).LabelIndex),
                    next
                ),
                WasmOpCodes.BrTable => next => typeof(WasmBrTable<,>).MakeGenericType(
                    BranchTableTypeFactory.Create(Unsafe.As<BrTableInstruction>(instr)),
                    next
                ),
                WasmOpCodes.Throw => next => typeof(WasmThrow<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<ThrowInstruction>(instr).TagIndex),
                    next
                ),
                WasmOpCodes.Rethrow => next => typeof(WasmRethrow<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<RethrowInstruction>(instr).LabelIndex),
                    next
                ),
                WasmOpCodes.ThrowRef => next => typeof(WasmThrowRef<>).MakeGenericType(next),
                WasmOpCodes.Return => next => typeof(WasmReturn<>).MakeGenericType(next),
                WasmOpCodes.Call => next => typeof(WasmCall<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<CallInstruction>(instr).FunctionIndex),
                    next
                ),
                WasmOpCodes.CallIndirect => next => typeof(WasmCallIndirect<,,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<CallIndirectInstruction>(instr).TypeIndex),
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<CallIndirectInstruction>(instr).TableIndex),
                    next
                ),
                WasmOpCodes.ReturnCall => next => typeof(WasmReturnCall<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<ReturnCallInstruction>(instr).FunctionIndex),
                    next
                ),
                WasmOpCodes.ReturnCallIndirect => next =>
                    typeof(WasmReturnCallIndirect<,,>).MakeGenericType(
                        TypeLiteralFactory.CreateInt32((int)Unsafe.As<ReturnCallIndirectInstruction>(instr).TypeIndex),
                        TypeLiteralFactory.CreateInt32((int)Unsafe.As<ReturnCallIndirectInstruction>(instr).TableIndex),
                        next
                    ),
                WasmOpCodes.CallRef => next => typeof(WasmCallRef<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<CallRefInstruction>(instr).TypeIndex),
                    next
                ),
                WasmOpCodes.ReturnCallRef => next => typeof(WasmReturnCallRef<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<ReturnCallRefInstruction>(instr).TypeIndex),
                    next
                ),
                WasmOpCodes.Drop => next => typeof(WasmDrop<>).MakeGenericType(next),
                WasmOpCodes.Select => next => typeof(WasmSelect<>).MakeGenericType(next),
                WasmOpCodes.SelectT => next => typeof(WasmSelect<>).MakeGenericType(next),
                WasmOpCodes.LocalGet => next => typeof(WasmLocalGet<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<LocalGetInstruction>(instr).LocalIndex),
                    next
                ),
                WasmOpCodes.LocalSet => next => typeof(WasmLocalSet<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<LocalSetInstruction>(instr).LocalIndex),
                    next
                ),
                WasmOpCodes.LocalTee => next => typeof(WasmLocalTee<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<LocalTeeInstruction>(instr).LocalIndex),
                    next
                ),
                WasmOpCodes.GlobalGet => next => typeof(WasmGlobalGet<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<GlobalGetInstruction>(instr).GlobalIndex),
                    next
                ),
                WasmOpCodes.GlobalSet => next => typeof(WasmGlobalSet<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<GlobalSetInstruction>(instr).GlobalIndex),
                    next
                ),
                WasmOpCodes.TableGet => next => typeof(WasmTableGet<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<TableGetInstruction>(instr).TableIndex),
                    next
                ),
                WasmOpCodes.TableSet => next => typeof(WasmTableSet<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<TableSetInstruction>(instr).TableIndex),
                    next
                ),
                WasmOpCodes.I32Load => next => EmitLoad<int, int>(
                    Unsafe.As<I32LoadInstruction>(instr).MemoryIndex,
                    Unsafe.As<I32LoadInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load => next => EmitLoad<long, long>(
                    Unsafe.As<I64LoadInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64LoadInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.F32Load => next => EmitLoad<float, float>(
                    Unsafe.As<F32LoadInstruction>(instr).MemoryIndex,
                    Unsafe.As<F32LoadInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.F64Load => next => EmitLoad<double, double>(
                    Unsafe.As<F64LoadInstruction>(instr).MemoryIndex,
                    Unsafe.As<F64LoadInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Load8S => next => EmitLoad<sbyte, int>(
                    Unsafe.As<I32Load8SInstruction>(instr).MemoryIndex,
                    Unsafe.As<I32Load8SInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Load8U => next => EmitLoad<byte, int>(
                    Unsafe.As<I32Load8UInstruction>(instr).MemoryIndex,
                    Unsafe.As<I32Load8UInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Load16S => next => EmitLoad<short, int>(
                    Unsafe.As<I32Load16SInstruction>(instr).MemoryIndex,
                    Unsafe.As<I32Load16SInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Load16U => next => EmitLoad<ushort, int>(
                    Unsafe.As<I32Load16UInstruction>(instr).MemoryIndex,
                    Unsafe.As<I32Load16UInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load8S => next => EmitLoad<sbyte, long>(
                    Unsafe.As<I64Load8SInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64Load8SInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load8U => next => EmitLoad<byte, long>(
                    Unsafe.As<I64Load8UInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64Load8UInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load16S => next => EmitLoad<short, long>(
                    Unsafe.As<I64Load16SInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64Load16SInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load16U => next => EmitLoad<ushort, long>(
                    Unsafe.As<I64Load16UInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64Load16UInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load32S => next => EmitLoad<int, long>(
                    Unsafe.As<I64Load32SInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64Load32SInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Load32U => next => EmitLoad<uint, long>(
                    Unsafe.As<I64Load32UInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64Load32UInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Store => next => EmitStore<int, int>(
                    Unsafe.As<I32StoreInstruction>(instr).MemoryIndex,
                    Unsafe.As<I32StoreInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Store => next => EmitStore<long, long>(
                    Unsafe.As<I64StoreInstruction>(instr).MemoryIndex,
                    Unsafe.As<I64StoreInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.F32Store => next => EmitStore<float, float>(
                    Unsafe.As<F32StoreInstruction>(instr).MemoryIndex,
                    Unsafe.As<F32StoreInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.F64Store => next => EmitStore<double, double>(
                    Unsafe.As<F64StoreInstruction>(instr).MemoryIndex,
                    Unsafe.As<F64StoreInstruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Store8 => next => EmitStore<int, byte>(
                    Unsafe.As<I32Store8Instruction>(instr).MemoryIndex,
                    Unsafe.As<I32Store8Instruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I32Store16 => next => EmitStore<int, ushort>(
                    Unsafe.As<I32Store16Instruction>(instr).MemoryIndex,
                    Unsafe.As<I32Store16Instruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Store8 => next => EmitStore<long, byte>(
                    Unsafe.As<I64Store8Instruction>(instr).MemoryIndex,
                    Unsafe.As<I64Store8Instruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Store16 => next => EmitStore<long, ushort>(
                    Unsafe.As<I64Store16Instruction>(instr).MemoryIndex,
                    Unsafe.As<I64Store16Instruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.I64Store32 => next => EmitStore<long, uint>(
                    Unsafe.As<I64Store32Instruction>(instr).MemoryIndex,
                    Unsafe.As<I64Store32Instruction>(instr).Offset,
                    next
                ),
                WasmOpCodes.MemorySize => next => typeof(WasmMemorySize<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<MemorySizeInstruction>(instr).MemoryIndex),
                    next
                ),
                WasmOpCodes.MemoryGrow => next => typeof(WasmMemoryGrow<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<MemoryGrowInstruction>(instr).MemoryIndex),
                    next
                ),
                WasmOpCodes.I32Const => next => EmitConst<int>(
                    TypeLiteralFactory.CreateInt32(Unsafe.As<I32ConstInstruction>(instr).Value),
                    next
                ),
                WasmOpCodes.I64Const => next => EmitConst<long>(
                    TypeLiteralFactory.CreateInt64(Unsafe.As<I64ConstInstruction>(instr).Value),
                    next
                ),
                WasmOpCodes.F32Const => next => EmitConst<float>(
                    TypeLiteralFactory.CreateFloat32(Unsafe.As<F32ConstInstruction>(instr).Value),
                    next
                ),
                WasmOpCodes.F64Const => next => EmitConst<double>(
                    TypeLiteralFactory.CreateFloat64(Unsafe.As<F64ConstInstruction>(instr).Value),
                    next
                ),
                WasmOpCodes.I32Eqz => EmitUnary<int, int, Eqz<int>>,
                WasmOpCodes.I32Eq => EmitCompare<int, Eq<int>>,
                WasmOpCodes.I32Ne => EmitCompare<int, Ne<int>>,
                WasmOpCodes.I32LtS => EmitCompare<int, Lt<int>>,
                WasmOpCodes.I32LtU => EmitCompare<int, I32LtU>,
                WasmOpCodes.I32GtS => EmitCompare<int, Gt<int>>,
                WasmOpCodes.I32GtU => EmitCompare<int, I32GtU>,
                WasmOpCodes.I32LeS => EmitCompare<int, Le<int>>,
                WasmOpCodes.I32LeU => EmitCompare<int, I32LeU>,
                WasmOpCodes.I32GeS => EmitCompare<int, Ge<int>>,
                WasmOpCodes.I32GeU => EmitCompare<int, I32GeU>,
                WasmOpCodes.I64Eqz => EmitUnary<long, int, Eqz<long>>,
                WasmOpCodes.I64Eq => EmitCompare<long, Eq<long>>,
                WasmOpCodes.I64Ne => EmitCompare<long, Ne<long>>,
                WasmOpCodes.I64LtS => EmitCompare<long, Lt<long>>,
                WasmOpCodes.I64LtU => EmitCompare<long, I64LtU>,
                WasmOpCodes.I64GtS => EmitCompare<long, Gt<long>>,
                WasmOpCodes.I64GtU => EmitCompare<long, I64GtU>,
                WasmOpCodes.I64LeS => EmitCompare<long, Le<long>>,
                WasmOpCodes.I64LeU => EmitCompare<long, I64LeU>,
                WasmOpCodes.I64GeS => EmitCompare<long, Ge<long>>,
                WasmOpCodes.I64GeU => EmitCompare<long, I64GeU>,
                WasmOpCodes.F32Eq => EmitCompare<float, Eq<float>>,
                WasmOpCodes.F32Ne => EmitCompare<float, Ne<float>>,
                WasmOpCodes.F32Lt => EmitCompare<float, Lt<float>>,
                WasmOpCodes.F32Gt => EmitCompare<float, Gt<float>>,
                WasmOpCodes.F32Le => EmitCompare<float, Le<float>>,
                WasmOpCodes.F32Ge => EmitCompare<float, Ge<float>>,
                WasmOpCodes.F64Eq => EmitCompare<double, Eq<double>>,
                WasmOpCodes.F64Ne => EmitCompare<double, Ne<double>>,
                WasmOpCodes.F64Lt => EmitCompare<double, Lt<double>>,
                WasmOpCodes.F64Gt => EmitCompare<double, Gt<double>>,
                WasmOpCodes.F64Le => EmitCompare<double, Le<double>>,
                WasmOpCodes.F64Ge => EmitCompare<double, Ge<double>>,
                WasmOpCodes.I32Clz => EmitUnary<int, int, I32Clz>,
                WasmOpCodes.I32Ctz => EmitUnary<int, int, I32Ctz>,
                WasmOpCodes.I32Popcnt => EmitUnary<int, int, I32Popcnt>,
                WasmOpCodes.I32Add => EmitBinary<int, int, Add<int>>,
                WasmOpCodes.I32Sub => EmitBinary<int, int, Sub<int>>,
                WasmOpCodes.I32Mul => EmitBinary<int, int, Mul<int>>,
                WasmOpCodes.I32DivS => EmitBinary<int, int, I32DivS>,
                WasmOpCodes.I32DivU => EmitBinary<int, int, I32DivU>,
                WasmOpCodes.I32RemS => EmitBinary<int, int, I32RemS>,
                WasmOpCodes.I32RemU => EmitBinary<int, int, I32RemU>,
                WasmOpCodes.I32And => EmitBinary<int, int, BitAnd<int>>,
                WasmOpCodes.I32Or => EmitBinary<int, int, BitOr<int>>,
                WasmOpCodes.I32Xor => EmitBinary<int, int, BitXor<int>>,
                WasmOpCodes.I32Shl => EmitBinary<int, int, I32Shl>,
                WasmOpCodes.I32ShrS => EmitBinary<int, int, I32ShrS>,
                WasmOpCodes.I32ShrU => EmitBinary<int, int, I32ShrU>,
                WasmOpCodes.I32Rotl => EmitBinary<int, int, I32Rotl>,
                WasmOpCodes.I32Rotr => EmitBinary<int, int, I32Rotr>,
                WasmOpCodes.I64Clz => EmitUnary<long, long, I64Clz>,
                WasmOpCodes.I64Ctz => EmitUnary<long, long, I64Ctz>,
                WasmOpCodes.I64Popcnt => EmitUnary<long, long, I64Popcnt>,
                WasmOpCodes.I64Add => EmitBinary<long, long, Add<long>>,
                WasmOpCodes.I64Sub => EmitBinary<long, long, Sub<long>>,
                WasmOpCodes.I64Mul => EmitBinary<long, long, Mul<long>>,
                WasmOpCodes.I64DivS => EmitBinary<long, long, I64DivS>,
                WasmOpCodes.I64DivU => EmitBinary<long, long, I64DivU>,
                WasmOpCodes.I64RemS => EmitBinary<long, long, I64RemS>,
                WasmOpCodes.I64RemU => EmitBinary<long, long, I64RemU>,
                WasmOpCodes.I64And => EmitBinary<long, long, BitAnd<long>>,
                WasmOpCodes.I64Or => EmitBinary<long, long, BitOr<long>>,
                WasmOpCodes.I64Xor => EmitBinary<long, long, BitXor<long>>,
                WasmOpCodes.I64Shl => EmitBinary<long, long, I64Shl>,
                WasmOpCodes.I64ShrS => EmitBinary<long, long, I64ShrS>,
                WasmOpCodes.I64ShrU => EmitBinary<long, long, I64ShrU>,
                WasmOpCodes.I64Rotl => EmitBinary<long, long, I64Rotl>,
                WasmOpCodes.I64Rotr => EmitBinary<long, long, I64Rotr>,
                WasmOpCodes.F32Abs => EmitUnary<float, float, Abs<float>>,
                WasmOpCodes.F32Neg => EmitUnary<float, float, Neg<float>>,
                WasmOpCodes.F32Ceil => EmitUnary<float, float, Ceil<float>>,
                WasmOpCodes.F32Floor => EmitUnary<float, float, Floor<float>>,
                WasmOpCodes.F32Trunc => EmitUnary<float, float, Trunc<float>>,
                WasmOpCodes.F32Nearest => EmitUnary<float, float, Nearest<float>>,
                WasmOpCodes.F32Sqrt => EmitUnary<float, float, Sqrt<float>>,
                WasmOpCodes.F32Add => EmitBinary<float, float, Add<float>>,
                WasmOpCodes.F32Sub => EmitBinary<float, float, Sub<float>>,
                WasmOpCodes.F32Mul => EmitBinary<float, float, Mul<float>>,
                WasmOpCodes.F32Div => EmitBinary<float, float, Div<float>>,
                WasmOpCodes.F32Min => EmitBinary<float, float, Min<float>>,
                WasmOpCodes.F32Max => EmitBinary<float, float, Max<float>>,
                WasmOpCodes.F32Copysign => EmitBinary<float, float, CopySign<float>>,
                WasmOpCodes.F64Abs => EmitUnary<double, double, Abs<double>>,
                WasmOpCodes.F64Neg => EmitUnary<double, double, Neg<double>>,
                WasmOpCodes.F64Ceil => EmitUnary<double, double, Ceil<double>>,
                WasmOpCodes.F64Floor => EmitUnary<double, double, Floor<double>>,
                WasmOpCodes.F64Trunc => EmitUnary<double, double, Trunc<double>>,
                WasmOpCodes.F64Nearest => EmitUnary<double, double, Nearest<double>>,
                WasmOpCodes.F64Sqrt => EmitUnary<double, double, Sqrt<double>>,
                WasmOpCodes.F64Add => EmitBinary<double, double, Add<double>>,
                WasmOpCodes.F64Sub => EmitBinary<double, double, Sub<double>>,
                WasmOpCodes.F64Mul => EmitBinary<double, double, Mul<double>>,
                WasmOpCodes.F64Div => EmitBinary<double, double, Div<double>>,
                WasmOpCodes.F64Min => EmitBinary<double, double, Min<double>>,
                WasmOpCodes.F64Max => EmitBinary<double, double, Max<double>>,
                WasmOpCodes.F64Copysign => EmitBinary<double, double, CopySign<double>>,
                WasmOpCodes.I32WrapI64 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32WrapI64), next),
                WasmOpCodes.I32TruncF32S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32TruncF32S), next),
                WasmOpCodes.I32TruncF32U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32TruncF32U), next),
                WasmOpCodes.I32TruncF64S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32TruncF64S), next),
                WasmOpCodes.I32TruncF64U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32TruncF64U), next),
                WasmOpCodes.I64ExtendI32S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64ExtendI32S), next),
                WasmOpCodes.I64ExtendI32U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64ExtendI32U), next),
                WasmOpCodes.I64TruncF32S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64TruncF32S), next),
                WasmOpCodes.I64TruncF32U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64TruncF32U), next),
                WasmOpCodes.I64TruncF64S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64TruncF64S), next),
                WasmOpCodes.I64TruncF64U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64TruncF64U), next),
                WasmOpCodes.F32ConvertI32S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F32ConvertI32S), next),
                WasmOpCodes.F32ConvertI32U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F32ConvertI32U), next),
                WasmOpCodes.F32ConvertI64S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F32ConvertI64S), next),
                WasmOpCodes.F32ConvertI64U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F32ConvertI64U), next),
                WasmOpCodes.F32DemoteF64 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F32DemoteF64), next),
                WasmOpCodes.F64ConvertI32S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F64ConvertI32S), next),
                WasmOpCodes.F64ConvertI32U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F64ConvertI32U), next),
                WasmOpCodes.F64ConvertI64S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F64ConvertI64S), next),
                WasmOpCodes.F64ConvertI64U => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F64ConvertI64U), next),
                WasmOpCodes.F64PromoteF32 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F64PromoteF32), next),
                WasmOpCodes.I32ReinterpretF32 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32ReinterpretF32), next),
                WasmOpCodes.I64ReinterpretF64 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64ReinterpretF64), next),
                WasmOpCodes.F32ReinterpretI32 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F32ReinterpretI32), next),
                WasmOpCodes.F64ReinterpretI64 => next => typeof(WasmConvert<,>).MakeGenericType(typeof(F64ReinterpretI64), next),
                WasmOpCodes.I32Extend8S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32Extend8S), next),
                WasmOpCodes.I32Extend16S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I32Extend16S), next),
                WasmOpCodes.I64Extend8S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64Extend8S), next),
                WasmOpCodes.I64Extend16S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64Extend16S), next),
                WasmOpCodes.I64Extend32S => next => typeof(WasmConvert<,>).MakeGenericType(typeof(I64Extend32S), next),
                WasmOpCodes.RefNull => next => typeof(WasmRefNull<>).MakeGenericType(next),
                WasmOpCodes.RefIsNull => next => typeof(WasmRefIsNull<>).MakeGenericType(next),
                WasmOpCodes.RefAsNonNull => next => typeof(WasmRefAsNonNull<>).MakeGenericType(next),
                WasmOpCodes.RefEq => next => typeof(WasmRefEq<>).MakeGenericType(next),
                WasmOpCodes.RefFunc => next => typeof(WasmRefFunc<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<RefFuncInstruction>(instr).FunctionIndex),
                    next
                ),
                WasmOpCodes.BrOnNull => next => typeof(WasmBrOnNull<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<BrOnNullInstruction>(instr).LabelIndex),
                    next
                ),
                WasmOpCodes.BrOnNonNull => next => typeof(WasmBrOnNonNull<,>).MakeGenericType(
                    TypeLiteralFactory.CreateInt32((int)Unsafe.As<BrOnNonNullInstruction>(instr).LabelIndex),
                    next
                ),
                WasmOpCodes.GCExtension => next => typeof(WasmGCExtension<,>).MakeGenericType(
                    InstructionLiteralFactory.Create(instr),
                    next
                ),
                WasmOpCodes.FCExtension => next => typeof(WasmFCExtension<,>).MakeGenericType(
                    InstructionLiteralFactory.Create(instr),
                    next
                ),
                WasmOpCodes.SIMDExtension => next => typeof(WasmSIMDExtension<,>).MakeGenericType(
                    InstructionLiteralFactory.Create(instr),
                    next
                ),
                _ => null,
            };

        static Type EmitConst<TValue>(Type literal, Type next)
            where TValue : INumberBase<TValue> =>
            typeof(WasmConst<,,>).MakeGenericType(typeof(TValue), literal, next);

        static Type EmitLoad<TStorage, TValue>(uint memoryIndex, uint offset, Type next)
            where TStorage : unmanaged, INumberBase<TStorage>
            where TValue : INumberBase<TValue> =>
            typeof(WasmLoad<,,,,>).MakeGenericType(
                TypeLiteralFactory.CreateInt32((int)memoryIndex),
                TypeLiteralFactory.CreateInt32((int)offset),
                typeof(TStorage),
                typeof(TValue),
                next
            );

        static Type EmitStore<TValue, TStorage>(uint memoryIndex, uint offset, Type next)
            where TValue : INumberBase<TValue>
            where TStorage : unmanaged, INumberBase<TStorage> =>
            typeof(WasmStore<,,,,>).MakeGenericType(
                TypeLiteralFactory.CreateInt32((int)memoryIndex),
                TypeLiteralFactory.CreateInt32((int)offset),
                typeof(TValue),
                typeof(TStorage),
                next
            );

        static Type EmitUnary<TValue, TResult, TOp>(Type next)
            where TOp : IUnaryOperator<TValue, TResult> =>
            typeof(WasmUnary<,,,>).MakeGenericType(typeof(TValue), typeof(TResult), typeof(TOp), next);

        static Type EmitBinary<TValue, TResult, TOp>(Type next)
            where TOp : IBinaryOperator<TValue, TResult> =>
            typeof(WasmBinary<,,,>).MakeGenericType(typeof(TValue), typeof(TResult), typeof(TOp), next);

        static Type EmitCompare<TValue, TOp>(Type next)
            where TOp : ICompareOperator<TValue> =>
            typeof(WasmCompare<,,>).MakeGenericType(typeof(TValue), typeof(TOp), next);
    }

    ref struct WasmExecutionFrame
    {
        public WasmExecutionFrame(
            WasmInstance instance,
            Span<WasmValue> locals,
            int functionStackBase,
            int functionResultCount
        )
        {
            Instance = instance;
            Locals = locals;
            FunctionStackBase = functionStackBase;
            FunctionResultCount = functionResultCount;
        }

        public WasmInstance Instance { get; }
        public Span<WasmValue> Locals { get; }
        public int FunctionStackBase { get; }
        public int FunctionResultCount { get; }
    }

    readonly struct WasmOpResult
    {
        readonly FunctionInstance? tailCall;

        WasmOpResult(WasmOpResultKind kind, int labelIndex = 0, FunctionInstance? tailCall = null)
        {
            Kind = kind;
            LabelIndex = labelIndex;
            this.tailCall = tailCall;
        }

        public WasmOpResultKind Kind { get; }
        public int LabelIndex { get; }
        public FunctionInstance TailCall => tailCall!.Value;

        public static WasmOpResult Continue => default;
        public static WasmOpResult Branch(int labelIndex) => new(WasmOpResultKind.Branch, labelIndex);
        public static WasmOpResult Return => new(WasmOpResultKind.Return);
        public static WasmOpResult Tail(FunctionInstance function) => new(WasmOpResultKind.TailCall, tailCall: function);
    }

    enum WasmOpResultKind : byte
    {
        Continue,
        Branch,
        Return,
        TailCall,
    }

    interface IWasmCompiledOp
    {
        abstract static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame);
    }

    readonly struct WasmStop : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame) =>
            WasmOpResult.Continue;
    }

    readonly struct WasmBlock<TParameterCount, TResultCount, TBody, TNext> : IWasmCompiledOp
        where TParameterCount : ILiteral<int>
        where TResultCount : ILiteral<int>
        where TBody : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var stackBase = context.valueStack.Count - TParameterCount.Value;
            var result = TBody.Run(context, ref frame);
            result = context.ResolveBlockResult(result, stackBase, TResultCount.Value);
            return result.Kind == WasmOpResultKind.Continue
                ? TNext.Run(context, ref frame)
                : result;
        }
    }

    readonly struct WasmLoop<TParameterCount, TResultCount, TBody, TNext> : IWasmCompiledOp
        where TParameterCount : ILiteral<int>
        where TResultCount : ILiteral<int>
        where TBody : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var stackBase = context.valueStack.Count - TParameterCount.Value;
            while (true)
            {
                var result = TBody.Run(context, ref frame);
                if (result.Kind == WasmOpResultKind.Continue)
                    return TNext.Run(context, ref frame);

                if (result.Kind != WasmOpResultKind.Branch)
                    return result;

                if (result.LabelIndex != 0)
                    return WasmOpResult.Branch(result.LabelIndex - 1);

                if (TParameterCount.Value == 0)
                    context.valueStack.Truncate(stackBase);
                else
                    context.PreserveValues(stackBase, TParameterCount.Value);
            }
        }
    }

    readonly struct WasmIf<TParameterCount, TResultCount, TThen, TElse, TNext> : IWasmCompiledOp
        where TParameterCount : ILiteral<int>
        where TResultCount : ILiteral<int>
        where TThen : IWasmCompiledOp
        where TElse : IWasmCompiledOp
        where TNext : IWasmCompiledOp
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static WasmOpResult Run(WasmExecutionContext context, ref WasmExecutionFrame frame)
        {
            var condition = context.valueStack.UnsafePop().I32;
            var stackBase = context.valueStack.Count - TParameterCount.Value;
            var result =
                condition != 0
                    ? TThen.Run(context, ref frame)
                    : TElse.Run(context, ref frame);
            result = context.ResolveBlockResult(result, stackBase, TResultCount.Value);
            return result.Kind == WasmOpResultKind.Continue
                ? TNext.Run(context, ref frame)
                : result;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    WasmOpResult ResolveBlockResult(WasmOpResult result, int stackBase, int resultCount)
    {
        if (result.Kind == WasmOpResultKind.Continue)
            return WasmOpResult.Continue;

        if (result.Kind != WasmOpResultKind.Branch)
            return result;

        if (result.LabelIndex != 0)
            return WasmOpResult.Branch(result.LabelIndex - 1);

        PreserveValues(stackBase, resultCount);
        return WasmOpResult.Continue;
    }
}
