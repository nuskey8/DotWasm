using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using DotWasm.Models;
using WasmValueType = DotWasm.Models.WasmValueType;

namespace DotWasm.Encoding;

public static class WasmEncoding
{
    public const uint Magic = 0x6d736100;
    public const byte MemoryIndexBit = 0x40;
    public const byte MemoryAlignmentMask = 0x3F;

    public static WasmModule Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new SpanReader(bytes);
        var magic = reader.ReadUInt32LittleEndian();
        if (magic != Magic)
        {
            WasmDecodeException.Throw($"Invalid WebAssembly magic '{magic:x8}'");
        }

        var version = reader.ReadUInt32LittleEndian();
        if (version != 1)
        {
            WasmDecodeException.Throw($"Unsupported WebAssembly version '{version}'");
        }

        RecursiveType[] types = [];
        FuncType?[] functionTypes = [];
        uint[] functionTypeIndices = [];
        TableType[] tables = [];
        MemoryType[] memories = [];
        Global[] globals = [];
        TagType[] tags = [];
        Element[] elements = [];
        DataSegment[] dataSegments = [];
        Import[] imports = [];
        Export[] exports = [];
        uint? startFunctionIndex = null;
        uint? dataSegmentCount = null;
        Function[]? functions = null;
        byte lastKnownSectionOrder = 0;

        while (!reader.IsEmpty)
        {
            var sectionId = reader.ReadByte();
            var payloadLength = checked((int)reader.ReadUInt32Leb128());
            var section = new SpanReader(reader.ReadBytes(payloadLength));
            if (sectionId != 0)
            {
                var sectionOrder = GetKnownSectionOrder(sectionId);
                if (sectionOrder < lastKnownSectionOrder)
                    WasmDecodeException.Throw("Known sections must appear in canonical order.");
                if (sectionOrder == lastKnownSectionOrder)
                    WasmDecodeException.Throw("Known sections cannot appear more than once.");
                lastKnownSectionOrder = sectionOrder;
            }

            switch (sectionId)
            {
                case 0:
                    _ = ReadName(ref section);
                    section.Advance(section.Length);
                    break;
                case 1:
                    (types, functionTypes) = DecodeTypeSection(ref section);
                    break;
                case 2:
                    imports = ReadImports(ref section, functionTypes);
                    break;
                case 3:
                    functionTypeIndices = DecodeVector(
                        ref section,
                        static (ref r) => r.ReadUInt32Leb128()
                    );
                    break;
                case 4:
                    tables = DecodeVector(
                        ref section,
                        (ref r) => ReadTableType(ref r, functionTypes)
                    );
                    break;
                case 5:
                    memories = DecodeVector(ref section, static (ref r) => ReadMemoryType(ref r));
                    break;
                case 6:
                    globals = DecodeVector(
                        ref section,
                        (ref r) => ReadGlobal(ref r, functionTypes)
                    );
                    break;
                case 7:
                    exports = DecodeVector(ref section, static (ref r) => ReadExport(ref r));
                    break;
                case 8:
                    startFunctionIndex = section.ReadUInt32Leb128();
                    break;
                case 9:
                    elements = DecodeVector(
                        ref section,
                        (ref r) => ReadElement(ref r, functionTypes)
                    );
                    break;
                case 10:
                {
                    var allMemories = BuildAllMemories(imports, memories);
                    functions = DecodeCodeSection(
                        ref section,
                        functionTypeIndices,
                        functionTypes,
                        allMemories
                    );
                    break;
                }
                case 11:
                    dataSegments = DecodeVector(
                        ref section,
                        (ref r) => ReadDataSegment(ref r, functionTypes)
                    );
                    break;
                case 12:
                    dataSegmentCount = section.ReadUInt32Leb128();
                    break;
                case 13:
                    tags = DecodeVector(ref section, (ref r) => ReadTagType(ref r, functionTypes));
                    break;
                default:
                    WasmDecodeException.Throw($"Unsupported WebAssembly section id {sectionId}.");
                    break;
            }

            EnsureEmpty(ref section);
        }

        if (functions is null)
        {
            if (functionTypeIndices.Length != 0)
                WasmDecodeException.Throw("Function section requires a matching code section.");
            functions = [];
        }
        else if (functions.Length != functionTypeIndices.Length)
        {
            WasmDecodeException.Throw("Function and code section counts differ.");
        }

        if (dataSegmentCount.HasValue && dataSegmentCount.Value != dataSegments.Length)
            WasmDecodeException.Throw("Data count and data section counts differ.");
        if (!dataSegmentCount.HasValue && UsesDataSegmentInstruction(functions))
            WasmDecodeException.Throw("Data count section is required.");

        EnsureEmpty(ref reader);
        return new WasmModule
        {
            Version = version,
            Types = types.ToImmutableArray(),
            Functions = functions.ToImmutableArray(),
            Tables = tables.ToImmutableArray(),
            Memories = memories.ToImmutableArray(),
            Globals = globals.ToImmutableArray(),
            Tags = tags.ToImmutableArray(),
            Elements = elements.ToImmutableArray(),
            Data = dataSegments.ToImmutableArray(),
            StartFunctionIndex = startFunctionIndex,
            Imports = imports.ToImmutableArray(),
            Exports = exports.ToImmutableArray(),
        };
    }

    static byte GetKnownSectionOrder(byte sectionId) =>
        sectionId switch
        {
            >= 1 and <= 5 => sectionId,
            13 => 6,
            6 => 7,
            >= 7 and <= 9 => (byte)(sectionId + 1),
            12 => 11,
            10 => 12,
            11 => 13,
            _ => ThrowSectionId(sectionId),
        };

    static byte ThrowSectionId(byte sectionId)
    {
        WasmDecodeException.Throw($"Unsupported WebAssembly section id {sectionId}.");
        return default;
    }

    static MemoryType[] BuildAllMemories(
        ReadOnlySpan<Import> imports,
        ReadOnlySpan<MemoryType> localMemories
    )
    {
        var importCount = 0;
        foreach (var import in imports)
        {
            if (import.Kind == ImportExportKind.Memory)
                importCount++;
        }
        var result = new MemoryType[importCount + localMemories.Length];
        var idx = 0;
        foreach (var import in imports)
        {
            if (import.Kind == ImportExportKind.Memory)
                result[idx++] = GetMemoryTypeFromImport(import);
        }
        localMemories.CopyTo(result.AsSpan(idx));
        return result;
    }

    static MemoryType GetMemoryTypeFromImport(Import import)
    {
        if (import.Type is MemoryType mt)
            return mt;
        WasmDecodeException.Throw("Expected memory type for import.");
        return default;
    }

    static Function[] DecodeCodeSection(
        ref SpanReader reader,
        ReadOnlySpan<uint> functionTypeIndices,
        ReadOnlySpan<FuncType?> types,
        ReadOnlySpan<MemoryType> allMemories
    )
    {
        var count = checked((int)reader.ReadUInt32Leb128());
        if (count != functionTypeIndices.Length)
            WasmDecodeException.Throw("Function and code section counts differ.");

        var functions = new Function[count];
        for (var i = 0; i < count; i++)
        {
            var typeIndex = functionTypeIndices[i];
            if (typeIndex >= types.Length)
                WasmDecodeException.Throw("Unknown function type.");
            var bodySize = checked((int)reader.ReadUInt32Leb128());
            var bodyReader = new SpanReader(reader.ReadBytes(bodySize));
            var locals = ReadLocals(ref bodyReader, types);
            var expression = ReadExpression(ref bodyReader, types, allMemories);
            EnsureEmpty(ref bodyReader);
            functions[i] = new Function
            {
                TypeIndex = typeIndex,
                Locals = locals.ToImmutableArray(),
                Body = expression,
            };
        }

        return functions;
    }

    static (RecursiveType[] Types, FuncType?[] FunctionTypes) DecodeTypeSection(
        ref SpanReader reader
    )
    {
        var count = checked((int)reader.ReadUInt32Leb128());
        if (count == 0)
            return ([], []);

        var recursiveTypes = new RecursiveType[count];
        var definedTypes = new List<SubType>();
        for (var i = 0; i < recursiveTypes.Length; i++)
        {
            recursiveTypes[i] = ReadRecursiveType(ref reader, definedTypes);
            definedTypes.AddRange(recursiveTypes[i].SubTypes);
        }

        var functionTypes = new FuncType?[definedTypes.Count];
        for (var i = 0; i < definedTypes.Count; i++)
            if (definedTypes[i].CompositeType is FuncType funcType)
                functionTypes[i] = funcType;
        return (recursiveTypes, functionTypes);
    }

    static bool UsesDataSegmentInstruction(ReadOnlySpan<Function> functions)
    {
        foreach (var function in functions)
        {
            foreach (var instr in function.Body.Instructions)
            {
                if (instr is MemoryInitInstruction or DataDropInstruction)
                    return true;
            }
        }

        return false;
    }

    static Expression ReadExpression(ref SpanReader reader, ReadOnlySpan<FuncType?> types) =>
        ReadExpression(ref reader, types, []);

    static Expression ReadExpression(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        ReadOnlySpan<MemoryType> memories
    )
    {
        var instructions = new List<Instruction>();
        var blockStack = new Stack<int>();
        var blockDepth = 0;

        while (true)
        {
            var pos = instructions.Count;
            var instr = ReadOneInstruction(ref reader, types, memories);
            instructions.Add(instr);

            switch (instr.OpCode)
            {
                case WasmOpCodes.Block:
                case WasmOpCodes.Loop:
                case WasmOpCodes.Try:
                case WasmOpCodes.If:
                case WasmOpCodes.TryTable:
                    blockStack.Push(pos);
                    blockDepth++;
                    break;
                case WasmOpCodes.Else:
                {
                    var ifPos = blockStack.Peek();
                    ((IfInstruction)instructions[ifPos]).ElseIndex = pos;
                    break;
                }
                case WasmOpCodes.End:
                {
                    if (blockDepth == 0)
                        return new Expression { Instructions = instructions.ToImmutableArray() };

                    var blockPos = blockStack.Pop();
                    blockDepth--;
                    var blockInstr = instructions[blockPos];
                    if (blockInstr is BlockInstruction b)
                        b.EndIndex = pos;
                    else if (blockInstr is IfInstruction i)
                        i.EndIndex = pos;
                    else if (blockInstr is TryTableInstruction t)
                        t.EndIndex = pos;
                    else if (blockInstr is TryInstruction tr)
                        tr.EndIndex = pos;
                    else if (blockInstr is LoopInstruction l)
                        l.EndIndex = pos;
                    break;
                }
            }
        }
    }

    static Instruction ReadOneInstruction(ref SpanReader reader, ReadOnlySpan<FuncType?> types) =>
        ReadOneInstruction(ref reader, types, []);

    static Instruction ReadOneInstruction(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        ReadOnlySpan<MemoryType> memories
    )
    {
        var opcode = reader.ReadByte();
        switch (opcode)
        {
            case WasmOpCodes.Unreachable:
                return new UnreachableInstruction();
            case WasmOpCodes.Nop:
                return new NopInstruction();
            case WasmOpCodes.Block:
            case WasmOpCodes.Loop:
            case WasmOpCodes.Try:
            {
                var blockType = ReadBlockType(ref reader, types, types.Length);
                var funcType = ResolveBlockFuncType(blockType, types);
                var paramCount = funcType.Parameters.Length;
                var resultCount = funcType.Results.Length;
                if (opcode == WasmOpCodes.Block)
                    return new BlockInstruction(paramCount, resultCount)
                    {
                        ParameterTypes = funcType.Parameters,
                        ResultTypes = funcType.Results,
                    };
                if (opcode == WasmOpCodes.Loop)
                    return new LoopInstruction(paramCount, resultCount)
                    {
                        ParameterTypes = funcType.Parameters,
                        ResultTypes = funcType.Results,
                    };
                return new TryInstruction(paramCount, resultCount)
                {
                    ParameterTypes = funcType.Parameters,
                    ResultTypes = funcType.Results,
                };
            }
            case WasmOpCodes.If:
            {
                var blockType = ReadBlockType(ref reader, types, types.Length);
                var funcType = ResolveBlockFuncType(blockType, types);
                return new IfInstruction(funcType.Parameters.Length, funcType.Results.Length)
                {
                    ParameterTypes = funcType.Parameters,
                    ResultTypes = funcType.Results,
                };
            }
            case WasmOpCodes.Else:
                return new ElseInstruction();
            case WasmOpCodes.TryTable:
            {
                var blockType = ReadBlockType(ref reader, types, types.Length);
                var funcType = ResolveBlockFuncType(blockType, types);
                var catchTable = ReadCatchTable(ref reader);
                return new TryTableInstruction(
                    funcType.Parameters.Length,
                    funcType.Results.Length,
                    catchTable
                )
                {
                    ParameterTypes = funcType.Parameters,
                    ResultTypes = funcType.Results,
                };
            }
            case WasmOpCodes.Catch:
                return new CatchInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.Throw:
                return new ThrowInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.Rethrow:
                return new RethrowInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.ThrowRef:
                return new ThrowRefInstruction();
            case WasmOpCodes.End:
                return new EndInstruction();
            case WasmOpCodes.Br:
                return new BrInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.BrIf:
                return new BrIfInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.BrTable:
            {
                var labels = ReadUInt32Vector(ref reader);
                var defaultLabel = reader.ReadUInt32Leb128();
                return new BrTableInstruction(labels.ToImmutableArray(), defaultLabel);
            }
            case WasmOpCodes.Return:
                return new ReturnInstruction();
            case WasmOpCodes.Call:
                return new CallInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.CallIndirect:
            {
                var typeIndex = reader.ReadUInt32Leb128();
                var tableIndex = reader.ReadUInt32Leb128();
                return new CallIndirectInstruction(typeIndex, tableIndex);
            }
            case WasmOpCodes.ReturnCall:
                return new ReturnCallInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.ReturnCallIndirect:
            {
                var typeIndex = reader.ReadUInt32Leb128();
                var tableIndex = reader.ReadUInt32Leb128();
                return new ReturnCallIndirectInstruction(typeIndex, tableIndex);
            }
            case WasmOpCodes.CallRef:
                return new CallRefInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.ReturnCallRef:
                return new ReturnCallRefInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.Delegate:
                return new DelegateInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.CatchAll:
                return new CatchAllInstruction();
            case WasmOpCodes.Drop:
                return new DropInstruction();
            case WasmOpCodes.Select:
                return new SelectInstruction();
            case WasmOpCodes.SelectT:
            {
                var typeCount = reader.ReadUInt32Leb128();
                var typesList = new WasmValueType[typeCount];
                for (var i = 0u; i < typeCount; i++)
                    typesList[i] = ReadValueType(ref reader, types, types.Length);
                return new SelectTInstruction(typesList.ToImmutableArray());
            }
            case WasmOpCodes.LocalGet:
                return new LocalGetInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.LocalSet:
                return new LocalSetInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.LocalTee:
                return new LocalTeeInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.GlobalGet:
                return new GlobalGetInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.GlobalSet:
                return new GlobalSetInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.TableGet:
                return new TableGetInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.TableSet:
                return new TableSetInstruction(reader.ReadUInt32Leb128());
            case >= WasmOpCodes.I32Load and <= WasmOpCodes.I64Store32:
            {
                var flags = reader.ReadUInt32Leb128();
                if (flags >= 0x80)
                    WasmDecodeException.Throw("Malformed memop flags.");
                var memoryIndex = (flags & MemoryIndexBit) != 0 ? reader.ReadUInt32Leb128() : 0u;
                var offset = ReadMemArgOffset(ref reader, memoryIndex, memories);
                var align = flags & MemoryAlignmentMask;
                return opcode switch
                {
                    WasmOpCodes.I32Load => new I32LoadInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load => new I64LoadInstruction(align, memoryIndex, offset),
                    WasmOpCodes.F32Load => new F32LoadInstruction(align, memoryIndex, offset),
                    WasmOpCodes.F64Load => new F64LoadInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Load8S => new I32Load8SInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Load8U => new I32Load8UInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Load16S => new I32Load16SInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Load16U => new I32Load16UInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load8S => new I64Load8SInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load8U => new I64Load8UInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load16S => new I64Load16SInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load16U => new I64Load16UInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load32S => new I64Load32SInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Load32U => new I64Load32UInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Store => new I32StoreInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Store => new I64StoreInstruction(align, memoryIndex, offset),
                    WasmOpCodes.F32Store => new F32StoreInstruction(align, memoryIndex, offset),
                    WasmOpCodes.F64Store => new F64StoreInstruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Store8 => new I32Store8Instruction(align, memoryIndex, offset),
                    WasmOpCodes.I32Store16 => new I32Store16Instruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Store8 => new I64Store8Instruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Store16 => new I64Store16Instruction(align, memoryIndex, offset),
                    WasmOpCodes.I64Store32 => new I64Store32Instruction(align, memoryIndex, offset),
                    _ => throw new InvalidOperationException(
                        $"Unexpected memop opcode 0x{opcode:X2}"
                    ),
                };
            }
            case WasmOpCodes.MemorySize:
                return new MemorySizeInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.MemoryGrow:
                return new MemoryGrowInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.I32Const:
                return new I32ConstInstruction(reader.ReadInt32Leb128());
            case WasmOpCodes.I64Const:
                return new I64ConstInstruction(reader.ReadInt64Leb128());
            case WasmOpCodes.F32Const:
            {
                var value = Unsafe.ReadUnaligned<float>(
                    ref MemoryMarshal.GetReference(reader.ReadBytes(sizeof(float)))
                );
                return new F32ConstInstruction(value);
            }
            case WasmOpCodes.F64Const:
            {
                var value = Unsafe.ReadUnaligned<double>(
                    ref MemoryMarshal.GetReference(reader.ReadBytes(sizeof(double)))
                );
                return new F64ConstInstruction(value);
            }
            case >= WasmOpCodes.I32Eqz and <= WasmOpCodes.I64Extend32S:
            {
                return opcode switch
                {
                    WasmOpCodes.I32Eqz => new I32EqzInstruction(),
                    WasmOpCodes.I32Eq => new I32EqInstruction(),
                    WasmOpCodes.I32Ne => new I32NeInstruction(),
                    WasmOpCodes.I32LtS => new I32LtSInstruction(),
                    WasmOpCodes.I32LtU => new I32LtUInstruction(),
                    WasmOpCodes.I32GtS => new I32GtSInstruction(),
                    WasmOpCodes.I32GtU => new I32GtUInstruction(),
                    WasmOpCodes.I32LeS => new I32LeSInstruction(),
                    WasmOpCodes.I32LeU => new I32LeUInstruction(),
                    WasmOpCodes.I32GeS => new I32GeSInstruction(),
                    WasmOpCodes.I32GeU => new I32GeUInstruction(),
                    WasmOpCodes.I64Eqz => new I64EqzInstruction(),
                    WasmOpCodes.I64Eq => new I64EqInstruction(),
                    WasmOpCodes.I64Ne => new I64NeInstruction(),
                    WasmOpCodes.I64LtS => new I64LtSInstruction(),
                    WasmOpCodes.I64LtU => new I64LtUInstruction(),
                    WasmOpCodes.I64GtS => new I64GtSInstruction(),
                    WasmOpCodes.I64GtU => new I64GtUInstruction(),
                    WasmOpCodes.I64LeS => new I64LeSInstruction(),
                    WasmOpCodes.I64LeU => new I64LeUInstruction(),
                    WasmOpCodes.I64GeS => new I64GeSInstruction(),
                    WasmOpCodes.I64GeU => new I64GeUInstruction(),
                    WasmOpCodes.F32Eq => new F32EqInstruction(),
                    WasmOpCodes.F32Ne => new F32NeInstruction(),
                    WasmOpCodes.F32Lt => new F32LtInstruction(),
                    WasmOpCodes.F32Gt => new F32GtInstruction(),
                    WasmOpCodes.F32Le => new F32LeInstruction(),
                    WasmOpCodes.F32Ge => new F32GeInstruction(),
                    WasmOpCodes.F64Eq => new F64EqInstruction(),
                    WasmOpCodes.F64Ne => new F64NeInstruction(),
                    WasmOpCodes.F64Lt => new F64LtInstruction(),
                    WasmOpCodes.F64Gt => new F64GtInstruction(),
                    WasmOpCodes.F64Le => new F64LeInstruction(),
                    WasmOpCodes.F64Ge => new F64GeInstruction(),
                    WasmOpCodes.I32Clz => new I32ClzInstruction(),
                    WasmOpCodes.I32Ctz => new I32CtzInstruction(),
                    WasmOpCodes.I32Popcnt => new I32PopcntInstruction(),
                    WasmOpCodes.I32Add => new I32AddInstruction(),
                    WasmOpCodes.I32Sub => new I32SubInstruction(),
                    WasmOpCodes.I32Mul => new I32MulInstruction(),
                    WasmOpCodes.I32DivS => new I32DivSInstruction(),
                    WasmOpCodes.I32DivU => new I32DivUInstruction(),
                    WasmOpCodes.I32RemS => new I32RemSInstruction(),
                    WasmOpCodes.I32RemU => new I32RemUInstruction(),
                    WasmOpCodes.I32And => new I32AndInstruction(),
                    WasmOpCodes.I32Or => new I32OrInstruction(),
                    WasmOpCodes.I32Xor => new I32XorInstruction(),
                    WasmOpCodes.I32Shl => new I32ShlInstruction(),
                    WasmOpCodes.I32ShrS => new I32ShrSInstruction(),
                    WasmOpCodes.I32ShrU => new I32ShrUInstruction(),
                    WasmOpCodes.I32Rotl => new I32RotlInstruction(),
                    WasmOpCodes.I32Rotr => new I32RotrInstruction(),
                    WasmOpCodes.I64Clz => new I64ClzInstruction(),
                    WasmOpCodes.I64Ctz => new I64CtzInstruction(),
                    WasmOpCodes.I64Popcnt => new I64PopcntInstruction(),
                    WasmOpCodes.I64Add => new I64AddInstruction(),
                    WasmOpCodes.I64Sub => new I64SubInstruction(),
                    WasmOpCodes.I64Mul => new I64MulInstruction(),
                    WasmOpCodes.I64DivS => new I64DivSInstruction(),
                    WasmOpCodes.I64DivU => new I64DivUInstruction(),
                    WasmOpCodes.I64RemS => new I64RemSInstruction(),
                    WasmOpCodes.I64RemU => new I64RemUInstruction(),
                    WasmOpCodes.I64And => new I64AndInstruction(),
                    WasmOpCodes.I64Or => new I64OrInstruction(),
                    WasmOpCodes.I64Xor => new I64XorInstruction(),
                    WasmOpCodes.I64Shl => new I64ShlInstruction(),
                    WasmOpCodes.I64ShrS => new I64ShrSInstruction(),
                    WasmOpCodes.I64ShrU => new I64ShrUInstruction(),
                    WasmOpCodes.I64Rotl => new I64RotlInstruction(),
                    WasmOpCodes.I64Rotr => new I64RotrInstruction(),
                    WasmOpCodes.F32Abs => new F32AbsInstruction(),
                    WasmOpCodes.F32Neg => new F32NegInstruction(),
                    WasmOpCodes.F32Ceil => new F32CeilInstruction(),
                    WasmOpCodes.F32Floor => new F32FloorInstruction(),
                    WasmOpCodes.F32Trunc => new F32TruncInstruction(),
                    WasmOpCodes.F32Nearest => new F32NearestInstruction(),
                    WasmOpCodes.F32Sqrt => new F32SqrtInstruction(),
                    WasmOpCodes.F32Add => new F32AddInstruction(),
                    WasmOpCodes.F32Sub => new F32SubInstruction(),
                    WasmOpCodes.F32Mul => new F32MulInstruction(),
                    WasmOpCodes.F32Div => new F32DivInstruction(),
                    WasmOpCodes.F32Min => new F32MinInstruction(),
                    WasmOpCodes.F32Max => new F32MaxInstruction(),
                    WasmOpCodes.F32Copysign => new F32CopysignInstruction(),
                    WasmOpCodes.F64Abs => new F64AbsInstruction(),
                    WasmOpCodes.F64Neg => new F64NegInstruction(),
                    WasmOpCodes.F64Ceil => new F64CeilInstruction(),
                    WasmOpCodes.F64Floor => new F64FloorInstruction(),
                    WasmOpCodes.F64Trunc => new F64TruncInstruction(),
                    WasmOpCodes.F64Nearest => new F64NearestInstruction(),
                    WasmOpCodes.F64Sqrt => new F64SqrtInstruction(),
                    WasmOpCodes.F64Add => new F64AddInstruction(),
                    WasmOpCodes.F64Sub => new F64SubInstruction(),
                    WasmOpCodes.F64Mul => new F64MulInstruction(),
                    WasmOpCodes.F64Div => new F64DivInstruction(),
                    WasmOpCodes.F64Min => new F64MinInstruction(),
                    WasmOpCodes.F64Max => new F64MaxInstruction(),
                    WasmOpCodes.F64Copysign => new F64CopysignInstruction(),
                    WasmOpCodes.I32WrapI64 => new I32WrapI64Instruction(),
                    WasmOpCodes.I32TruncF32S => new I32TruncF32SInstruction(),
                    WasmOpCodes.I32TruncF32U => new I32TruncF32UInstruction(),
                    WasmOpCodes.I32TruncF64S => new I32TruncF64SInstruction(),
                    WasmOpCodes.I32TruncF64U => new I32TruncF64UInstruction(),
                    WasmOpCodes.I64ExtendI32S => new I64ExtendI32SInstruction(),
                    WasmOpCodes.I64ExtendI32U => new I64ExtendI32UInstruction(),
                    WasmOpCodes.I64TruncF32S => new I64TruncF32SInstruction(),
                    WasmOpCodes.I64TruncF32U => new I64TruncF32UInstruction(),
                    WasmOpCodes.I64TruncF64S => new I64TruncF64SInstruction(),
                    WasmOpCodes.I64TruncF64U => new I64TruncF64UInstruction(),
                    WasmOpCodes.F32ConvertI32S => new F32ConvertI32SInstruction(),
                    WasmOpCodes.F32ConvertI32U => new F32ConvertI32UInstruction(),
                    WasmOpCodes.F32ConvertI64S => new F32ConvertI64SInstruction(),
                    WasmOpCodes.F32ConvertI64U => new F32ConvertI64UInstruction(),
                    WasmOpCodes.F32DemoteF64 => new F32DemoteF64Instruction(),
                    WasmOpCodes.F64ConvertI32S => new F64ConvertI32SInstruction(),
                    WasmOpCodes.F64ConvertI32U => new F64ConvertI32UInstruction(),
                    WasmOpCodes.F64ConvertI64S => new F64ConvertI64SInstruction(),
                    WasmOpCodes.F64ConvertI64U => new F64ConvertI64UInstruction(),
                    WasmOpCodes.F64PromoteF32 => new F64PromoteF32Instruction(),
                    WasmOpCodes.I32ReinterpretF32 => new I32ReinterpretF32Instruction(),
                    WasmOpCodes.I64ReinterpretF64 => new I64ReinterpretF64Instruction(),
                    WasmOpCodes.F32ReinterpretI32 => new F32ReinterpretI32Instruction(),
                    WasmOpCodes.F64ReinterpretI64 => new F64ReinterpretI64Instruction(),
                    WasmOpCodes.I32Extend8S => new I32Extend8SInstruction(),
                    WasmOpCodes.I32Extend16S => new I32Extend16SInstruction(),
                    WasmOpCodes.I64Extend8S => new I64Extend8SInstruction(),
                    WasmOpCodes.I64Extend16S => new I64Extend16SInstruction(),
                    WasmOpCodes.I64Extend32S => new I64Extend32SInstruction(),
                    _ => throw new InvalidOperationException($"Unexpected opcode 0x{opcode:X2}"),
                };
            }
            case WasmOpCodes.RefNull:
            {
                var heapType = reader.ReadInt32Leb128();
                var type = ReadHeapType(heapType, types.Length, true);
                return new RefNullInstruction(type);
            }
            case WasmOpCodes.RefIsNull:
                return new RefIsNullInstruction();
            case WasmOpCodes.RefFunc:
                return new RefFuncInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.RefEq:
                return new RefEqInstruction();
            case WasmOpCodes.RefAsNonNull:
                return new RefAsNonNullInstruction();
            case WasmOpCodes.BrOnNull:
                return new BrOnNullInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.BrOnNonNull:
                return new BrOnNonNullInstruction(reader.ReadUInt32Leb128());
            case WasmOpCodes.GCExtension:
                return ReadGCInstruction(ref reader, types);
            case WasmOpCodes.FCExtension:
            {
                var subOpcode = reader.ReadUInt32Leb128();
                switch (subOpcode)
                {
                    case WasmOpCodes.I32TruncSatF32S:
                        return new I32TruncSatF32SInstruction();
                    case WasmOpCodes.I32TruncSatF32U:
                        return new I32TruncSatF32UInstruction();
                    case WasmOpCodes.I32TruncSatF64S:
                        return new I32TruncSatF64SInstruction();
                    case WasmOpCodes.I32TruncSatF64U:
                        return new I32TruncSatF64UInstruction();
                    case WasmOpCodes.I64TruncSatF32S:
                        return new I64TruncSatF32SInstruction();
                    case WasmOpCodes.I64TruncSatF32U:
                        return new I64TruncSatF32UInstruction();
                    case WasmOpCodes.I64TruncSatF64S:
                        return new I64TruncSatF64SInstruction();
                    case WasmOpCodes.I64TruncSatF64U:
                        return new I64TruncSatF64UInstruction();
                    case WasmOpCodes.MemoryInit:
                    {
                        var dataIdx = reader.ReadUInt32Leb128();
                        var memIdx = reader.ReadUInt32Leb128();
                        return new MemoryInitInstruction(dataIdx, memIdx);
                    }
                    case WasmOpCodes.DataDrop:
                        return new DataDropInstruction(reader.ReadUInt32Leb128());
                    case WasmOpCodes.MemoryCopy:
                    {
                        var dstMemIdx = reader.ReadUInt32Leb128();
                        var srcMemIdx = reader.ReadUInt32Leb128();
                        return new MemoryCopyInstruction(dstMemIdx, srcMemIdx);
                    }
                    case WasmOpCodes.MemoryFill:
                        return new MemoryFillInstruction(reader.ReadUInt32Leb128());
                    case WasmOpCodes.TableInit:
                    {
                        var elemIdx = reader.ReadUInt32Leb128();
                        var tableIdx = reader.ReadUInt32Leb128();
                        return new TableInitInstruction(elemIdx, tableIdx);
                    }
                    case WasmOpCodes.ElemDrop:
                        return new ElemDropInstruction(reader.ReadUInt32Leb128());
                    case WasmOpCodes.TableCopy:
                    {
                        var dstTableIdx = reader.ReadUInt32Leb128();
                        var srcTableIdx = reader.ReadUInt32Leb128();
                        return new TableCopyInstruction(dstTableIdx, srcTableIdx);
                    }
                    case WasmOpCodes.TableGrow:
                        return new TableGrowInstruction(reader.ReadUInt32Leb128());
                    case WasmOpCodes.TableSize:
                        return new TableSizeInstruction(reader.ReadUInt32Leb128());
                    case WasmOpCodes.TableFill:
                        return new TableFillInstruction(reader.ReadUInt32Leb128());
                    default:
                        WasmDecodeException.Throw(
                            $"Unsupported FC instruction opcode 0x{WasmOpCodes.FCExtension:x2} {subOpcode}."
                        );
                        return null!;
                }
            }
            case WasmOpCodes.SIMDExtension:
            {
                var subOpcode = reader.ReadUInt32Leb128();
                switch (subOpcode)
                {
                    case <= WasmOpCodes.V128Load32x2U:
                    case WasmOpCodes.V128Load32Zero:
                    case WasmOpCodes.V128Load64Zero:
                    {
                        var flags = reader.ReadUInt32Leb128();
                        var memoryIndex =
                            (flags & MemoryIndexBit) != 0 ? reader.ReadUInt32Leb128() : 0u;
                        var offset = ReadMemArgOffset(ref reader, memoryIndex, memories);
                        var align = flags & MemoryAlignmentMask;
                        return subOpcode switch
                        {
                            WasmOpCodes.V128Load => new V128LoadInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load8x8S => new V128Load8x8SInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load8x8U => new V128Load8x8UInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load16x4S => new V128Load16x4SInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load16x4U => new V128Load16x4UInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load32x2S => new V128Load32x2SInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load32x2U => new V128Load32x2UInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load32Zero => new V128Load32ZeroInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load64Zero => new V128Load64ZeroInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            _ => throw new InvalidOperationException(),
                        };
                    }
                    case WasmOpCodes.V128Store:
                    {
                        var flags = reader.ReadUInt32Leb128();
                        var memoryIndex =
                            (flags & MemoryIndexBit) != 0 ? reader.ReadUInt32Leb128() : 0u;
                        var offset = ReadMemArgOffset(ref reader, memoryIndex, memories);
                        var align = flags & MemoryAlignmentMask;
                        return new V128StoreInstruction(align, memoryIndex, offset);
                    }
                    case WasmOpCodes.V128Const:
                    {
                        var data = reader.ReadBytes(16);
                        var lower = Unsafe.ReadUnaligned<ulong>(
                            ref MemoryMarshal.GetReference(data)
                        );
                        var upper = Unsafe.ReadUnaligned<ulong>(
                            ref MemoryMarshal.GetReference(data.Slice(8))
                        );
                        return new V128ConstInstruction(lower, upper);
                    }
                    case WasmOpCodes.I8x16Shuffle:
                    {
                        var data = reader.ReadBytes(16);
                        return new I8x16ShuffleInstruction(data.ToArray());
                    }
                    case >= WasmOpCodes.V128Load8Splat and <= WasmOpCodes.V128Load64Splat:
                    {
                        var flags = reader.ReadUInt32Leb128();
                        var memoryIndex =
                            (flags & MemoryIndexBit) != 0 ? reader.ReadUInt32Leb128() : 0u;
                        var offset = ReadMemArgOffset(ref reader, memoryIndex, memories);
                        var align = flags & MemoryAlignmentMask;
                        return subOpcode switch
                        {
                            WasmOpCodes.V128Load8Splat => new V128Load8SplatInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load16Splat => new V128Load16SplatInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load32Splat => new V128Load32SplatInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            WasmOpCodes.V128Load64Splat => new V128Load64SplatInstruction(
                                align,
                                memoryIndex,
                                offset
                            ),
                            _ => throw new InvalidOperationException(),
                        };
                    }
                    case >= WasmOpCodes.V128Load8Lane and <= WasmOpCodes.V128Load64Lane:
                    {
                        var flags = reader.ReadUInt32Leb128();
                        var memoryIndex =
                            (flags & MemoryIndexBit) != 0 ? reader.ReadUInt32Leb128() : 0u;
                        var offset = reader.ReadUInt32Leb128();
                        var laneIndex = reader.ReadByte();
                        var align = flags & MemoryAlignmentMask;
                        return subOpcode switch
                        {
                            WasmOpCodes.V128Load8Lane => new V128Load8LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            WasmOpCodes.V128Load16Lane => new V128Load16LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            WasmOpCodes.V128Load32Lane => new V128Load32LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            WasmOpCodes.V128Load64Lane => new V128Load64LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            _ => throw new InvalidOperationException(),
                        };
                    }
                    case >= WasmOpCodes.V128Store8Lane and <= WasmOpCodes.V128Store64Lane:
                    {
                        var flags = reader.ReadUInt32Leb128();
                        var memoryIndex =
                            (flags & MemoryIndexBit) != 0 ? reader.ReadUInt32Leb128() : 0u;
                        var offset = reader.ReadUInt32Leb128();
                        var laneIndex = reader.ReadByte();
                        var align = flags & MemoryAlignmentMask;
                        return subOpcode switch
                        {
                            WasmOpCodes.V128Store8Lane => new V128Store8LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            WasmOpCodes.V128Store16Lane => new V128Store16LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            WasmOpCodes.V128Store32Lane => new V128Store32LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            WasmOpCodes.V128Store64Lane => new V128Store64LaneInstruction(
                                align,
                                memoryIndex,
                                offset,
                                laneIndex
                            ),
                            _ => throw new InvalidOperationException(),
                        };
                    }
                    case >= WasmOpCodes.I8x16ExtractLaneS and <= WasmOpCodes.F64x2ReplaceLane:
                    {
                        var laneIndex = reader.ReadByte();
                        return subOpcode switch
                        {
                            WasmOpCodes.I8x16ExtractLaneS => new I8x16ExtractLaneSInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I8x16ExtractLaneU => new I8x16ExtractLaneUInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I8x16ReplaceLane => new I8x16ReplaceLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I16x8ExtractLaneS => new I16x8ExtractLaneSInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I16x8ExtractLaneU => new I16x8ExtractLaneUInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I16x8ReplaceLane => new I16x8ReplaceLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I32x4ExtractLane => new I32x4ExtractLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I32x4ReplaceLane => new I32x4ReplaceLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I64x2ExtractLane => new I64x2ExtractLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.I64x2ReplaceLane => new I64x2ReplaceLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.F32x4ExtractLane => new F32x4ExtractLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.F32x4ReplaceLane => new F32x4ReplaceLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.F64x2ExtractLane => new F64x2ExtractLaneInstruction(
                                laneIndex
                            ),
                            WasmOpCodes.F64x2ReplaceLane => new F64x2ReplaceLaneInstruction(
                                laneIndex
                            ),
                            _ => throw new InvalidOperationException(),
                        };
                    }
                    default:
                    {
                        return subOpcode switch
                        {
                            WasmOpCodes.I8x16Swizzle => new I8x16SwizzleInstruction(),
                            WasmOpCodes.I8x16Splat => new I8x16SplatInstruction(),
                            WasmOpCodes.I16x8Splat => new I16x8SplatInstruction(),
                            WasmOpCodes.I32x4Splat => new I32x4SplatInstruction(),
                            WasmOpCodes.I64x2Splat => new I64x2SplatInstruction(),
                            WasmOpCodes.F32x4Splat => new F32x4SplatInstruction(),
                            WasmOpCodes.F64x2Splat => new F64x2SplatInstruction(),
                            WasmOpCodes.I8x16Eq => new I8x16EqInstruction(),
                            WasmOpCodes.I8x16Ne => new I8x16NeInstruction(),
                            WasmOpCodes.I8x16LtS => new I8x16LtSInstruction(),
                            WasmOpCodes.I8x16LtU => new I8x16LtUInstruction(),
                            WasmOpCodes.I8x16GtS => new I8x16GtSInstruction(),
                            WasmOpCodes.I8x16GtU => new I8x16GtUInstruction(),
                            WasmOpCodes.I8x16LeS => new I8x16LeSInstruction(),
                            WasmOpCodes.I8x16LeU => new I8x16LeUInstruction(),
                            WasmOpCodes.I8x16GeS => new I8x16GeSInstruction(),
                            WasmOpCodes.I8x16GeU => new I8x16GeUInstruction(),
                            WasmOpCodes.I16x8Eq => new I16x8EqInstruction(),
                            WasmOpCodes.I16x8Ne => new I16x8NeInstruction(),
                            WasmOpCodes.I16x8LtS => new I16x8LtSInstruction(),
                            WasmOpCodes.I16x8LtU => new I16x8LtUInstruction(),
                            WasmOpCodes.I16x8GtS => new I16x8GtSInstruction(),
                            WasmOpCodes.I16x8GtU => new I16x8GtUInstruction(),
                            WasmOpCodes.I16x8LeS => new I16x8LeSInstruction(),
                            WasmOpCodes.I16x8LeU => new I16x8LeUInstruction(),
                            WasmOpCodes.I16x8GeS => new I16x8GeSInstruction(),
                            WasmOpCodes.I16x8GeU => new I16x8GeUInstruction(),
                            WasmOpCodes.I32x4Eq => new I32x4EqInstruction(),
                            WasmOpCodes.I32x4Ne => new I32x4NeInstruction(),
                            WasmOpCodes.I32x4LtS => new I32x4LtSInstruction(),
                            WasmOpCodes.I32x4LtU => new I32x4LtUInstruction(),
                            WasmOpCodes.I32x4GtS => new I32x4GtSInstruction(),
                            WasmOpCodes.I32x4GtU => new I32x4GtUInstruction(),
                            WasmOpCodes.I32x4LeS => new I32x4LeSInstruction(),
                            WasmOpCodes.I32x4LeU => new I32x4LeUInstruction(),
                            WasmOpCodes.I32x4GeS => new I32x4GeSInstruction(),
                            WasmOpCodes.I32x4GeU => new I32x4GeUInstruction(),
                            WasmOpCodes.F32x4Eq => new F32x4EqInstruction(),
                            WasmOpCodes.F32x4Ne => new F32x4NeInstruction(),
                            WasmOpCodes.F32x4Lt => new F32x4LtInstruction(),
                            WasmOpCodes.F32x4Gt => new F32x4GtInstruction(),
                            WasmOpCodes.F32x4Le => new F32x4LeInstruction(),
                            WasmOpCodes.F32x4Ge => new F32x4GeInstruction(),
                            WasmOpCodes.F64x2Eq => new F64x2EqInstruction(),
                            WasmOpCodes.F64x2Ne => new F64x2NeInstruction(),
                            WasmOpCodes.F64x2Lt => new F64x2LtInstruction(),
                            WasmOpCodes.F64x2Gt => new F64x2GtInstruction(),
                            WasmOpCodes.F64x2Le => new F64x2LeInstruction(),
                            WasmOpCodes.F64x2Ge => new F64x2GeInstruction(),
                            WasmOpCodes.I64x2Eq => new I64x2EqInstruction(),
                            WasmOpCodes.I64x2Ne => new I64x2NeInstruction(),
                            WasmOpCodes.I64x2LtS => new I64x2LtSInstruction(),
                            WasmOpCodes.I64x2GtS => new I64x2GtSInstruction(),
                            WasmOpCodes.I64x2LeS => new I64x2LeSInstruction(),
                            WasmOpCodes.I64x2GeS => new I64x2GeSInstruction(),
                            WasmOpCodes.V128Not => new V128NotInstruction(),
                            WasmOpCodes.V128And => new V128AndInstruction(),
                            WasmOpCodes.V128AndNot => new V128AndNotInstruction(),
                            WasmOpCodes.V128Or => new V128OrInstruction(),
                            WasmOpCodes.V128Xor => new V128XorInstruction(),
                            WasmOpCodes.V128BitSelect => new V128BitSelectInstruction(),
                            WasmOpCodes.V128AnyTrue => new V128AnyTrueInstruction(),
                            WasmOpCodes.F32x4DemoteF64x2Zero =>
                                new F32x4DemoteF64x2ZeroInstruction(),
                            WasmOpCodes.F64x2PromoteLowF32x4 =>
                                new F64x2PromoteLowF32x4Instruction(),
                            WasmOpCodes.I8x16Abs => new I8x16AbsInstruction(),
                            WasmOpCodes.I8x16Neg => new I8x16NegInstruction(),
                            WasmOpCodes.I8x16Popcnt => new I8x16PopcntInstruction(),
                            WasmOpCodes.I8x16AllTrue => new I8x16AllTrueInstruction(),
                            WasmOpCodes.I8x16Bitmask => new I8x16BitmaskInstruction(),
                            WasmOpCodes.I8x16NarrowI16x8S => new I8x16NarrowI16x8SInstruction(),
                            WasmOpCodes.I8x16NarrowI16x8U => new I8x16NarrowI16x8UInstruction(),
                            WasmOpCodes.I8x16Shl => new I8x16ShlInstruction(),
                            WasmOpCodes.I8x16ShrS => new I8x16ShrSInstruction(),
                            WasmOpCodes.I8x16ShrU => new I8x16ShrUInstruction(),
                            WasmOpCodes.I8x16Add => new I8x16AddInstruction(),
                            WasmOpCodes.I8x16AddSaturateS => new I8x16AddSaturateSInstruction(),
                            WasmOpCodes.I8x16AddSaturateU => new I8x16AddSaturateUInstruction(),
                            WasmOpCodes.I8x16Sub => new I8x16SubInstruction(),
                            WasmOpCodes.I8x16SubSaturateS => new I8x16SubSaturateSInstruction(),
                            WasmOpCodes.I8x16SubSaturateU => new I8x16SubSaturateUInstruction(),
                            WasmOpCodes.I8x16MinS => new I8x16MinSInstruction(),
                            WasmOpCodes.I8x16MinU => new I8x16MinUInstruction(),
                            WasmOpCodes.I8x16MaxS => new I8x16MaxSInstruction(),
                            WasmOpCodes.I8x16MaxU => new I8x16MaxUInstruction(),
                            WasmOpCodes.I8x16AvgrU => new I8x16AvgrUInstruction(),
                            WasmOpCodes.I16x8Abs => new I16x8AbsInstruction(),
                            WasmOpCodes.I16x8Neg => new I16x8NegInstruction(),
                            WasmOpCodes.I16x8Q15mulrSatS => new I16x8Q15mulrSatSInstruction(),
                            WasmOpCodes.I16x8AllTrue => new I16x8AllTrueInstruction(),
                            WasmOpCodes.I16x8Bitmask => new I16x8BitmaskInstruction(),
                            WasmOpCodes.I16x8NarrowI32x4S => new I16x8NarrowI32x4SInstruction(),
                            WasmOpCodes.I16x8NarrowI32x4U => new I16x8NarrowI32x4UInstruction(),
                            WasmOpCodes.I16x8ExtendLowI8x16S =>
                                new I16x8ExtendLowI8x16SInstruction(),
                            WasmOpCodes.I16x8ExtendHighI8x16S =>
                                new I16x8ExtendHighI8x16SInstruction(),
                            WasmOpCodes.I16x8ExtendLowI8x16U =>
                                new I16x8ExtendLowI8x16UInstruction(),
                            WasmOpCodes.I16x8ExtendHighI8x16U =>
                                new I16x8ExtendHighI8x16UInstruction(),
                            WasmOpCodes.I16x8Shl => new I16x8ShlInstruction(),
                            WasmOpCodes.I16x8ShrS => new I16x8ShrSInstruction(),
                            WasmOpCodes.I16x8ShrU => new I16x8ShrUInstruction(),
                            WasmOpCodes.I16x8Add => new I16x8AddInstruction(),
                            WasmOpCodes.I16x8AddSaturateS => new I16x8AddSaturateSInstruction(),
                            WasmOpCodes.I16x8AddSaturateU => new I16x8AddSaturateUInstruction(),
                            WasmOpCodes.I16x8Sub => new I16x8SubInstruction(),
                            WasmOpCodes.I16x8SubSaturateS => new I16x8SubSaturateSInstruction(),
                            WasmOpCodes.I16x8SubSaturateU => new I16x8SubSaturateUInstruction(),
                            WasmOpCodes.I16x8Mul => new I16x8MulInstruction(),
                            WasmOpCodes.I16x8MinS => new I16x8MinSInstruction(),
                            WasmOpCodes.I16x8MinU => new I16x8MinUInstruction(),
                            WasmOpCodes.I16x8MaxS => new I16x8MaxSInstruction(),
                            WasmOpCodes.I16x8MaxU => new I16x8MaxUInstruction(),
                            WasmOpCodes.I16x8AvgrU => new I16x8AvgrUInstruction(),
                            WasmOpCodes.I16x8ExtMulLowI8x16S =>
                                new I16x8ExtMulLowI8x16SInstruction(),
                            WasmOpCodes.I16x8ExtMulHighI8x16S =>
                                new I16x8ExtMulHighI8x16SInstruction(),
                            WasmOpCodes.I16x8ExtMulLowI8x16U =>
                                new I16x8ExtMulLowI8x16UInstruction(),
                            WasmOpCodes.I16x8ExtMulHighI8x16U =>
                                new I16x8ExtMulHighI8x16UInstruction(),
                            WasmOpCodes.I32x4Abs => new I32x4AbsInstruction(),
                            WasmOpCodes.I32x4Neg => new I32x4NegInstruction(),
                            WasmOpCodes.I32x4AllTrue => new I32x4AllTrueInstruction(),
                            WasmOpCodes.I32x4Bitmask => new I32x4BitmaskInstruction(),
                            WasmOpCodes.I32x4ExtendLowI16x8S =>
                                new I32x4ExtendLowI16x8SInstruction(),
                            WasmOpCodes.I32x4ExtendHighI16x8S =>
                                new I32x4ExtendHighI16x8SInstruction(),
                            WasmOpCodes.I32x4ExtendLowI16x8U =>
                                new I32x4ExtendLowI16x8UInstruction(),
                            WasmOpCodes.I32x4ExtendHighI16x8U =>
                                new I32x4ExtendHighI16x8UInstruction(),
                            WasmOpCodes.I32x4Shl => new I32x4ShlInstruction(),
                            WasmOpCodes.I32x4ShrS => new I32x4ShrSInstruction(),
                            WasmOpCodes.I32x4ShrU => new I32x4ShrUInstruction(),
                            WasmOpCodes.I32x4Add => new I32x4AddInstruction(),
                            WasmOpCodes.I32x4Sub => new I32x4SubInstruction(),
                            WasmOpCodes.I32x4Mul => new I32x4MulInstruction(),
                            WasmOpCodes.I32x4MinS => new I32x4MinSInstruction(),
                            WasmOpCodes.I32x4MinU => new I32x4MinUInstruction(),
                            WasmOpCodes.I32x4MaxS => new I32x4MaxSInstruction(),
                            WasmOpCodes.I32x4MaxU => new I32x4MaxUInstruction(),
                            WasmOpCodes.I32x4DotI16x8S => new I32x4DotI16x8SInstruction(),
                            WasmOpCodes.I32x4ExtMulLowI16x8S =>
                                new I32x4ExtMulLowI16x8SInstruction(),
                            WasmOpCodes.I32x4ExtMulHighI16x8S =>
                                new I32x4ExtMulHighI16x8SInstruction(),
                            WasmOpCodes.I32x4ExtMulLowI16x8U =>
                                new I32x4ExtMulLowI16x8UInstruction(),
                            WasmOpCodes.I32x4ExtMulHighI16x8U =>
                                new I32x4ExtMulHighI16x8UInstruction(),
                            WasmOpCodes.I64x2Abs => new I64x2AbsInstruction(),
                            WasmOpCodes.I64x2Neg => new I64x2NegInstruction(),
                            WasmOpCodes.I64x2AllTrue => new I64x2AllTrueInstruction(),
                            WasmOpCodes.I64x2Bitmask => new I64x2BitmaskInstruction(),
                            WasmOpCodes.I64x2ExtendLowI32x4S =>
                                new I64x2ExtendLowI32x4SInstruction(),
                            WasmOpCodes.I64x2ExtendHighI32x4S =>
                                new I64x2ExtendHighI32x4SInstruction(),
                            WasmOpCodes.I64x2ExtendLowI32x4U =>
                                new I64x2ExtendLowI32x4UInstruction(),
                            WasmOpCodes.I64x2ExtendHighI32x4U =>
                                new I64x2ExtendHighI32x4UInstruction(),
                            WasmOpCodes.I64x2Shl => new I64x2ShlInstruction(),
                            WasmOpCodes.I64x2ShrS => new I64x2ShrSInstruction(),
                            WasmOpCodes.I64x2ShrU => new I64x2ShrUInstruction(),
                            WasmOpCodes.I64x2Add => new I64x2AddInstruction(),
                            WasmOpCodes.I64x2Sub => new I64x2SubInstruction(),
                            WasmOpCodes.I64x2Mul => new I64x2MulInstruction(),
                            WasmOpCodes.I64x2ExtMulLowI32x4S =>
                                new I64x2ExtMulLowI32x4SInstruction(),
                            WasmOpCodes.I64x2ExtMulHighI32x4S =>
                                new I64x2ExtMulHighI32x4SInstruction(),
                            WasmOpCodes.I64x2ExtMulLowI32x4U =>
                                new I64x2ExtMulLowI32x4UInstruction(),
                            WasmOpCodes.I64x2ExtMulHighI32x4U =>
                                new I64x2ExtMulHighI32x4UInstruction(),
                            WasmOpCodes.F32x4Abs => new F32x4AbsInstruction(),
                            WasmOpCodes.F32x4Neg => new F32x4NegInstruction(),
                            WasmOpCodes.F32x4Sqrt => new F32x4SqrtInstruction(),
                            WasmOpCodes.F32x4Add => new F32x4AddInstruction(),
                            WasmOpCodes.F32x4Sub => new F32x4SubInstruction(),
                            WasmOpCodes.F32x4Mul => new F32x4MulInstruction(),
                            WasmOpCodes.F32x4Div => new F32x4DivInstruction(),
                            WasmOpCodes.F32x4Min => new F32x4MinInstruction(),
                            WasmOpCodes.F32x4Max => new F32x4MaxInstruction(),
                            WasmOpCodes.F32x4PMin => new F32x4PMinInstruction(),
                            WasmOpCodes.F32x4PMax => new F32x4PMaxInstruction(),
                            WasmOpCodes.F32x4Ceil => new F32x4CeilInstruction(),
                            WasmOpCodes.F32x4Floor => new F32x4FloorInstruction(),
                            WasmOpCodes.F32x4Trunc => new F32x4TruncInstruction(),
                            WasmOpCodes.F32x4Nearest => new F32x4NearestInstruction(),
                            WasmOpCodes.F64x2Abs => new F64x2AbsInstruction(),
                            WasmOpCodes.F64x2Neg => new F64x2NegInstruction(),
                            WasmOpCodes.F64x2Sqrt => new F64x2SqrtInstruction(),
                            WasmOpCodes.F64x2Add => new F64x2AddInstruction(),
                            WasmOpCodes.F64x2Sub => new F64x2SubInstruction(),
                            WasmOpCodes.F64x2Mul => new F64x2MulInstruction(),
                            WasmOpCodes.F64x2Div => new F64x2DivInstruction(),
                            WasmOpCodes.F64x2Min => new F64x2MinInstruction(),
                            WasmOpCodes.F64x2Max => new F64x2MaxInstruction(),
                            WasmOpCodes.F64x2PMin => new F64x2PMinInstruction(),
                            WasmOpCodes.F64x2PMax => new F64x2PMaxInstruction(),
                            WasmOpCodes.F64x2Ceil => new F64x2CeilInstruction(),
                            WasmOpCodes.F64x2Floor => new F64x2FloorInstruction(),
                            WasmOpCodes.F64x2Trunc => new F64x2TruncInstruction(),
                            WasmOpCodes.F64x2Nearest => new F64x2NearestInstruction(),
                            WasmOpCodes.I32x4TruncSatF32x4S => new I32x4TruncSatF32x4SInstruction(),
                            WasmOpCodes.I32x4TruncSatF32x4U => new I32x4TruncSatF32x4UInstruction(),
                            WasmOpCodes.F32x4ConvertI32x4S => new F32x4ConvertI32x4SInstruction(),
                            WasmOpCodes.F32x4ConvertI32x4U => new F32x4ConvertI32x4UInstruction(),
                            WasmOpCodes.I32x4TruncSatF64x2SZero =>
                                new I32x4TruncSatF64x2SZeroInstruction(),
                            WasmOpCodes.I32x4TruncSatF64x2UZero =>
                                new I32x4TruncSatF64x2UZeroInstruction(),
                            WasmOpCodes.F64x2ConvertLowI32x4S =>
                                new F64x2ConvertLowI32x4SInstruction(),
                            WasmOpCodes.F64x2ConvertLowI32x4U =>
                                new F64x2ConvertLowI32x4UInstruction(),
                            WasmOpCodes.I16x8ExtaddPairwiseI8x16S =>
                                new I16x8ExtaddPairwiseI8x16SInstruction(),
                            WasmOpCodes.I16x8ExtaddPairwiseI8x16U =>
                                new I16x8ExtaddPairwiseI8x16UInstruction(),
                            WasmOpCodes.I32x4ExtaddPairwiseI16x8S =>
                                new I32x4ExtaddPairwiseI16x8SInstruction(),
                            WasmOpCodes.I32x4ExtaddPairwiseI16x8U =>
                                new I32x4ExtaddPairwiseI16x8UInstruction(),
                            WasmOpCodes.I8x16RelaxedSwizzle => new I8x16RelaxedSwizzleInstruction(),
                            WasmOpCodes.I32x4RelaxedTruncF32x4S =>
                                new I32x4RelaxedTruncF32x4SInstruction(),
                            WasmOpCodes.I32x4RelaxedTruncF32x4U =>
                                new I32x4RelaxedTruncF32x4UInstruction(),
                            WasmOpCodes.I32x4RelaxedTruncF64x2SZero =>
                                new I32x4RelaxedTruncF64x2SZeroInstruction(),
                            WasmOpCodes.I32x4RelaxedTruncF64x2UZero =>
                                new I32x4RelaxedTruncF64x2UZeroInstruction(),
                            WasmOpCodes.F32x4RelaxedMAdd => new F32x4RelaxedMAddInstruction(),
                            WasmOpCodes.F32x4RelaxedNMAdd => new F32x4RelaxedNMAddInstruction(),
                            WasmOpCodes.F64x2RelaxedMAdd => new F64x2RelaxedMAddInstruction(),
                            WasmOpCodes.F64x2RelaxedNMAdd => new F64x2RelaxedNMAddInstruction(),
                            WasmOpCodes.I8x16RelaxedLaneSelect =>
                                new I8x16RelaxedLaneSelectInstruction(),
                            WasmOpCodes.I16x8RelaxedLaneSelect =>
                                new I16x8RelaxedLaneSelectInstruction(),
                            WasmOpCodes.I32x4RelaxedLaneSelect =>
                                new I32x4RelaxedLaneSelectInstruction(),
                            WasmOpCodes.I64x2RelaxedLaneSelect =>
                                new I64x2RelaxedLaneSelectInstruction(),
                            WasmOpCodes.F32x4RelaxedMin => new F32x4RelaxedMinInstruction(),
                            WasmOpCodes.F32x4RelaxedMax => new F32x4RelaxedMaxInstruction(),
                            WasmOpCodes.F64x2RelaxedMin => new F64x2RelaxedMinInstruction(),
                            WasmOpCodes.F64x2RelaxedMax => new F64x2RelaxedMaxInstruction(),
                            WasmOpCodes.I16x8RelaxedQ15MulrS =>
                                new I16x8RelaxedQ15MulrSInstruction(),
                            WasmOpCodes.I16x8RelaxedDotI8x16I7x16S =>
                                new I16x8RelaxedDotI8x16I7x16SInstruction(),
                            WasmOpCodes.I32x4RelaxedDotI8x16I7x16AddS =>
                                new I32x4RelaxedDotI8x16I7x16AddSInstruction(),
                            _ => throw new InvalidOperationException(
                                $"Unsupported SIMD opcode 0x{subOpcode:X}"
                            ),
                        };
                    }
                }
            }
            default:
                WasmDecodeException.Throw(
                    $"Unsupported WebAssembly instruction opcode 0x{opcode:x2}."
                );
                return null!;
        }
    }

    static FuncType ResolveBlockFuncType(BlockType blockType, ReadOnlySpan<FuncType?> types)
    {
        if (blockType.ValueType.HasValue)
            return new FuncType { Parameters = [], Results = [blockType.ValueType.Value] };
        if (blockType.TypeIndex.HasValue)
        {
            var idx = (int)blockType.TypeIndex.Value;
            if (idx >= types.Length)
                WasmDecodeException.Throw(
                    $"Block type index {idx} out of range (types count = {types.Length})."
                );
            return types[idx] ?? ThrowUnknownFunctionType();
        }
        return new FuncType { Parameters = [], Results = [] };
    }

    static (int ParamCount, int ResultCount) GetBlockArity(
        BlockType blockType,
        ReadOnlySpan<FuncType?> types
    )
    {
        if (blockType.ValueType.HasValue)
            return (0, 1);
        if (blockType.TypeIndex.HasValue)
        {
            var funcType = types[(int)blockType.TypeIndex.Value] ?? ThrowUnknownFunctionType();
            return (funcType.Parameters.Length, funcType.Results.Length);
        }
        return (0, 0);
    }

    static Instruction ReadGCInstruction(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var subOpcode = reader.ReadUInt32Leb128();
        return subOpcode switch
        {
            WasmOpCodes.StructNew => new StructNewInstruction(reader.ReadUInt32Leb128()),
            WasmOpCodes.StructNewDefault => new StructNewDefaultInstruction(
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.StructGet => new StructGetInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128(),
                Signedness.None
            ),
            WasmOpCodes.StructGetS => new StructGetInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128(),
                Signedness.Signed
            ),
            WasmOpCodes.StructGetU => new StructGetInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128(),
                Signedness.Unsigned
            ),
            WasmOpCodes.StructSet => new StructSetInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayNew => new ArrayNewInstruction(reader.ReadUInt32Leb128()),
            WasmOpCodes.ArrayNewDefault => new ArrayNewDefaultInstruction(
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayNewFixed => new ArrayNewFixedInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayNewData => new ArrayNewDataInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayNewElem => new ArrayNewElemInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayGet => new ArrayGetInstruction(
                reader.ReadUInt32Leb128(),
                Signedness.None
            ),
            WasmOpCodes.ArrayGetS => new ArrayGetInstruction(
                reader.ReadUInt32Leb128(),
                Signedness.Signed
            ),
            WasmOpCodes.ArrayGetU => new ArrayGetInstruction(
                reader.ReadUInt32Leb128(),
                Signedness.Unsigned
            ),
            WasmOpCodes.ArraySet => new ArraySetInstruction(reader.ReadUInt32Leb128()),
            WasmOpCodes.ArrayLen => new ArrayLenInstruction(),
            WasmOpCodes.ArrayFill => new ArrayFillInstruction(reader.ReadUInt32Leb128()),
            WasmOpCodes.ArrayCopy => new ArrayCopyInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayInitData => new ArrayInitDataInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.ArrayInitElem => new ArrayInitElemInstruction(
                reader.ReadUInt32Leb128(),
                reader.ReadUInt32Leb128()
            ),
            WasmOpCodes.RefTest => new RefTestInstruction(
                ReadHeapType(reader.ReadInt32Leb128(), types.Length, false)
            ),
            WasmOpCodes.RefTestNull => new RefTestInstruction(
                ReadHeapType(reader.ReadInt32Leb128(), types.Length, true)
            ),
            WasmOpCodes.RefCast => new RefCastInstruction(
                ReadHeapType(reader.ReadInt32Leb128(), types.Length, false)
            ),
            WasmOpCodes.RefCastNull => new RefCastInstruction(
                ReadHeapType(reader.ReadInt32Leb128(), types.Length, true)
            ),
            WasmOpCodes.BrOnCast => ReadBrOnCastInstruction(ref reader, types, isFail: false),
            WasmOpCodes.BrOnCastFail => ReadBrOnCastInstruction(ref reader, types, isFail: true),
            WasmOpCodes.AnyConvertExtern => new AnyConvertExternInstruction(),
            WasmOpCodes.ExternConvertAny => new ExternConvertAnyInstruction(),
            WasmOpCodes.RefI31 => new RefI31Instruction(),
            WasmOpCodes.I31GetS => new I31GetInstruction(Signedness.Signed),
            WasmOpCodes.I31GetU => new I31GetInstruction(Signedness.Unsigned),
            _ => ThrowGCInstruction(subOpcode),
        };
    }

    static Instruction ReadBrOnCastInstruction(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        bool isFail
    )
    {
        var flags = reader.ReadByte();
        if ((flags & 0xFC) != 0)
            WasmDecodeException.Throw("malformed br_on_cast flags");
        var labelIndex = reader.ReadUInt32Leb128();
        var source = ReadHeapType(reader.ReadInt32Leb128(), types.Length, (flags & 0x01) != 0);
        var target = ReadHeapType(reader.ReadInt32Leb128(), types.Length, (flags & 0x02) != 0);
        return isFail
            ? new BrOnCastFailInstruction(labelIndex, source, target)
            : new BrOnCastInstruction(labelIndex, source, target);
    }

    static CatchClause[] ReadCatchTable(ref SpanReader reader)
    {
        var count = reader.ReadUInt32Leb128();
        var clauses = new CatchClause[count];
        for (var i = 0; i < clauses.Length; i++)
        {
            var kind = reader.ReadByte();
            switch (kind)
            {
                case 0:
                case 1:
                {
                    var tagIndex = reader.ReadUInt32Leb128();
                    var labelIndex = reader.ReadUInt32Leb128();
                    clauses[i] = new CatchClause(kind, tagIndex, labelIndex);
                    break;
                }
                case 2:
                case 3:
                {
                    var labelIndex = reader.ReadUInt32Leb128();
                    clauses[i] = new CatchClause(kind, 0, labelIndex);
                    break;
                }
                default:
                    WasmDecodeException.Throw("Invalid catch clause.");
                    break;
            }
        }
        return clauses;
    }

    static string ReadName(ref SpanReader reader)
    {
        var length = checked((int)reader.ReadUInt32Leb128());
        var bytes = reader.ReadBytes(checked((int)length));
        if (!Utf8.IsValid(bytes))
            WasmDecodeException.Throw("malformed UTF-8 encoding");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    static WasmValueType ReadValueType(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        int typesLength
    )
    {
        var code = reader.ReadByte();
        return code switch
        {
            0x7F => WasmTypes.I32,
            0x7E => WasmTypes.I64,
            0x7D => WasmTypes.F32,
            0x7C => WasmTypes.F64,
            0x7B => WasmTypes.V128,
            0x70 => WasmTypes.FuncRef(true),
            0x71 => WasmTypes.NoneRef(true),
            0x72 => WasmTypes.NoExternRef(true),
            0x73 => WasmTypes.NoFuncRef(true),
            0x74 => WasmTypes.NoExnRef(true),
            0x6F => WasmTypes.ExternRef(true),
            0x6E => WasmTypes.AnyRef(true),
            0x6D => WasmTypes.EqRef(true),
            0x6C => WasmTypes.I31Ref(true),
            0x6B => WasmTypes.StructRef(true),
            0x6A => WasmTypes.ArrayRef(true),
            0x69 => WasmTypes.ExnRef(true),
            0x63 => ReadReferenceType(ref reader, typesLength, isNullable: true),
            0x64 => ReadReferenceType(ref reader, typesLength, isNullable: false),
            _ => ThrowValueType(),
        };
    }

    static WasmValueType ReadReferenceType(ref SpanReader reader, int typesLength, bool isNullable)
    {
        var heapType = reader.ReadInt32Leb128();
        return heapType switch
        {
            -0x0F => WasmTypes.NoneRef(isNullable),
            -0x0E => WasmTypes.NoExternRef(isNullable),
            -0x0D => WasmTypes.NoFuncRef(isNullable),
            -0x10 or 0x70 => WasmTypes.FuncRef(isNullable),
            -0x11 or 0x6F => WasmTypes.ExternRef(isNullable),
            -0x12 or 0x6E => WasmTypes.AnyRef(isNullable),
            -0x13 or 0x6D => WasmTypes.EqRef(isNullable),
            -0x14 or 0x6C => WasmTypes.I31Ref(isNullable),
            -0x15 or 0x6B => WasmTypes.StructRef(isNullable),
            -0x16 or 0x6A => WasmTypes.ArrayRef(isNullable),
            -0x17 or 0x69 => WasmTypes.ExnRef(isNullable),
            -0x0C or 0x74 => WasmTypes.NoExnRef(isNullable),
            0x71 => WasmTypes.NoneRef(isNullable),
            0x72 => WasmTypes.NoExternRef(isNullable),
            0x73 => WasmTypes.NoFuncRef(isNullable),
            >= 0 when heapType < typesLength => WasmTypes.ConcreteType((uint)heapType, isNullable),
            _ => ThrowValueType(),
        };
    }

    static bool IsReferenceTypeCode(byte code) =>
        code
            is 0x70
                or 0x6F
                or 0x6E
                or 0x6D
                or 0x6C
                or 0x6B
                or 0x6A
                or 0x69
                or 0x71
                or 0x72
                or 0x73
                or 0x74
                or 0x63
                or 0x64;

    static BlockType ReadBlockType(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        int typesLength
    )
    {
        var first = reader.ReadByte();
        if (first == 0x40)
            return new BlockType(null, null);
        if (
            first
            is 0x7F
                or 0x7E
                or 0x7D
                or 0x7C
                or 0x7B
                or 0x70
                or 0x6F
                or 0x6E
                or 0x6D
                or 0x6C
                or 0x6B
                or 0x6A
                or 0x69
                or 0x71
                or 0x72
                or 0x73
                or 0x74
        )
            return new BlockType(ValueTypeFromCode(first), null);
        if (first is 0x63 or 0x64)
        {
            var isNullable = first == 0x63;
            var heapType = reader.ReadInt32Leb128();
            return new BlockType(ReadHeapType(heapType, typesLength, isNullable), null);
        }
        uint typeIndex = (uint)(first & 0x7F);
        if ((first & 0x80) != 0)
        {
            var shift = 7;
            byte next;
            do
            {
                next = reader.ReadByte();
                typeIndex |= (uint)(next & 0x7F) << shift;
                shift += 7;
            } while ((next & 0x80) != 0);
        }
        return new BlockType(null, typeIndex);
    }

    static WasmValueType ValueTypeFromCode(byte code) =>
        code switch
        {
            0x7F => WasmTypes.I32,
            0x7E => WasmTypes.I64,
            0x7D => WasmTypes.F32,
            0x7C => WasmTypes.F64,
            0x7B => WasmTypes.V128,
            0x70 => WasmTypes.FuncRef(true),
            0x71 => WasmTypes.NoneRef(true),
            0x72 => WasmTypes.NoExternRef(true),
            0x73 => WasmTypes.NoFuncRef(true),
            0x74 => WasmTypes.NoExnRef(true),
            0x6F => WasmTypes.ExternRef(true),
            0x6E => WasmTypes.AnyRef(true),
            0x6D => WasmTypes.EqRef(true),
            0x6C => WasmTypes.I31Ref(true),
            0x6B => WasmTypes.StructRef(true),
            0x6A => WasmTypes.ArrayRef(true),
            0x69 => WasmTypes.ExnRef(true),
            _ => ThrowValueType(),
        };

    static WasmValueType ReadHeapType(int heapType, int typesLength, bool isNullable)
    {
        return heapType switch
        {
            -0x0F => WasmTypes.NoneRef(isNullable),
            -0x0E => WasmTypes.NoExternRef(isNullable),
            -0x0D => WasmTypes.NoFuncRef(isNullable),
            -0x10 or 0x70 => WasmTypes.FuncRef(isNullable),
            -0x11 or 0x6F => WasmTypes.ExternRef(isNullable),
            -0x12 or 0x6E => WasmTypes.AnyRef(isNullable),
            -0x13 or 0x6D => WasmTypes.EqRef(isNullable),
            -0x14 or 0x6C => WasmTypes.I31Ref(isNullable),
            -0x15 or 0x6B => WasmTypes.StructRef(isNullable),
            -0x16 or 0x6A => WasmTypes.ArrayRef(isNullable),
            -0x17 or 0x69 => WasmTypes.ExnRef(isNullable),
            -0x0C or 0x74 => WasmTypes.NoExnRef(isNullable),
            0x71 => WasmTypes.NoneRef(isNullable),
            0x72 => WasmTypes.NoExternRef(isNullable),
            0x73 => WasmTypes.NoFuncRef(isNullable),
            >= 0 when heapType < typesLength => WasmTypes.ConcreteType((uint)heapType, isNullable),
            _ => ThrowValueType(),
        };
    }

    static RecursiveType ReadRecursiveType(ref SpanReader reader, List<SubType> previousTypes)
    {
        if (reader.PeekByte() == 0x4E)
        {
            reader.ReadByte();
            var count = checked((int)reader.ReadUInt32Leb128());
            var subTypes = ImmutableArray.CreateBuilder<SubType>(count);
            var extendedTypes = new List<SubType>(previousTypes);
            for (var i = 0; i < count; i++)
                extendedTypes.Add(
                    new SubType
                    {
                        CompositeType = new FuncType { Parameters = [], Results = [] },
                    }
                );
            for (var i = 0; i < count; i++)
            {
                var subType = ReadSubType(ref reader, extendedTypes);
                subTypes.Add(subType);
                extendedTypes[previousTypes.Count + i] = subType;
            }
            return new RecursiveType { SubTypes = subTypes.MoveToImmutable() };
        }

        var extended = new List<SubType>(previousTypes)
        {
            new SubType
            {
                CompositeType = new FuncType { Parameters = [], Results = [] },
            },
        };
        var st = ReadSubType(ref reader, extended);
        return RecursiveType.From(st);
    }

    static SubType ReadSubType(ref SpanReader reader, List<SubType> previousTypes)
    {
        var marker = reader.PeekByte();
        var isFinal = true;
        ImmutableArray<uint> superTypes = [];
        if (marker is 0x4F or 0x50)
        {
            reader.ReadByte();
            isFinal = marker == 0x4F;
            superTypes = ReadUInt32Vector(ref reader).ToImmutableArray();
        }

        return new SubType
        {
            IsFinal = isFinal,
            SuperTypes = superTypes,
            CompositeType = ReadCompositeType(ref reader, previousTypes),
        };
    }

    static CompositeType ReadCompositeType(ref SpanReader reader, List<SubType> previousTypes)
    {
        var types = BuildFunctionTypeView(previousTypes);
        return reader.PeekByte() switch
        {
            0x60 => ReadFunctionType(ref reader, types, types.Length),
            0x5F => new StructType { Fields = ReadFieldTypeVector(ref reader, types) },
            0x5E => ReadArrayType(ref reader, types),
            _ => ThrowCompositeType(),
        };
    }

    static FuncType?[] BuildFunctionTypeView(List<SubType> previousTypes)
    {
        var types = new FuncType?[previousTypes.Count];
        for (var i = 0; i < previousTypes.Count; i++)
            if (previousTypes[i].CompositeType is FuncType funcType)
                types[i] = funcType;
        return types;
    }

    static ImmutableArray<FieldType> ReadFieldTypeVector(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types
    )
    {
        if (reader.ReadByte() != 0x5F)
            WasmDecodeException.Throw("Expected struct type tag 0x5f.");
        var count = checked((int)reader.ReadUInt32Leb128());
        var fields = ImmutableArray.CreateBuilder<FieldType>(count);
        for (var i = 0; i < count; i++)
            fields.Add(ReadFieldType(ref reader, types));
        return fields.MoveToImmutable();
    }

    static ArrayType ReadArrayType(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        if (reader.ReadByte() != 0x5E)
            WasmDecodeException.Throw("Expected array type tag 0x5e.");
        return new ArrayType { Field = ReadFieldType(ref reader, types) };
    }

    static FieldType ReadFieldType(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var storageType = ReadStorageType(ref reader, types);
        var mutability = reader.ReadByte();
        if (mutability > 1)
            WasmDecodeException.Throw("malformed mutability");
        return new FieldType { StorageType = storageType, Mutable = mutability == 1 };
    }

    static StorageType ReadStorageType(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        if (reader.PeekByte() == 0x78)
        {
            reader.ReadByte();
            return PackedType.I8;
        }
        if (reader.PeekByte() == 0x77)
        {
            reader.ReadByte();
            return PackedType.I16;
        }

        return ReadValueType(ref reader, types, types.Length);
    }

    static FuncType ReadFunctionType(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        int typesLength
    )
    {
        if (reader.ReadByte() != 0x60)
            WasmDecodeException.Throw("Expected function type tag 0x60.");
        var parameters = ReadValueTypeVector(ref reader, types, typesLength);
        var results = ReadValueTypeVector(ref reader, types, typesLength);
        return new FuncType
        {
            IsNullable = false,
            Parameters = parameters,
            Results = results,
        };
    }

    static ImmutableArray<WasmValueType> ReadValueTypeVector(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        int typesLength
    )
    {
        var count = reader.ReadUInt32Leb128();
        var builder = ImmutableArray.CreateBuilder<WasmValueType>(checked((int)count));
        for (var i = 0u; i < count; i++)
            builder.Add(ReadValueType(ref reader, types, typesLength));
        return builder.MoveToImmutable();
    }

    static ImmutableArray<WasmValueType> ReadLocals(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types
    )
    {
        var count = reader.ReadUInt32Leb128();
        var builder = ImmutableArray.CreateBuilder<WasmValueType>();
        var typesLen = types.Length;
        for (var i = 0u; i < count; i++)
        {
            var localCount = reader.ReadUInt32Leb128();
            var type = ReadValueType(ref reader, types, typesLen);
            if (localCount > int.MaxValue || builder.Count > int.MaxValue - (int)localCount)
                WasmDecodeException.Throw("Too many locals.");

            for (var j = 0; j < (int)localCount; j++)
                builder.Add(type);
        }
        return builder.ToImmutable();
    }

    static Import[] ReadImports(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var count = checked((int)reader.ReadUInt32Leb128());
        var imports = new Import[count];
        var typesLen = types.Length;
        var importIndex = 0;
        while (importIndex < count)
        {
            var module = ReadName(ref reader);
            var name = ReadName(ref reader);

            if (name.Length == 0 && reader.Length > 0)
            {
                var marker = reader.PeekByte();
                if (marker == 0x7F)
                {
                    reader.ReadByte(); // consume 0x7F marker
                    var subCount = checked((int)reader.ReadUInt32Leb128());
                    for (var j = 0; j < subCount; j++)
                    {
                        var fieldName = ReadName(ref reader);
                        var kind = (ImportExportKind)reader.ReadByte();
                        imports[importIndex++] = new Import
                        {
                            Module = module,
                            Name = fieldName,
                            Kind = kind,
                            Index = 0,
                            Type = ReadImportDesc(ref reader, types, typesLen, kind),
                        };
                    }
                    continue;
                }
                if (marker == 0x7E)
                {
                    reader.ReadByte(); // consume 0x7E marker
                    var kind = (ImportExportKind)reader.ReadByte();
                    var externalType = ReadImportDesc(ref reader, types, typesLen, kind);
                    var subCount = checked((int)reader.ReadUInt32Leb128());
                    for (var j = 0; j < subCount; j++)
                    {
                        var fieldName = ReadName(ref reader);
                        imports[importIndex++] = new Import
                        {
                            Module = module,
                            Name = fieldName,
                            Kind = kind,
                            Index = 0,
                            Type = externalType,
                        };
                    }
                    continue;
                }
            }

            var normalKind = (ImportExportKind)reader.ReadByte();
            imports[importIndex++] = new Import
            {
                Module = module,
                Name = name,
                Kind = normalKind,
                Index = 0,
                Type = ReadImportDesc(ref reader, types, typesLen, normalKind),
            };
        }
        return imports;
    }

    static ExternalType ReadImportDesc(
        ref SpanReader reader,
        ReadOnlySpan<FuncType?> types,
        int typesLen,
        ImportExportKind kind
    )
    {
        switch (kind)
        {
            case ImportExportKind.Function:
            {
                var typeIndex = (int)reader.ReadUInt32Leb128();
                if ((uint)typeIndex >= (uint)typesLen)
                    WasmDecodeException.Throw("Function type index is out of bounds.");
                var funcType = types[typeIndex] ?? ThrowUnknownFunctionType();
                return new FuncType
                {
                    TypeIndex = (uint)typeIndex,
                    IsNullable = false,
                    Parameters = funcType.Parameters,
                    Results = funcType.Results,
                };
            }
            case ImportExportKind.Table:
                return ReadTableType(ref reader, types);
            case ImportExportKind.Memory:
                return ReadMemoryType(ref reader);
            case ImportExportKind.Global:
            {
                var valueType = ReadValueType(ref reader, types, typesLen);
                var mutability = reader.ReadByte();
                if (mutability > 1)
                    WasmDecodeException.Throw("malformed mutability");
                return new GlobalType { ValueType = valueType, Mutable = mutability == 1 };
            }
            case ImportExportKind.Tag:
                return ReadTagType(ref reader, types);
            default:
                WasmDecodeException.Throw("Invalid external kind.");
                return null!;
        }
    }

    static TableType ReadTableType(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var hasInitExpr = false;
        if (reader.PeekByte() == 0x40)
        {
            reader.ReadByte();
            reader.ReadByte();
            hasInitExpr = true;
        }
        var elementType = ReadValueType(ref reader, types, types.Length);
        if (!elementType.IsRefType)
            WasmDecodeException.Throw("Table element type must be a reference type.");
        var (addressType, minimum, maximum) = ReadLimits(ref reader);
        Expression? initExpression = null;
        if (hasInitExpr)
            initExpression = ReadExpression(ref reader, types);
        return new TableType
        {
            AddressType = addressType,
            Minimum = minimum,
            Maximum = maximum,
            ElementType = elementType,
            InitExpression = initExpression,
        };
    }

    static MemoryType ReadMemoryType(ref SpanReader reader)
    {
        var (addressType, minimum, maximum) = ReadLimits(ref reader);
        return new MemoryType
        {
            AddressType = addressType,
            Minimum = minimum,
            Maximum = maximum,
        };
    }

    static Global ReadGlobal(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var valueType = ReadValueType(ref reader, types, types.Length);
        var mutability = reader.ReadByte();
        if (mutability > 1)
            WasmDecodeException.Throw("malformed mutability");
        var initExpression = ReadExpression(ref reader, types);
        return new Global
        {
            Type = new GlobalType { ValueType = valueType, Mutable = mutability == 1 },
            InitExpression = initExpression,
        };
    }

    static Export ReadExport(ref SpanReader reader)
    {
        var name = ReadName(ref reader);
        var kind = (ImportExportKind)reader.ReadByte();
        var index = reader.ReadUInt32Leb128();
        return new Export
        {
            Name = name,
            Kind = kind,
            Index = index,
        };
    }

    static Element ReadElement(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var flags = reader.ReadUInt32Leb128();
        var mode =
            (flags & 4) != 0
                ? (flags & 3) switch
                {
                    0 => ElementMode.Active,
                    1 => ElementMode.Passive,
                    2 => ElementMode.Active,
                    _ => ElementMode.Declarative,
                }
                : (flags & 3) switch
                {
                    0 => ElementMode.Active,
                    1 => ElementMode.Passive,
                    2 => ElementMode.Active,
                    _ => ElementMode.Declarative,
                };
        var typesLen = types.Length;
        WasmValueType elementType =
            (flags & 4) == 0 ? WasmTypes.FuncRef(false) : WasmTypes.FuncRef(true);
        Expression expression;
        uint tableIndex = 0;
        ImmutableArray<Expression> initializers;
        var hasTableIdx = (flags & 3) == 2;
        if ((flags & 4) != 0)
        {
            if (hasTableIdx)
                tableIndex = reader.ReadUInt32Leb128();
            if ((flags & 3) != 2)
            {
                if (reader.Length > 0 && IsReferenceTypeCode(reader.PeekByte()))
                    elementType = ReadValueType(ref reader, types, typesLen);
            }
        }
        if (mode == ElementMode.Active)
        {
            if ((flags & 4) == 0 && hasTableIdx)
                tableIndex = reader.ReadUInt32Leb128();
            expression = ReadExpression(ref reader, types);
            if ((flags & 4) != 0)
            {
                if (reader.Length > 0 && IsReferenceTypeCode(reader.PeekByte()))
                    elementType = ReadValueType(ref reader, types, typesLen);
            }
            if (
                (flags & 4) == 0
                && (flags & 3) != 0
                && reader.Length > 0
                && reader.PeekByte() == 0x00
            )
                reader.ReadByte();
        }
        else
        {
            expression = EmptyOffsetExpression();
            if ((flags & 4) == 0 && reader.Length > 0 && reader.PeekByte() == 0x00)
                reader.ReadByte();
        }
        if (reader.IsEmpty)
        {
            initializers = ImmutableArray<Expression>.Empty;
        }
        else
        {
            var initCount = reader.ReadUInt32Leb128();
            var initBuilder = ImmutableArray.CreateBuilder<Expression>(checked((int)initCount));
            for (var i = 0u; i < initCount; i++)
            {
                if ((flags & 4) != 0)
                    initBuilder.Add(ReadExpression(ref reader, types));
                else
                    initBuilder.Add(EncodeRefFuncExpression(reader.ReadUInt32Leb128()));
            }
            initializers = initBuilder.MoveToImmutable();
        }
        return new Element
        {
            Mode = mode,
            TableIndex = tableIndex,
            ElementType = elementType,
            Expression = expression,
            Initializers = initializers,
        };
    }

    static DataSegment ReadDataSegment(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        var flags = reader.ReadUInt32Leb128();
        DataSegmentMode mode;
        uint memoryIndex;
        Expression offsetExpression;
        if (flags == 0)
        {
            mode = DataSegmentMode.Active;
            memoryIndex = 0;
            offsetExpression = ReadExpression(ref reader, types);
        }
        else if (flags == 1)
        {
            mode = DataSegmentMode.Passive;
            memoryIndex = 0;
            offsetExpression = EmptyOffsetExpression();
        }
        else if (flags == 2)
        {
            mode = DataSegmentMode.Active;
            memoryIndex = reader.ReadUInt32Leb128();
            offsetExpression = ReadExpression(ref reader, types);
        }
        else
        {
            WasmDecodeException.Throw("Unsupported data segment flag.");
            return null!;
        }
        var dataLength = reader.ReadUInt32Leb128();
        var data = reader.ReadBytes(checked((int)dataLength));
        return new DataSegment
        {
            Mode = mode,
            MemoryIndex = memoryIndex,
            OffsetExpression = offsetExpression,
            Data = data.ToArray(),
        };
    }

    static TagType ReadTagType(ref SpanReader reader, ReadOnlySpan<FuncType?> types)
    {
        if (reader.ReadByte() != 0x00)
            WasmDecodeException.Throw("Expected tag attribute 0x00.");
        var typeIndex = reader.ReadUInt32Leb128();
        if (typeIndex >= types.Length)
            WasmDecodeException.Throw("Unknown function type for tag.");
        return new TagType
        {
            TypeIndex = typeIndex,
            Type = types[(int)typeIndex] ?? ThrowUnknownFunctionType(),
        };
    }

    static Expression EmptyOffsetExpression() =>
        new() { Instructions = [new I32ConstInstruction(0), new EndInstruction()] };

    static Expression EncodeRefFuncExpression(uint functionIndex)
    {
        return new()
        {
            Instructions = [new RefFuncInstruction(functionIndex), new EndInstruction()],
        };
    }

    static uint[] ReadUInt32Vector(ref SpanReader reader)
    {
        var count = checked((int)reader.ReadUInt32Leb128());
        if (count == 0)
            return [];
        var values = new uint[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = reader.ReadUInt32Leb128();
        return values;
    }

    static uint ReadMemArgOffset(
        ref SpanReader reader,
        uint memoryIndex,
        ReadOnlySpan<MemoryType> memories
    )
    {
        if (
            memoryIndex < memories.Length
            && memories[(int)memoryIndex].AddressType == AddressType.I64
        )
            return (uint)reader.ReadUInt64Leb128();
        return reader.ReadUInt32Leb128();
    }

    static (AddressType AddressType, ulong Minimum, ulong? Maximum) ReadLimits(
        ref SpanReader reader
    )
    {
        return reader.ReadByte() switch
        {
            0x00 => (AddressType.I32, reader.ReadUInt32Leb128(), null),
            0x01 => (AddressType.I32, reader.ReadUInt32Leb128(), reader.ReadUInt32Leb128()),
            0x04 => (AddressType.I64, reader.ReadUInt64Leb128(), null),
            0x05 => (AddressType.I64, reader.ReadUInt64Leb128(), reader.ReadUInt64Leb128()),
            _ => ThrowLimits(),
        };
    }

    static byte ReadExternalKind(ref SpanReader reader)
    {
        var kind = reader.ReadByte();
        if (kind > 0x04)
            WasmDecodeException.Throw("Invalid external kind.");
        return kind;
    }

    [DoesNotReturn]
    static WasmValueType ThrowValueType()
    {
        WasmDecodeException.Throw("Invalid value type.");
        return default;
    }

    [DoesNotReturn]
    static WasmValueType ThrowReferenceType()
    {
        WasmDecodeException.Throw("Invalid reference type.");
        return default;
    }

    [DoesNotReturn]
    static WasmValueType ThrowUnknownType()
    {
        WasmDecodeException.Throw("Unknown type.");
        return default;
    }

    [DoesNotReturn]
    static FuncType ThrowUnknownFunctionType()
    {
        WasmDecodeException.Throw("Unknown function type.");
        return default;
    }

    [DoesNotReturn]
    static CompositeType ThrowCompositeType()
    {
        WasmDecodeException.Throw("Invalid composite type.");
        return default;
    }

    [DoesNotReturn]
    static bool ThrowGlobalMutability()
    {
        WasmDecodeException.Throw("Unexpected global mutability value.");
        return default;
    }

    [DoesNotReturn]
    static uint ThrowExternalKind()
    {
        WasmDecodeException.Throw("Invalid external kind.");
        return default;
    }

    [DoesNotReturn]
    static (AddressType AddressType, ulong Minimum, ulong? Maximum) ThrowLimits()
    {
        WasmDecodeException.Throw("Invalid limits tag.");
        return default;
    }

    [DoesNotReturn]
    static ElementMode ThrowElementFlagMode()
    {
        WasmDecodeException.Throw("Unsupported element segment flag.");
        return default;
    }

    [DoesNotReturn]
    static uint ThrowElementFlag()
    {
        WasmDecodeException.Throw("Unsupported element segment flag.");
        return default;
    }

    [DoesNotReturn]
    static WasmValueType ThrowElementType()
    {
        WasmDecodeException.Throw("Unsupported element segment flag.");
        return default;
    }

    [DoesNotReturn]
    static Expression[] ThrowElementInitializers()
    {
        WasmDecodeException.Throw("Unsupported element segment flag.");
        return default;
    }

    [DoesNotReturn]
    static uint ThrowDataSegmentFlag()
    {
        WasmDecodeException.Throw("Unsupported data segment flag.");
        return default;
    }

    [DoesNotReturn]
    static void ThrowPrefixedInstruction(uint opcode)
    {
        WasmDecodeException.Throw(
            $"Unsupported WebAssembly prefixed instruction opcode 0x{WasmOpCodes.FCExtension:x2} {opcode}."
        );
    }

    [DoesNotReturn]
    static Instruction ThrowGCInstruction(uint opcode)
    {
        WasmDecodeException.Throw(
            $"Unsupported WebAssembly GC instruction opcode 0x{WasmOpCodes.GCExtension:x2} {opcode}."
        );
        return default;
    }

    static void EnsureEmpty(ref SpanReader reader)
    {
        if (!reader.IsEmpty)
            WasmDecodeException.Throw("Section payload has extra data.");
    }

    delegate T RefReaderFunc<T>(ref SpanReader reader);

    static T[] DecodeVector<T>(ref SpanReader reader, RefReaderFunc<T> read)
    {
        var count = checked((int)reader.ReadUInt32Leb128());
        if (count == 0)
            return [];

        var values = new T[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = read(ref reader);
        }
        return values;
    }
}
