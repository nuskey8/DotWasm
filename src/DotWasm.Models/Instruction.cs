using System.Collections.Immutable;

namespace DotWasm.Models;

public abstract record Instruction(byte OpCode);

// ─── Control flow ──────────────────────────────────────────────────────────────
public sealed record UnreachableInstruction() : Instruction(WasmOpCodes.Unreachable);

public sealed record NopInstruction() : Instruction(WasmOpCodes.Nop);

public sealed record BlockInstruction(int ParameterCount, int ResultCount)
    : Instruction(WasmOpCodes.Block)
{
    public int EndIndex { get; set; }
    public ImmutableArray<WasmValueType> ParameterTypes { get; init; }
    public ImmutableArray<WasmValueType> ResultTypes { get; init; }
}

public sealed record LoopInstruction(int ParameterCount, int ResultCount)
    : Instruction(WasmOpCodes.Loop)
{
    public int EndIndex { get; set; }
    public ImmutableArray<WasmValueType> ParameterTypes { get; init; }
    public ImmutableArray<WasmValueType> ResultTypes { get; init; }
}

public sealed record IfInstruction(int ParameterCount, int ResultCount)
    : Instruction(WasmOpCodes.If)
{
    public int ElseIndex { get; set; } = -1;
    public int EndIndex { get; set; }
    public ImmutableArray<WasmValueType> ParameterTypes { get; init; }
    public ImmutableArray<WasmValueType> ResultTypes { get; init; }
}

public sealed record ElseInstruction() : Instruction(WasmOpCodes.Else);

public sealed record TryInstruction(int ParameterCount, int ResultCount)
    : Instruction(WasmOpCodes.Try)
{
    public int EndIndex { get; set; }
    public ImmutableArray<WasmValueType> ParameterTypes { get; init; }
    public ImmutableArray<WasmValueType> ResultTypes { get; init; }
}

public sealed record CatchInstruction(uint TagIndex) : Instruction(WasmOpCodes.Catch);

public sealed record ThrowInstruction(uint TagIndex) : Instruction(WasmOpCodes.Throw);

public sealed record RethrowInstruction(uint LabelIndex) : Instruction(WasmOpCodes.Rethrow);

public sealed record ThrowRefInstruction() : Instruction(WasmOpCodes.ThrowRef);

public sealed record EndInstruction() : Instruction(WasmOpCodes.End);

public readonly record struct CatchClause(byte Kind, uint TagIndex, uint LabelIndex);

public sealed record TryTableInstruction(
    int ParameterCount,
    int ResultCount,
    CatchClause[] CatchTable
) : Instruction(WasmOpCodes.TryTable)
{
    public int EndIndex { get; set; }
    public ImmutableArray<WasmValueType> ParameterTypes { get; init; }
    public ImmutableArray<WasmValueType> ResultTypes { get; init; }
}

public sealed record BrInstruction(uint LabelIndex) : Instruction(WasmOpCodes.Br);

public sealed record BrIfInstruction(uint LabelIndex) : Instruction(WasmOpCodes.BrIf);

public sealed record BrTableInstruction(ImmutableArray<uint> LabelIndices, uint DefaultLabelIndex)
    : Instruction(WasmOpCodes.BrTable);

public sealed record ReturnInstruction() : Instruction(WasmOpCodes.Return);

public sealed record CallInstruction(uint FunctionIndex) : Instruction(WasmOpCodes.Call);

public sealed record CallIndirectInstruction(uint TypeIndex, uint TableIndex)
    : Instruction(WasmOpCodes.CallIndirect);

public sealed record ReturnCallInstruction(uint FunctionIndex)
    : Instruction(WasmOpCodes.ReturnCall);

public sealed record ReturnCallIndirectInstruction(uint TypeIndex, uint TableIndex)
    : Instruction(WasmOpCodes.ReturnCallIndirect);

public sealed record CallRefInstruction(uint TypeIndex) : Instruction(WasmOpCodes.CallRef);

public sealed record ReturnCallRefInstruction(uint TypeIndex)
    : Instruction(WasmOpCodes.ReturnCallRef);

public sealed record DelegateInstruction(uint LabelIndex) : Instruction(WasmOpCodes.Delegate);

public sealed record CatchAllInstruction() : Instruction(WasmOpCodes.CatchAll);

public sealed record DropInstruction() : Instruction(WasmOpCodes.Drop);

public sealed record SelectInstruction() : Instruction(WasmOpCodes.Select);

public sealed record SelectTInstruction(ImmutableArray<WasmValueType> Types)
    : Instruction(WasmOpCodes.SelectT);

public enum Signedness : byte
{
    None,
    Signed,
    Unsigned,
}

// ═══════════════════════════════════════════════════════════════════════════════
// 0xFB extension (GC)
// ═══════════════════════════════════════════════════════════════════════════════
public abstract record GCExtensionInstruction(uint ExtensionCode)
    : Instruction(WasmOpCodes.GCExtension);

public sealed record StructNewInstruction(uint TypeIndex)
    : GCExtensionInstruction(WasmOpCodes.StructNew);

public sealed record StructNewDefaultInstruction(uint TypeIndex)
    : GCExtensionInstruction(WasmOpCodes.StructNewDefault);

public sealed record StructGetInstruction(uint TypeIndex, uint FieldIndex, Signedness Signedness)
    : GCExtensionInstruction(
        Signedness switch
        {
            Signedness.Signed => WasmOpCodes.StructGetS,
            Signedness.Unsigned => WasmOpCodes.StructGetU,
            _ => WasmOpCodes.StructGet,
        }
    );

public sealed record StructSetInstruction(uint TypeIndex, uint FieldIndex)
    : GCExtensionInstruction(WasmOpCodes.StructSet);

public sealed record ArrayNewInstruction(uint TypeIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayNew);

public sealed record ArrayNewDefaultInstruction(uint TypeIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayNewDefault);

public sealed record ArrayNewFixedInstruction(uint TypeIndex, uint Length)
    : GCExtensionInstruction(WasmOpCodes.ArrayNewFixed);

public sealed record ArrayNewDataInstruction(uint TypeIndex, uint DataIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayNewData);

public sealed record ArrayNewElemInstruction(uint TypeIndex, uint ElementIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayNewElem);

public sealed record ArrayGetInstruction(uint TypeIndex, Signedness Signedness)
    : GCExtensionInstruction(
        Signedness switch
        {
            Signedness.Signed => WasmOpCodes.ArrayGetS,
            Signedness.Unsigned => WasmOpCodes.ArrayGetU,
            _ => WasmOpCodes.ArrayGet,
        }
    );

public sealed record ArraySetInstruction(uint TypeIndex)
    : GCExtensionInstruction(WasmOpCodes.ArraySet);

public sealed record ArrayLenInstruction() : GCExtensionInstruction(WasmOpCodes.ArrayLen);

public sealed record ArrayFillInstruction(uint TypeIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayFill);

public sealed record ArrayCopyInstruction(uint DestinationTypeIndex, uint SourceTypeIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayCopy);

public sealed record ArrayInitDataInstruction(uint TypeIndex, uint DataIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayInitData);

public sealed record ArrayInitElemInstruction(uint TypeIndex, uint ElementIndex)
    : GCExtensionInstruction(WasmOpCodes.ArrayInitElem);

public sealed record RefTestInstruction(WasmValueType ReferenceType)
    : GCExtensionInstruction(
        ReferenceType.IsNullable ? WasmOpCodes.RefTestNull : WasmOpCodes.RefTest
    );

public sealed record RefCastInstruction(WasmValueType ReferenceType)
    : GCExtensionInstruction(
        ReferenceType.IsNullable ? WasmOpCodes.RefCastNull : WasmOpCodes.RefCast
    );

public sealed record BrOnCastInstruction(
    uint LabelIndex,
    WasmValueType SourceReferenceType,
    WasmValueType TargetReferenceType
) : GCExtensionInstruction(WasmOpCodes.BrOnCast);

public sealed record BrOnCastFailInstruction(
    uint LabelIndex,
    WasmValueType SourceReferenceType,
    WasmValueType TargetReferenceType
) : GCExtensionInstruction(WasmOpCodes.BrOnCastFail);

public sealed record AnyConvertExternInstruction()
    : GCExtensionInstruction(WasmOpCodes.AnyConvertExtern);

public sealed record ExternConvertAnyInstruction()
    : GCExtensionInstruction(WasmOpCodes.ExternConvertAny);

public sealed record RefI31Instruction() : GCExtensionInstruction(WasmOpCodes.RefI31);

public sealed record I31GetInstruction(Signedness Signedness)
    : GCExtensionInstruction(
        Signedness == Signedness.Unsigned ? WasmOpCodes.I31GetU : WasmOpCodes.I31GetS
    );

// ─── Variables ─────────────────────────────────────────────────────────────────
public sealed record LocalGetInstruction(uint LocalIndex) : Instruction(WasmOpCodes.LocalGet);

public sealed record LocalSetInstruction(uint LocalIndex) : Instruction(WasmOpCodes.LocalSet);

public sealed record LocalTeeInstruction(uint LocalIndex) : Instruction(WasmOpCodes.LocalTee);

public sealed record GlobalGetInstruction(uint GlobalIndex) : Instruction(WasmOpCodes.GlobalGet);

public sealed record GlobalSetInstruction(uint GlobalIndex) : Instruction(WasmOpCodes.GlobalSet);

// ─── Tables ────────────────────────────────────────────────────────────────────
public sealed record TableGetInstruction(uint TableIndex) : Instruction(WasmOpCodes.TableGet);

public sealed record TableSetInstruction(uint TableIndex) : Instruction(WasmOpCodes.TableSet);

// ─── Memory ────────────────────────────────────────────────────────────────────
public sealed record MemorySizeInstruction(uint MemoryIndex) : Instruction(WasmOpCodes.MemorySize);

public sealed record MemoryGrowInstruction(uint MemoryIndex) : Instruction(WasmOpCodes.MemoryGrow);

// ─── Memory load/store ─────────────────────────────────────────────────────────
public sealed record I32LoadInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Load);

public sealed record I64LoadInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load);

public sealed record F32LoadInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.F32Load);

public sealed record F64LoadInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.F64Load);

public sealed record I32Load8SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Load8S);

public sealed record I32Load8UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Load8U);

public sealed record I32Load16SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Load16S);

public sealed record I32Load16UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Load16U);

public sealed record I64Load8SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load8S);

public sealed record I64Load8UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load8U);

public sealed record I64Load16SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load16S);

public sealed record I64Load16UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load16U);

public sealed record I64Load32SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load32S);

public sealed record I64Load32UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Load32U);

public sealed record I32StoreInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Store);

public sealed record I64StoreInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Store);

public sealed record F32StoreInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.F32Store);

public sealed record F64StoreInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.F64Store);

public sealed record I32Store8Instruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Store8);

public sealed record I32Store16Instruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I32Store16);

public sealed record I64Store8Instruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Store8);

public sealed record I64Store16Instruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Store16);

public sealed record I64Store32Instruction(uint Alignment, uint MemoryIndex, uint Offset)
    : Instruction(WasmOpCodes.I64Store32);

// ─── Constants ─────────────────────────────────────────────────────────────────
public sealed record I32ConstInstruction(int Value) : Instruction(WasmOpCodes.I32Const);

public sealed record I64ConstInstruction(long Value) : Instruction(WasmOpCodes.I64Const);

public sealed record F32ConstInstruction(float Value) : Instruction(WasmOpCodes.F32Const);

public sealed record F64ConstInstruction(double Value) : Instruction(WasmOpCodes.F64Const);

// ─── Comparisons (i32) ─────────────────────────────────────────────────────────
public sealed record I32EqzInstruction() : Instruction(WasmOpCodes.I32Eqz);

public sealed record I32EqInstruction() : Instruction(WasmOpCodes.I32Eq);

public sealed record I32NeInstruction() : Instruction(WasmOpCodes.I32Ne);

public sealed record I32LtSInstruction() : Instruction(WasmOpCodes.I32LtS);

public sealed record I32LtUInstruction() : Instruction(WasmOpCodes.I32LtU);

public sealed record I32GtSInstruction() : Instruction(WasmOpCodes.I32GtS);

public sealed record I32GtUInstruction() : Instruction(WasmOpCodes.I32GtU);

public sealed record I32LeSInstruction() : Instruction(WasmOpCodes.I32LeS);

public sealed record I32LeUInstruction() : Instruction(WasmOpCodes.I32LeU);

public sealed record I32GeSInstruction() : Instruction(WasmOpCodes.I32GeS);

public sealed record I32GeUInstruction() : Instruction(WasmOpCodes.I32GeU);

// ─── Comparisons (i64) ─────────────────────────────────────────────────────────
public sealed record I64EqzInstruction() : Instruction(WasmOpCodes.I64Eqz);

public sealed record I64EqInstruction() : Instruction(WasmOpCodes.I64Eq);

public sealed record I64NeInstruction() : Instruction(WasmOpCodes.I64Ne);

public sealed record I64LtSInstruction() : Instruction(WasmOpCodes.I64LtS);

public sealed record I64LtUInstruction() : Instruction(WasmOpCodes.I64LtU);

public sealed record I64GtSInstruction() : Instruction(WasmOpCodes.I64GtS);

public sealed record I64GtUInstruction() : Instruction(WasmOpCodes.I64GtU);

public sealed record I64LeSInstruction() : Instruction(WasmOpCodes.I64LeS);

public sealed record I64LeUInstruction() : Instruction(WasmOpCodes.I64LeU);

public sealed record I64GeSInstruction() : Instruction(WasmOpCodes.I64GeS);

public sealed record I64GeUInstruction() : Instruction(WasmOpCodes.I64GeU);

// ─── Comparisons (f32) ─────────────────────────────────────────────────────────
public sealed record F32EqInstruction() : Instruction(WasmOpCodes.F32Eq);

public sealed record F32NeInstruction() : Instruction(WasmOpCodes.F32Ne);

public sealed record F32LtInstruction() : Instruction(WasmOpCodes.F32Lt);

public sealed record F32GtInstruction() : Instruction(WasmOpCodes.F32Gt);

public sealed record F32LeInstruction() : Instruction(WasmOpCodes.F32Le);

public sealed record F32GeInstruction() : Instruction(WasmOpCodes.F32Ge);

// ─── Comparisons (f64) ─────────────────────────────────────────────────────────
public sealed record F64EqInstruction() : Instruction(WasmOpCodes.F64Eq);

public sealed record F64NeInstruction() : Instruction(WasmOpCodes.F64Ne);

public sealed record F64LtInstruction() : Instruction(WasmOpCodes.F64Lt);

public sealed record F64GtInstruction() : Instruction(WasmOpCodes.F64Gt);

public sealed record F64LeInstruction() : Instruction(WasmOpCodes.F64Le);

public sealed record F64GeInstruction() : Instruction(WasmOpCodes.F64Ge);

// ─── Arithmetic (i32) ──────────────────────────────────────────────────────────
public sealed record I32ClzInstruction() : Instruction(WasmOpCodes.I32Clz);

public sealed record I32CtzInstruction() : Instruction(WasmOpCodes.I32Ctz);

public sealed record I32PopcntInstruction() : Instruction(WasmOpCodes.I32Popcnt);

public sealed record I32AddInstruction() : Instruction(WasmOpCodes.I32Add);

public sealed record I32SubInstruction() : Instruction(WasmOpCodes.I32Sub);

public sealed record I32MulInstruction() : Instruction(WasmOpCodes.I32Mul);

public sealed record I32DivSInstruction() : Instruction(WasmOpCodes.I32DivS);

public sealed record I32DivUInstruction() : Instruction(WasmOpCodes.I32DivU);

public sealed record I32RemSInstruction() : Instruction(WasmOpCodes.I32RemS);

public sealed record I32RemUInstruction() : Instruction(WasmOpCodes.I32RemU);

public sealed record I32AndInstruction() : Instruction(WasmOpCodes.I32And);

public sealed record I32OrInstruction() : Instruction(WasmOpCodes.I32Or);

public sealed record I32XorInstruction() : Instruction(WasmOpCodes.I32Xor);

public sealed record I32ShlInstruction() : Instruction(WasmOpCodes.I32Shl);

public sealed record I32ShrSInstruction() : Instruction(WasmOpCodes.I32ShrS);

public sealed record I32ShrUInstruction() : Instruction(WasmOpCodes.I32ShrU);

public sealed record I32RotlInstruction() : Instruction(WasmOpCodes.I32Rotl);

public sealed record I32RotrInstruction() : Instruction(WasmOpCodes.I32Rotr);

// ─── Arithmetic (i64) ──────────────────────────────────────────────────────────
public sealed record I64ClzInstruction() : Instruction(WasmOpCodes.I64Clz);

public sealed record I64CtzInstruction() : Instruction(WasmOpCodes.I64Ctz);

public sealed record I64PopcntInstruction() : Instruction(WasmOpCodes.I64Popcnt);

public sealed record I64AddInstruction() : Instruction(WasmOpCodes.I64Add);

public sealed record I64SubInstruction() : Instruction(WasmOpCodes.I64Sub);

public sealed record I64MulInstruction() : Instruction(WasmOpCodes.I64Mul);

public sealed record I64DivSInstruction() : Instruction(WasmOpCodes.I64DivS);

public sealed record I64DivUInstruction() : Instruction(WasmOpCodes.I64DivU);

public sealed record I64RemSInstruction() : Instruction(WasmOpCodes.I64RemS);

public sealed record I64RemUInstruction() : Instruction(WasmOpCodes.I64RemU);

public sealed record I64AndInstruction() : Instruction(WasmOpCodes.I64And);

public sealed record I64OrInstruction() : Instruction(WasmOpCodes.I64Or);

public sealed record I64XorInstruction() : Instruction(WasmOpCodes.I64Xor);

public sealed record I64ShlInstruction() : Instruction(WasmOpCodes.I64Shl);

public sealed record I64ShrSInstruction() : Instruction(WasmOpCodes.I64ShrS);

public sealed record I64ShrUInstruction() : Instruction(WasmOpCodes.I64ShrU);

public sealed record I64RotlInstruction() : Instruction(WasmOpCodes.I64Rotl);

public sealed record I64RotrInstruction() : Instruction(WasmOpCodes.I64Rotr);

// ─── Arithmetic (f32) ──────────────────────────────────────────────────────────
public sealed record F32AbsInstruction() : Instruction(WasmOpCodes.F32Abs);

public sealed record F32NegInstruction() : Instruction(WasmOpCodes.F32Neg);

public sealed record F32CeilInstruction() : Instruction(WasmOpCodes.F32Ceil);

public sealed record F32FloorInstruction() : Instruction(WasmOpCodes.F32Floor);

public sealed record F32TruncInstruction() : Instruction(WasmOpCodes.F32Trunc);

public sealed record F32NearestInstruction() : Instruction(WasmOpCodes.F32Nearest);

public sealed record F32SqrtInstruction() : Instruction(WasmOpCodes.F32Sqrt);

public sealed record F32AddInstruction() : Instruction(WasmOpCodes.F32Add);

public sealed record F32SubInstruction() : Instruction(WasmOpCodes.F32Sub);

public sealed record F32MulInstruction() : Instruction(WasmOpCodes.F32Mul);

public sealed record F32DivInstruction() : Instruction(WasmOpCodes.F32Div);

public sealed record F32MinInstruction() : Instruction(WasmOpCodes.F32Min);

public sealed record F32MaxInstruction() : Instruction(WasmOpCodes.F32Max);

public sealed record F32CopysignInstruction() : Instruction(WasmOpCodes.F32Copysign);

// ─── Arithmetic (f64) ──────────────────────────────────────────────────────────
public sealed record F64AbsInstruction() : Instruction(WasmOpCodes.F64Abs);

public sealed record F64NegInstruction() : Instruction(WasmOpCodes.F64Neg);

public sealed record F64CeilInstruction() : Instruction(WasmOpCodes.F64Ceil);

public sealed record F64FloorInstruction() : Instruction(WasmOpCodes.F64Floor);

public sealed record F64TruncInstruction() : Instruction(WasmOpCodes.F64Trunc);

public sealed record F64NearestInstruction() : Instruction(WasmOpCodes.F64Nearest);

public sealed record F64SqrtInstruction() : Instruction(WasmOpCodes.F64Sqrt);

public sealed record F64AddInstruction() : Instruction(WasmOpCodes.F64Add);

public sealed record F64SubInstruction() : Instruction(WasmOpCodes.F64Sub);

public sealed record F64MulInstruction() : Instruction(WasmOpCodes.F64Mul);

public sealed record F64DivInstruction() : Instruction(WasmOpCodes.F64Div);

public sealed record F64MinInstruction() : Instruction(WasmOpCodes.F64Min);

public sealed record F64MaxInstruction() : Instruction(WasmOpCodes.F64Max);

public sealed record F64CopysignInstruction() : Instruction(WasmOpCodes.F64Copysign);

// ─── Conversions ───────────────────────────────────────────────────────────────
public sealed record I32WrapI64Instruction() : Instruction(WasmOpCodes.I32WrapI64);

public sealed record I32TruncF32SInstruction() : Instruction(WasmOpCodes.I32TruncF32S);

public sealed record I32TruncF32UInstruction() : Instruction(WasmOpCodes.I32TruncF32U);

public sealed record I32TruncF64SInstruction() : Instruction(WasmOpCodes.I32TruncF64S);

public sealed record I32TruncF64UInstruction() : Instruction(WasmOpCodes.I32TruncF64U);

public sealed record I64ExtendI32SInstruction() : Instruction(WasmOpCodes.I64ExtendI32S);

public sealed record I64ExtendI32UInstruction() : Instruction(WasmOpCodes.I64ExtendI32U);

public sealed record I64TruncF32SInstruction() : Instruction(WasmOpCodes.I64TruncF32S);

public sealed record I64TruncF32UInstruction() : Instruction(WasmOpCodes.I64TruncF32U);

public sealed record I64TruncF64SInstruction() : Instruction(WasmOpCodes.I64TruncF64S);

public sealed record I64TruncF64UInstruction() : Instruction(WasmOpCodes.I64TruncF64U);

public sealed record F32ConvertI32SInstruction() : Instruction(WasmOpCodes.F32ConvertI32S);

public sealed record F32ConvertI32UInstruction() : Instruction(WasmOpCodes.F32ConvertI32U);

public sealed record F32ConvertI64SInstruction() : Instruction(WasmOpCodes.F32ConvertI64S);

public sealed record F32ConvertI64UInstruction() : Instruction(WasmOpCodes.F32ConvertI64U);

public sealed record F32DemoteF64Instruction() : Instruction(WasmOpCodes.F32DemoteF64);

public sealed record F64ConvertI32SInstruction() : Instruction(WasmOpCodes.F64ConvertI32S);

public sealed record F64ConvertI32UInstruction() : Instruction(WasmOpCodes.F64ConvertI32U);

public sealed record F64ConvertI64SInstruction() : Instruction(WasmOpCodes.F64ConvertI64S);

public sealed record F64ConvertI64UInstruction() : Instruction(WasmOpCodes.F64ConvertI64U);

public sealed record F64PromoteF32Instruction() : Instruction(WasmOpCodes.F64PromoteF32);

// ─── Reinterpret ───────────────────────────────────────────────────────────────
public sealed record I32ReinterpretF32Instruction() : Instruction(WasmOpCodes.I32ReinterpretF32);

public sealed record I64ReinterpretF64Instruction() : Instruction(WasmOpCodes.I64ReinterpretF64);

public sealed record F32ReinterpretI32Instruction() : Instruction(WasmOpCodes.F32ReinterpretI32);

public sealed record F64ReinterpretI64Instruction() : Instruction(WasmOpCodes.F64ReinterpretI64);

// ─── Sign extension ────────────────────────────────────────────────────────────
public sealed record I32Extend8SInstruction() : Instruction(WasmOpCodes.I32Extend8S);

public sealed record I32Extend16SInstruction() : Instruction(WasmOpCodes.I32Extend16S);

public sealed record I64Extend8SInstruction() : Instruction(WasmOpCodes.I64Extend8S);

public sealed record I64Extend16SInstruction() : Instruction(WasmOpCodes.I64Extend16S);

public sealed record I64Extend32SInstruction() : Instruction(WasmOpCodes.I64Extend32S);

// ─── Reference types ───────────────────────────────────────────────────────────
public sealed record RefNullInstruction(WasmValueType Type) : Instruction(WasmOpCodes.RefNull);

public sealed record RefIsNullInstruction() : Instruction(WasmOpCodes.RefIsNull);

public sealed record RefFuncInstruction(uint FunctionIndex) : Instruction(WasmOpCodes.RefFunc);

public sealed record RefEqInstruction() : Instruction(WasmOpCodes.RefEq);

public sealed record RefAsNonNullInstruction() : Instruction(WasmOpCodes.RefAsNonNull);

public sealed record BrOnNullInstruction(uint LabelIndex) : Instruction(WasmOpCodes.BrOnNull);

public sealed record BrOnNonNullInstruction(uint LabelIndex) : Instruction(WasmOpCodes.BrOnNonNull);

// ═══════════════════════════════════════════════════════════════════════════════
// 0xFC extension (atomic / misc)
// ═══════════════════════════════════════════════════════════════════════════════
public abstract record FCExtensionInstruction(uint ExtensionCode)
    : Instruction(WasmOpCodes.FCExtension);

public sealed record I32TruncSatF32SInstruction()
    : FCExtensionInstruction(WasmOpCodes.I32TruncSatF32S);

public sealed record I32TruncSatF32UInstruction()
    : FCExtensionInstruction(WasmOpCodes.I32TruncSatF32U);

public sealed record I32TruncSatF64SInstruction()
    : FCExtensionInstruction(WasmOpCodes.I32TruncSatF64S);

public sealed record I32TruncSatF64UInstruction()
    : FCExtensionInstruction(WasmOpCodes.I32TruncSatF64U);

public sealed record I64TruncSatF32SInstruction()
    : FCExtensionInstruction(WasmOpCodes.I64TruncSatF32S);

public sealed record I64TruncSatF32UInstruction()
    : FCExtensionInstruction(WasmOpCodes.I64TruncSatF32U);

public sealed record I64TruncSatF64SInstruction()
    : FCExtensionInstruction(WasmOpCodes.I64TruncSatF64S);

public sealed record I64TruncSatF64UInstruction()
    : FCExtensionInstruction(WasmOpCodes.I64TruncSatF64U);

public sealed record MemoryInitInstruction(uint DataIndex, uint MemoryIndex)
    : FCExtensionInstruction(WasmOpCodes.MemoryInit);

public sealed record DataDropInstruction(uint DataIndex)
    : FCExtensionInstruction(WasmOpCodes.DataDrop);

public sealed record MemoryCopyInstruction(uint DestinationMemoryIndex, uint SourceMemoryIndex)
    : FCExtensionInstruction(WasmOpCodes.MemoryCopy);

public sealed record MemoryFillInstruction(uint MemoryIndex)
    : FCExtensionInstruction(WasmOpCodes.MemoryFill);

public sealed record TableInitInstruction(uint ElementIndex, uint TableIndex)
    : FCExtensionInstruction(WasmOpCodes.TableInit);

public sealed record ElemDropInstruction(uint ElementIndex)
    : FCExtensionInstruction(WasmOpCodes.ElemDrop);

public sealed record TableCopyInstruction(uint DestinationTableIndex, uint SourceTableIndex)
    : FCExtensionInstruction(WasmOpCodes.TableCopy);

public sealed record TableGrowInstruction(uint TableIndex)
    : FCExtensionInstruction(WasmOpCodes.TableGrow);

public sealed record TableSizeInstruction(uint TableIndex)
    : FCExtensionInstruction(WasmOpCodes.TableSize);

public sealed record TableFillInstruction(uint TableIndex)
    : FCExtensionInstruction(WasmOpCodes.TableFill);

// ═══════════════════════════════════════════════════════════════════════════════
// 0xFD extension (SIMD)
// ═══════════════════════════════════════════════════════════════════════════════
public abstract record SIMDExtensionInstruction(uint ExtensionCode)
    : Instruction(WasmOpCodes.SIMDExtension);

// Memory load / load-splat
public sealed record V128LoadInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load);

public sealed record V128Load8x8SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load8x8S);

public sealed record V128Load8x8UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load8x8U);

public sealed record V128Load16x4SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load16x4S);

public sealed record V128Load16x4UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load16x4U);

public sealed record V128Load32x2SInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load32x2S);

public sealed record V128Load32x2UInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load32x2U);

public sealed record V128Load8SplatInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load8Splat);

public sealed record V128Load16SplatInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load16Splat);

public sealed record V128Load32SplatInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load32Splat);

public sealed record V128Load64SplatInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load64Splat);

// Memory store
public sealed record V128StoreInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Store);

// Const and shuffle (16 bytes)
public sealed record V128ConstInstruction(ulong LowerBits, ulong UpperBits)
    : SIMDExtensionInstruction(WasmOpCodes.V128Const);

public sealed record I8x16ShuffleInstruction(byte[] Lanes)
    : SIMDExtensionInstruction(WasmOpCodes.I8x16Shuffle);

// Splat
public sealed record I8x16SplatInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Splat);

public sealed record I16x8SplatInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Splat);

public sealed record I32x4SplatInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Splat);

public sealed record I64x2SplatInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Splat);

public sealed record F32x4SplatInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Splat);

public sealed record F64x2SplatInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Splat);

// Extract / replace lane
public sealed record I8x16ExtractLaneSInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I8x16ExtractLaneS);

public sealed record I8x16ExtractLaneUInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I8x16ExtractLaneU);

public sealed record I8x16ReplaceLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I8x16ReplaceLane);

public sealed record I16x8ExtractLaneSInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtractLaneS);

public sealed record I16x8ExtractLaneUInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtractLaneU);

public sealed record I16x8ReplaceLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ReplaceLane);

public sealed record I32x4ExtractLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtractLane);

public sealed record I32x4ReplaceLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ReplaceLane);

public sealed record I64x2ExtractLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtractLane);

public sealed record I64x2ReplaceLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ReplaceLane);

public sealed record F32x4ExtractLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.F32x4ExtractLane);

public sealed record F32x4ReplaceLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.F32x4ReplaceLane);

public sealed record F64x2ExtractLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.F64x2ExtractLane);

public sealed record F64x2ReplaceLaneInstruction(byte LaneIndex)
    : SIMDExtensionInstruction(WasmOpCodes.F64x2ReplaceLane);

// Comparisons
public sealed record I8x16EqInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Eq);

public sealed record I8x16NeInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Ne);

public sealed record I8x16LtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16LtS);

public sealed record I8x16LtUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16LtU);

public sealed record I8x16GtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16GtS);

public sealed record I8x16GtUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16GtU);

public sealed record I8x16LeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16LeS);

public sealed record I8x16LeUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16LeU);

public sealed record I8x16GeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16GeS);

public sealed record I8x16GeUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16GeU);

public sealed record I16x8EqInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Eq);

public sealed record I16x8NeInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Ne);

public sealed record I16x8LtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8LtS);

public sealed record I16x8LtUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8LtU);

public sealed record I16x8GtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8GtS);

public sealed record I16x8GtUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8GtU);

public sealed record I16x8LeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8LeS);

public sealed record I16x8LeUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8LeU);

public sealed record I16x8GeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8GeS);

public sealed record I16x8GeUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8GeU);

public sealed record I32x4EqInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Eq);

public sealed record I32x4NeInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Ne);

public sealed record I32x4LtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4LtS);

public sealed record I32x4LtUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4LtU);

public sealed record I32x4GtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4GtS);

public sealed record I32x4GtUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4GtU);

public sealed record I32x4LeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4LeS);

public sealed record I32x4LeUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4LeU);

public sealed record I32x4GeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4GeS);

public sealed record I32x4GeUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4GeU);

public sealed record F32x4EqInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Eq);

public sealed record F32x4NeInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Ne);

public sealed record F32x4LtInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Lt);

public sealed record F32x4GtInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Gt);

public sealed record F32x4LeInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Le);

public sealed record F32x4GeInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Ge);

public sealed record F64x2EqInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Eq);

public sealed record F64x2NeInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Ne);

public sealed record F64x2LtInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Lt);

public sealed record F64x2GtInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Gt);

public sealed record F64x2LeInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Le);

public sealed record F64x2GeInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Ge);

public sealed record I64x2EqInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Eq);

public sealed record I64x2NeInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Ne);

public sealed record I64x2LtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2LtS);

public sealed record I64x2GtSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2GtS);

public sealed record I64x2LeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2LeS);

public sealed record I64x2GeSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2GeS);

// Logical / select
public sealed record V128NotInstruction() : SIMDExtensionInstruction(WasmOpCodes.V128Not);

public sealed record V128AndInstruction() : SIMDExtensionInstruction(WasmOpCodes.V128And);

public sealed record V128AndNotInstruction() : SIMDExtensionInstruction(WasmOpCodes.V128AndNot);

public sealed record V128OrInstruction() : SIMDExtensionInstruction(WasmOpCodes.V128Or);

public sealed record V128XorInstruction() : SIMDExtensionInstruction(WasmOpCodes.V128Xor);

public sealed record V128BitSelectInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.V128BitSelect);

public sealed record V128AnyTrueInstruction() : SIMDExtensionInstruction(WasmOpCodes.V128AnyTrue);

// Load-lane / store-lane (memarg + lane)
public sealed record V128Load8LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Load8Lane);

public sealed record V128Load16LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Load16Lane);

public sealed record V128Load32LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Load32Lane);

public sealed record V128Load64LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Load64Lane);

public sealed record V128Store8LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Store8Lane);

public sealed record V128Store16LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Store16Lane);

public sealed record V128Store32LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Store32Lane);

public sealed record V128Store64LaneInstruction(
    uint Alignment,
    uint MemoryIndex,
    uint Offset,
    byte LaneIndex
) : SIMDExtensionInstruction(WasmOpCodes.V128Store64Lane);

// load32/64 zero
public sealed record V128Load32ZeroInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load32Zero);

public sealed record V128Load64ZeroInstruction(uint Alignment, uint MemoryIndex, uint Offset)
    : SIMDExtensionInstruction(WasmOpCodes.V128Load64Zero);

// Float conversion
public sealed record F32x4DemoteF64x2ZeroInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4DemoteF64x2Zero);

public sealed record F64x2PromoteLowF32x4Instruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2PromoteLowF32x4);

// i8x16 numeric
public sealed record I8x16SwizzleInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Swizzle);

public sealed record I8x16AbsInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Abs);

public sealed record I8x16NegInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Neg);

public sealed record I8x16PopcntInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Popcnt);

public sealed record I8x16AllTrueInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16AllTrue);

public sealed record I8x16BitmaskInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Bitmask);

public sealed record I8x16NarrowI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16NarrowI16x8S);

public sealed record I8x16NarrowI16x8UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16NarrowI16x8U);

public sealed record I8x16ShlInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Shl);

public sealed record I8x16ShrSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16ShrS);

public sealed record I8x16ShrUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16ShrU);

public sealed record I8x16AddInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Add);

public sealed record I8x16AddSaturateSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16AddSaturateS);

public sealed record I8x16AddSaturateUInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16AddSaturateU);

public sealed record I8x16SubInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16Sub);

public sealed record I8x16SubSaturateSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16SubSaturateS);

public sealed record I8x16SubSaturateUInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16SubSaturateU);

public sealed record I8x16MinSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16MinS);

public sealed record I8x16MinUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16MinU);

public sealed record I8x16MaxSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16MaxS);

public sealed record I8x16MaxUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16MaxU);

public sealed record I8x16AvgrUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I8x16AvgrU);

// i16x8 numeric
public sealed record I16x8AbsInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Abs);

public sealed record I16x8NegInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Neg);

public sealed record I16x8Q15mulrSatSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8Q15mulrSatS);

public sealed record I16x8AllTrueInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8AllTrue);

public sealed record I16x8BitmaskInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Bitmask);

public sealed record I16x8NarrowI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8NarrowI32x4S);

public sealed record I16x8NarrowI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8NarrowI32x4U);

public sealed record I16x8ExtendLowI8x16SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtendLowI8x16S);

public sealed record I16x8ExtendHighI8x16SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtendHighI8x16S);

public sealed record I16x8ExtendLowI8x16UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtendLowI8x16U);

public sealed record I16x8ExtendHighI8x16UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtendHighI8x16U);

public sealed record I16x8ShlInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Shl);

public sealed record I16x8ShrSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8ShrS);

public sealed record I16x8ShrUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8ShrU);

public sealed record I16x8AddInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Add);

public sealed record I16x8AddSaturateSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8AddSaturateS);

public sealed record I16x8AddSaturateUInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8AddSaturateU);

public sealed record I16x8SubInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Sub);

public sealed record I16x8SubSaturateSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8SubSaturateS);

public sealed record I16x8SubSaturateUInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8SubSaturateU);

public sealed record I16x8MulInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8Mul);

public sealed record I16x8MinSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8MinS);

public sealed record I16x8MinUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8MinU);

public sealed record I16x8MaxSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8MaxS);

public sealed record I16x8MaxUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8MaxU);

public sealed record I16x8AvgrUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I16x8AvgrU);

public sealed record I16x8ExtMulLowI8x16SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtMulLowI8x16S);

public sealed record I16x8ExtMulHighI8x16SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtMulHighI8x16S);

public sealed record I16x8ExtMulLowI8x16UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtMulLowI8x16U);

public sealed record I16x8ExtMulHighI8x16UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtMulHighI8x16U);

// i32x4 numeric
public sealed record I32x4AbsInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Abs);

public sealed record I32x4NegInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Neg);

public sealed record I32x4AllTrueInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4AllTrue);

public sealed record I32x4BitmaskInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Bitmask);

public sealed record I32x4ExtendLowI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtendLowI16x8S);

public sealed record I32x4ExtendHighI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtendHighI16x8S);

public sealed record I32x4ExtendLowI16x8UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtendLowI16x8U);

public sealed record I32x4ExtendHighI16x8UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtendHighI16x8U);

public sealed record I32x4ShlInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Shl);

public sealed record I32x4ShrSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4ShrS);

public sealed record I32x4ShrUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4ShrU);

public sealed record I32x4AddInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Add);

public sealed record I32x4SubInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Sub);

public sealed record I32x4MulInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4Mul);

public sealed record I32x4MinSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4MinS);

public sealed record I32x4MinUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4MinU);

public sealed record I32x4MaxSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4MaxS);

public sealed record I32x4MaxUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I32x4MaxU);

public sealed record I32x4DotI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4DotI16x8S);

public sealed record I32x4ExtMulLowI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtMulLowI16x8S);

public sealed record I32x4ExtMulHighI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtMulHighI16x8S);

public sealed record I32x4ExtMulLowI16x8UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtMulLowI16x8U);

public sealed record I32x4ExtMulHighI16x8UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtMulHighI16x8U);

// i64x2 numeric
public sealed record I64x2AbsInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Abs);

public sealed record I64x2NegInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Neg);

public sealed record I64x2AllTrueInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2AllTrue);

public sealed record I64x2BitmaskInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Bitmask);

public sealed record I64x2ExtendLowI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtendLowI32x4S);

public sealed record I64x2ExtendHighI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtendHighI32x4S);

public sealed record I64x2ExtendLowI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtendLowI32x4U);

public sealed record I64x2ExtendHighI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtendHighI32x4U);

public sealed record I64x2ShlInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Shl);

public sealed record I64x2ShrSInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2ShrS);

public sealed record I64x2ShrUInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2ShrU);

public sealed record I64x2AddInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Add);

public sealed record I64x2SubInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Sub);

public sealed record I64x2MulInstruction() : SIMDExtensionInstruction(WasmOpCodes.I64x2Mul);

public sealed record I64x2ExtMulLowI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtMulLowI32x4S);

public sealed record I64x2ExtMulHighI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtMulHighI32x4S);

public sealed record I64x2ExtMulLowI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtMulLowI32x4U);

public sealed record I64x2ExtMulHighI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2ExtMulHighI32x4U);

// f32x4 numeric
public sealed record F32x4AbsInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Abs);

public sealed record F32x4NegInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Neg);

public sealed record F32x4SqrtInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Sqrt);

public sealed record F32x4AddInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Add);

public sealed record F32x4SubInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Sub);

public sealed record F32x4MulInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Mul);

public sealed record F32x4DivInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Div);

public sealed record F32x4MinInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Min);

public sealed record F32x4MaxInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Max);

public sealed record F32x4PMinInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4PMin);

public sealed record F32x4PMaxInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4PMax);

public sealed record F32x4CeilInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Ceil);

public sealed record F32x4FloorInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Floor);

public sealed record F32x4TruncInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Trunc);

public sealed record F32x4NearestInstruction() : SIMDExtensionInstruction(WasmOpCodes.F32x4Nearest);

// f64x2 numeric
public sealed record F64x2AbsInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Abs);

public sealed record F64x2NegInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Neg);

public sealed record F64x2SqrtInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Sqrt);

public sealed record F64x2AddInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Add);

public sealed record F64x2SubInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Sub);

public sealed record F64x2MulInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Mul);

public sealed record F64x2DivInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Div);

public sealed record F64x2MinInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Min);

public sealed record F64x2MaxInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Max);

public sealed record F64x2PMinInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2PMin);

public sealed record F64x2PMaxInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2PMax);

public sealed record F64x2CeilInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Ceil);

public sealed record F64x2FloorInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Floor);

public sealed record F64x2TruncInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Trunc);

public sealed record F64x2NearestInstruction() : SIMDExtensionInstruction(WasmOpCodes.F64x2Nearest);

// Integer conversions
public sealed record I32x4TruncSatF32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4TruncSatF32x4S);

public sealed record I32x4TruncSatF32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4TruncSatF32x4U);

public sealed record F32x4ConvertI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4ConvertI32x4S);

public sealed record F32x4ConvertI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4ConvertI32x4U);

public sealed record I32x4TruncSatF64x2SZeroInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4TruncSatF64x2SZero);

public sealed record I32x4TruncSatF64x2UZeroInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4TruncSatF64x2UZero);

public sealed record F64x2ConvertLowI32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2ConvertLowI32x4S);

public sealed record F64x2ConvertLowI32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2ConvertLowI32x4U);

// Extended pairwise add
public sealed record I16x8ExtaddPairwiseI8x16SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtaddPairwiseI8x16S);

public sealed record I16x8ExtaddPairwiseI8x16UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8ExtaddPairwiseI8x16U);

public sealed record I32x4ExtaddPairwiseI16x8SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtaddPairwiseI16x8S);

public sealed record I32x4ExtaddPairwiseI16x8UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4ExtaddPairwiseI16x8U);

// ─── Relaxed SIMD ─────────────────────────────────────────────────────────
public sealed record I8x16RelaxedSwizzleInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16RelaxedSwizzle);

public sealed record I32x4RelaxedTruncF32x4SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4RelaxedTruncF32x4S);

public sealed record I32x4RelaxedTruncF32x4UInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4RelaxedTruncF32x4U);

public sealed record I32x4RelaxedTruncF64x2SZeroInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4RelaxedTruncF64x2SZero);

public sealed record I32x4RelaxedTruncF64x2UZeroInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4RelaxedTruncF64x2UZero);

public sealed record F32x4RelaxedMAddInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4RelaxedMAdd);

public sealed record F32x4RelaxedNMAddInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4RelaxedNMAdd);

public sealed record F64x2RelaxedMAddInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2RelaxedMAdd);

public sealed record F64x2RelaxedNMAddInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2RelaxedNMAdd);

public sealed record I8x16RelaxedLaneSelectInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I8x16RelaxedLaneSelect);

public sealed record I16x8RelaxedLaneSelectInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8RelaxedLaneSelect);

public sealed record I32x4RelaxedLaneSelectInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4RelaxedLaneSelect);

public sealed record I64x2RelaxedLaneSelectInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I64x2RelaxedLaneSelect);

public sealed record F32x4RelaxedMinInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4RelaxedMin);

public sealed record F32x4RelaxedMaxInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F32x4RelaxedMax);

public sealed record F64x2RelaxedMinInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2RelaxedMin);

public sealed record F64x2RelaxedMaxInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.F64x2RelaxedMax);

public sealed record I16x8RelaxedQ15MulrSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8RelaxedQ15MulrS);

public sealed record I16x8RelaxedDotI8x16I7x16SInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I16x8RelaxedDotI8x16I7x16S);

public sealed record I32x4RelaxedDotI8x16I7x16AddSInstruction()
    : SIMDExtensionInstruction(WasmOpCodes.I32x4RelaxedDotI8x16I7x16AddS);
