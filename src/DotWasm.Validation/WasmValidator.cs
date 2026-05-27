using System.Collections.Immutable;
using DotWasm.Models;

namespace DotWasm.Validation;

public static class WasmValidator
{
    public static void Validate(WasmModule module)
    {
        ValidateTypes(module);
        ValidateImports(module);
        ValidateFunctions(module);
        ValidateTables(module);
        ValidateMemories(module);
        ValidateGlobals(module);
        ValidateTags(module);
        ValidateElements(module);
        ValidateData(module);
        ValidateStart(module);
        ValidateExports(module);
    }

    static void ValidateTypes(WasmModule module)
    {
        var graph = new TypeGraph(module.Types.AsSpan());
        var typeIndex = 0u;
        foreach (var recursiveType in module.Types.AsSpan())
        {
            foreach (var subType in recursiveType.SubTypes.AsSpan())
            {
                if (subType.SuperTypes.Length > 1)
                    WasmValidationException.Throw("sub type");

                foreach (var superTypeIndex in subType.SuperTypes.AsSpan())
                {
                    if (superTypeIndex >= typeIndex)
                        WasmValidationException.Throw("sub type");
                    if (!graph.TryGetSubType(superTypeIndex, out var superType))
                        WasmValidationException.Throw("sub type");
                    if (superType.IsFinal)
                        WasmValidationException.Throw("sub type");
                    if (!graph.IsCompositeSubtype(subType.CompositeType, superType.CompositeType))
                        WasmValidationException.Throw("sub type");
                }

                typeIndex++;
            }
        }
    }

    static void ValidateImports(WasmModule module)
    {
        foreach (var import in module.Imports.AsSpan())
        {
            switch (import.Kind, import.Type)
            {
                case (ImportExportKind.Function, FuncType _):
                    break;
                case (ImportExportKind.Table, TableType type):
                    ValidateTable(type);
                    break;
                case (ImportExportKind.Memory, MemoryType type):
                    ValidateMemory(type);
                    break;
                case (ImportExportKind.Global, GlobalType _):
                    break;
                case (ImportExportKind.Tag, TagType type):
                    ValidateTag(type);
                    break;
                default:
                    WasmValidationException.Throw(
                        $"Import '{import.Module}.{import.Name}' has a mismatched kind and type."
                    );
                    break;
            }
        }
    }

    static void ValidateFunctions(WasmModule module)
    {
        foreach (var function in module.Functions.AsSpan())
        {
            if (function.TypeIndex >= DefinedTypeCount(module))
            {
                WasmValidationException.Throw(
                    $"Function type index {function.TypeIndex} is out of bounds."
                );
            }

            var type = GetDefinedFunctionType(module, function.TypeIndex);
            ValidateExpression(
                module,
                function.Body,
                type.Parameters.AsSpan(),
                function.Locals.AsSpan(),
                type.Results.AsSpan()
            );
        }
    }

    static void ValidateTag(TagType tag)
    {
        if (tag.Type.Results.Length != 0)
            WasmValidationException.Throw("Tag type must have no results.");
    }

    static void ValidateTags(WasmModule module)
    {
        foreach (var tag in module.Tags.AsSpan())
            ValidateTag(tag);
    }

    static void ValidateTables(WasmModule module)
    {
        var importGlobalCount = CountImports(module, ImportExportKind.Global);
        foreach (var table in module.Tables.AsSpan())
        {
            ValidateTable(table);
            if (table.InitExpression is null && !table.ElementType.IsDefaultable)
                WasmValidationException.Throw(
                    "Table element type must be defaultable without an initializer."
                );
            if (table.InitExpression is not null)
                ValidateConstantExpression(
                    module,
                    table.InitExpression,
                    table.ElementType,
                    maxGlobalIndex: importGlobalCount
                );
        }
    }

    static void ValidateMemories(WasmModule module)
    {
        foreach (var memory in module.Memories.AsSpan())
        {
            ValidateMemory(memory);
        }
    }

    static void ValidateGlobals(WasmModule module)
    {
        var importGlobalCount = CountImports(module, ImportExportKind.Global);
        for (var i = 0; i < module.Globals.Length; i++)
        {
            var maxGlobalIndex = importGlobalCount + (uint)i;
            ValidateConstantExpression(
                module,
                module.Globals[i].InitExpression,
                module.Globals[i].Type.ValueType,
                maxGlobalIndex
            );
        }
    }

    static void ValidateElements(WasmModule module)
    {
        foreach (var element in module.Elements.AsSpan())
        {
            ValidateReferenceType(element.ElementType);

            if (element.Mode == ElementMode.Active)
            {
                EnsureIndex(element.TableIndex, TableCount(module), "table");
                var table = GetTable(module, element.TableIndex);
                EnsureElementMatchesTable(new TypeGraph(module.Types.AsSpan()), table, element);
                ValidateConstantExpression(module, element.Expression, GetAddressType(table));
            }

            foreach (var initializer in element.Initializers.AsSpan())
            {
                if (!element.ElementType.IsNullable && IsRefNullExpression(initializer))
                    WasmValidationException.Throw(
                        "Null element is not valid for a non-nullable element segment."
                    );
                ValidateConstantExpression(module, initializer, element.ElementType);
            }
        }
    }

    static void ValidateData(WasmModule module)
    {
        foreach (var data in module.Data.AsSpan())
        {
            if (data.Mode == DataSegmentMode.Active)
            {
                ValidateConstantExpression(
                    module,
                    data.OffsetExpression,
                    GetAddressType(GetMemory(module, data.MemoryIndex))
                );
            }
        }
    }

    static void ValidateStart(WasmModule module)
    {
        if (!module.StartFunctionIndex.HasValue)
            return;

        EnsureIndex(module.StartFunctionIndex.Value, FunctionCount(module), "function");
        var type = GetFunctionType(module, module.StartFunctionIndex.Value);
        if (type.Parameters.Length != 0 || type.Results.Length != 0)
            WasmValidationException.Throw("Start function must have no parameters or results.");
    }

    static void ValidateExports(WasmModule module)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in module.Exports.AsSpan())
        {
            if (!names.Add(export.Name))
                WasmValidationException.Throw($"Duplicate export name '{export.Name}'.");

            EnsureIndex(
                export.Index,
                GetExportCount(module, export.Kind),
                export.Kind.ToString().ToLowerInvariant()
            );
        }
    }

    static SubTypeResolver CreateTypeResolver(WasmModule module) =>
        typeIndex =>
        {
            var remaining = typeIndex;
            foreach (var recursiveType in module.Types.AsSpan())
            {
                if (remaining >= recursiveType.SubTypes.Length)
                {
                    remaining -= checked((uint)recursiveType.SubTypes.Length);
                    continue;
                }
                return recursiveType.SubTypes[checked((int)remaining)];
            }
            return null;
        };

    static void ValidateExpression(
        WasmModule module,
        Expression expression,
        ReadOnlySpan<WasmValueType> parameters,
        ReadOnlySpan<WasmValueType> locals,
        ReadOnlySpan<WasmValueType> results
    )
    {
        var context = ValidationContext.Rent();
        context.TypeResolver = CreateTypeResolver(module);
        context.TypeContext = module.Types;
        context.TypeGraph = new TypeGraph(module.Types.AsSpan());
        try
        {
            context.EnterFunction(parameters, locals, results);

            var instructions = expression.Instructions;
            for (int ip = 0; ip < instructions.Length; ip++)
            {
                ValidateInstruction(
                    module,
                    context,
                    instructions,
                    ref ip,
                    instructions[ip],
                    parameters,
                    locals
                );
            }

            context.EnsureComplete();
        }
        finally
        {
            ValidationContext.Return(context);
        }
    }

    static void ValidateConstantExpression(
        WasmModule module,
        Expression expression,
        WasmValueType result,
        uint? maxGlobalIndex = null
    )
    {
        var context = ValidationContext.Rent();
        context.TypeResolver = CreateTypeResolver(module);
        context.TypeContext = module.Types;
        context.TypeGraph = new TypeGraph(module.Types.AsSpan());
        try
        {
            context.EnterFunction([], [], [result]);
            var instructions = expression.Instructions;
            for (int ip = 0; ip < instructions.Length; ip++)
            {
                var instr = instructions[ip];
                if (instr.OpCode == WasmOpCodes.End)
                {
                    context.ExitLabel();
                    break;
                }

                switch (instr.OpCode)
                {
                    case WasmOpCodes.I32Const:
                        context.Push(WasmTypes.I32);
                        break;
                    case WasmOpCodes.I64Const:
                        context.Push(WasmTypes.I64);
                        break;
                    case WasmOpCodes.F32Const:
                        context.Push(WasmTypes.F32);
                        break;
                    case WasmOpCodes.F64Const:
                        context.Push(WasmTypes.F64);
                        break;
                    case WasmOpCodes.SIMDExtension:
                    {
                        var simd = (SIMDExtensionInstruction)instr;
                        if (simd.ExtensionCode != WasmOpCodes.V128Const)
                            WasmValidationException.Throw(
                                $"SIMD opcode 0x{simd.ExtensionCode:X} is not allowed in a constant expression."
                            );
                        context.Push(WasmTypes.V128);
                        break;
                    }
                    case WasmOpCodes.RefNull:
                    {
                        var refNull = (RefNullInstruction)instr;
                        context.Push(refNull.Type);
                        break;
                    }
                    case WasmOpCodes.RefFunc:
                    {
                        var refFunc = (RefFuncInstruction)instr;
                        EnsureIndex(refFunc.FunctionIndex, FunctionCount(module), "function");
                        var typeIndex = GetFunctionTypeIndex(module, refFunc.FunctionIndex);
                        if (typeIndex is { } ti)
                            context.Push(WasmTypes.ConcreteType(ti, isNullable: false));
                        else
                        {
                            var functionType = GetFunctionType(module, refFunc.FunctionIndex);
                            context.Push(
                                WasmTypes.Func(
                                    isNullable: false,
                                    functionType.Parameters,
                                    functionType.Results
                                )
                            );
                        }
                        break;
                    }
                    case WasmOpCodes.GlobalGet:
                    {
                        var gg = (GlobalGetInstruction)instr;
                        if (maxGlobalIndex.HasValue && gg.GlobalIndex >= maxGlobalIndex.Value)
                            WasmValidationException.Throw("unknown global");
                        EnsureIndex(gg.GlobalIndex, GlobalCount(module), "global");
                        var global = GetGlobal(module, gg.GlobalIndex);
                        if (global.Mutable)
                            WasmValidationException.Throw(
                                "Constant expression cannot read a mutable global."
                            );
                        context.Push(global.ValueType);
                        break;
                    }
                    case WasmOpCodes.I32Add:
                    case WasmOpCodes.I32Sub:
                    case WasmOpCodes.I32Mul:
                        context.Pop(WasmTypes.I32);
                        context.Pop(WasmTypes.I32);
                        context.Push(WasmTypes.I32);
                        break;
                    case WasmOpCodes.I64Add:
                    case WasmOpCodes.I64Sub:
                    case WasmOpCodes.I64Mul:
                        context.Pop(WasmTypes.I64);
                        context.Pop(WasmTypes.I64);
                        context.Push(WasmTypes.I64);
                        break;
                    case WasmOpCodes.GCExtension:
                    {
                        var gc = (GCExtensionInstruction)instr;
                        switch (gc.ExtensionCode)
                        {
                            case WasmOpCodes.StructNew:
                            case WasmOpCodes.StructNewDefault:
                            case WasmOpCodes.ArrayNew:
                            case WasmOpCodes.ArrayNewDefault:
                            case WasmOpCodes.ArrayNewFixed:
                            case WasmOpCodes.AnyConvertExtern:
                            case WasmOpCodes.ExternConvertAny:
                            case WasmOpCodes.RefI31:
                                ValidateGCInstruction(module, context, instr);
                                break;
                            default:
                                WasmValidationException.Throw(
                                    $"GC opcode 0x{gc.ExtensionCode:X} is not allowed in a constant expression."
                                );
                                break;
                        }
                        break;
                    }
                    default:
                        WasmValidationException.Throw(
                            $"Opcode 0x{instr.OpCode:X2} is not allowed in a constant expression."
                        );
                        break;
                }
            }

            context.EnsureComplete();
        }
        finally
        {
            ValidationContext.Return(context);
        }
    }

    static void ValidateInstruction(
        WasmModule module,
        ValidationContext context,
        ImmutableArray<Instruction> instructions,
        ref int ip,
        Instruction instr,
        ReadOnlySpan<WasmValueType> parameters,
        ReadOnlySpan<WasmValueType> locals
    )
    {
        switch (instr.OpCode)
        {
            case WasmOpCodes.Unreachable:
                context.MarkUnreachable();
                break;
            case WasmOpCodes.Nop:
                break;
            case WasmOpCodes.End:
                context.ExitLabel();
                break;
            case WasmOpCodes.Else:
                context.ElseLabel();
                break;
            case WasmOpCodes.Block:
            {
                var block = (BlockInstruction)instr;
                var btype = GetBlockFuncType(
                    module,
                    block.ParameterTypes,
                    block.ResultTypes,
                    block.ParameterCount,
                    block.ResultCount
                );
                PopResults(context, btype.Parameters.AsSpan());
                context.EnterLabel(
                    btype.Parameters.AsSpan(),
                    btype.Results.AsSpan(),
                    btype.Results.AsSpan()
                );
                break;
            }
            case WasmOpCodes.Loop:
            {
                var loop = (LoopInstruction)instr;
                var ltype = GetBlockFuncType(
                    module,
                    loop.ParameterTypes,
                    loop.ResultTypes,
                    loop.ParameterCount,
                    loop.ResultCount
                );
                PopResults(context, ltype.Parameters.AsSpan());
                context.EnterLabel(
                    ltype.Parameters.AsSpan(),
                    ltype.Results.AsSpan(),
                    ltype.Parameters.AsSpan()
                );
                break;
            }
            case WasmOpCodes.If:
            {
                var ifInstr = (IfInstruction)instr;
                var iftype = GetBlockFuncType(
                    module,
                    ifInstr.ParameterTypes,
                    ifInstr.ResultTypes,
                    ifInstr.ParameterCount,
                    ifInstr.ResultCount
                );
                context.Pop(WasmTypes.I32);
                PopResults(context, iftype.Parameters.AsSpan());
                context.EnterLabel(
                    iftype.Parameters.AsSpan(),
                    iftype.Results.AsSpan(),
                    iftype.Results.AsSpan()
                );
                context.MarkIfWithoutElse();
                break;
            }
            case WasmOpCodes.Try:
            {
                var tryInstr = (TryInstruction)instr;
                var trytype = GetBlockFuncType(
                    module,
                    tryInstr.ParameterTypes,
                    tryInstr.ResultTypes,
                    tryInstr.ParameterCount,
                    tryInstr.ResultCount
                );
                PopResults(context, trytype.Parameters.AsSpan());
                context.EnterLabel(
                    trytype.Parameters.AsSpan(),
                    trytype.Results.AsSpan(),
                    trytype.Results.AsSpan()
                );
                break;
            }
            case WasmOpCodes.TryTable:
            {
                var tryTable = (TryTableInstruction)instr;
                var ttype = GetBlockFuncType(
                    module,
                    tryTable.ParameterTypes,
                    tryTable.ResultTypes,
                    tryTable.ParameterCount,
                    tryTable.ResultCount
                );
                PopResults(context, ttype.Parameters.AsSpan());
                ValidateTryTableCatchClauses(module, context, tryTable.CatchTable);
                context.EnterLabel(
                    ttype.Parameters.AsSpan(),
                    ttype.Results.AsSpan(),
                    ttype.Results.AsSpan()
                );
                break;
            }
            case WasmOpCodes.Catch:
            {
                var catchInstr = (CatchInstruction)instr;
                EnsureIndex(catchInstr.TagIndex, TagCount(module), "tag");
                context.MarkUnreachable();
                foreach (
                    var parameter in GetTag(module, catchInstr.TagIndex).Type.Parameters.AsSpan()
                )
                    context.Push(parameter);
                break;
            }
            case WasmOpCodes.CatchAll:
                context.MarkUnreachable();
                break;
            case WasmOpCodes.Throw:
            {
                var throwInstr = (ThrowInstruction)instr;
                EnsureIndex(throwInstr.TagIndex, TagCount(module), "tag");
                PopResults(context, GetTag(module, throwInstr.TagIndex).Type.Parameters.AsSpan());
                context.MarkUnreachable();
                break;
            }
            case WasmOpCodes.Rethrow:
            {
                var rethrowInstr = (RethrowInstruction)instr;
                EnsureLabel(context, rethrowInstr.LabelIndex);
                context.MarkUnreachable();
                break;
            }
            case WasmOpCodes.ThrowRef:
                context.Pop(WasmTypes.ExnRef(true));
                context.MarkUnreachable();
                break;
            case WasmOpCodes.Br:
            {
                var br = (BrInstruction)instr;
                EnsureLabel(context, br.LabelIndex);
                PopResults(context, context.GetLabelResults(br.LabelIndex));
                context.MarkUnreachable();
                break;
            }
            case WasmOpCodes.BrIf:
            {
                var brIf = (BrIfInstruction)instr;
                EnsureLabel(context, brIf.LabelIndex);
                context.Pop(WasmTypes.I32);
                var labelResults = context.GetLabelResults(brIf.LabelIndex);
                PopResults(context, labelResults);
                foreach (var result in labelResults)
                    context.Push(result);
                break;
            }
            case WasmOpCodes.BrTable:
            {
                var brTable = (BrTableInstruction)instr;
                var defaultLabelIndex = brTable.DefaultLabelIndex;
                var labelIndices = brTable.LabelIndices;

                EnsureLabel(context, defaultLabelIndex);
                var branchResults = context.GetLabelResults(defaultLabelIndex).ToArray();
                var labelTypesMatch = true;
                foreach (var labelIndex in labelIndices.AsSpan())
                {
                    EnsureLabel(context, labelIndex);
                    labelTypesMatch &= TryMergeBranchTableResults(
                        context,
                        branchResults,
                        context.GetLabelResults(labelIndex)
                    );
                }

                context.Pop(WasmTypes.I32);
                if (!labelTypesMatch && !context.IsPolymorphic)
                    WasmValidationException.Throw("br_table label types must match.");

                var branchValues = labelTypesMatch
                    ? PopResults(context, branchResults)
                    : PopBottomResults(branchResults.Length);
                foreach (var labelIndex in labelIndices.AsSpan())
                    EnsureBranchValuesMatchLabel(
                        context,
                        branchValues,
                        context.GetLabelResults(labelIndex)
                    );
                context.MarkUnreachable();
                break;
            }
            case WasmOpCodes.Return:
                PopResults(context, context.GetFunctionResults());
                context.MarkUnreachable();
                break;
            case WasmOpCodes.Call:
            {
                var call = (CallInstruction)instr;
                EnsureIndex(call.FunctionIndex, FunctionCount(module), "function");
                ApplyFunctionType(context, GetFunctionType(module, call.FunctionIndex));
                break;
            }
            case WasmOpCodes.CallIndirect:
            {
                var callIndirect = (CallIndirectInstruction)instr;
                EnsureIndex(callIndirect.TypeIndex, DefinedTypeCount(module), "type");
                EnsureIndex(callIndirect.TableIndex, TableCount(module), "table");
                EnsureFunctionTable(module, context.TypeGraph!, callIndirect.TableIndex);
                context.Pop(GetAddressType(GetTable(module, callIndirect.TableIndex)));
                ApplyFunctionType(context, GetDefinedFunctionType(module, callIndirect.TypeIndex));
                break;
            }
            case WasmOpCodes.ReturnCall:
            {
                var returnCall = (ReturnCallInstruction)instr;
                EnsureIndex(returnCall.FunctionIndex, FunctionCount(module), "function");
                ApplyTailCallFunctionType(
                    context,
                    GetFunctionType(module, returnCall.FunctionIndex)
                );
                break;
            }
            case WasmOpCodes.ReturnCallIndirect:
            {
                var rci = (ReturnCallIndirectInstruction)instr;
                EnsureIndex(rci.TypeIndex, DefinedTypeCount(module), "type");
                EnsureIndex(rci.TableIndex, TableCount(module), "table");
                EnsureFunctionTable(module, context.TypeGraph!, rci.TableIndex);
                context.Pop(GetAddressType(GetTable(module, rci.TableIndex)));
                ApplyTailCallFunctionType(context, GetDefinedFunctionType(module, rci.TypeIndex));
                break;
            }
            case WasmOpCodes.CallRef:
            {
                var callRef = (CallRefInstruction)instr;
                EnsureIndex(callRef.TypeIndex, DefinedTypeCount(module), "type");
                var callRefType = GetDefinedFunctionType(module, callRef.TypeIndex);
                context.Pop(WasmTypes.ConcreteType(callRef.TypeIndex, isNullable: true));
                ApplyFunctionType(context, callRefType);
                break;
            }
            case WasmOpCodes.ReturnCallRef:
            {
                var rcr = (ReturnCallRefInstruction)instr;
                EnsureIndex(rcr.TypeIndex, DefinedTypeCount(module), "type");
                var returnCallRefType = GetDefinedFunctionType(module, rcr.TypeIndex);
                context.Pop(WasmTypes.ConcreteType(rcr.TypeIndex, isNullable: true));
                ApplyTailCallFunctionType(context, returnCallRefType);
                break;
            }
            case WasmOpCodes.Drop:
                context.Pop();
                break;
            case WasmOpCodes.Select:
            {
                context.Pop(WasmTypes.I32);
                var right = context.Pop();
                var left = context.Pop();
                if (left.IsSubtypeOf(right, context.TypeGraph))
                    PushUntypedSelectResult(context, right);
                else if (right.IsSubtypeOf(left, context.TypeGraph))
                    PushUntypedSelectResult(context, left);
                else
                    WasmValidationException.Throw("select operands must have compatible types.");
                break;
            }
            case WasmOpCodes.SelectT:
            {
                var selectT = (SelectTInstruction)instr;
                if (selectT.Types.Length != 1)
                    WasmValidationException.Throw("typed select must specify one result type.");
                context.Pop(WasmTypes.I32);
                context.Pop(selectT.Types[0]);
                context.Pop(selectT.Types[0]);
                context.Push(selectT.Types[0]);
                break;
            }
            case WasmOpCodes.LocalGet:
            {
                var lg = (LocalGetInstruction)instr;
                context.EnsureLocalInitialized(lg.LocalIndex);
                context.Push(GetLocal(parameters, locals, lg.LocalIndex));
                break;
            }
            case WasmOpCodes.LocalSet:
            {
                var ls = (LocalSetInstruction)instr;
                context.Pop(GetLocal(parameters, locals, ls.LocalIndex));
                context.InitializeLocal(ls.LocalIndex);
                break;
            }
            case WasmOpCodes.LocalTee:
            {
                var lt = (LocalTeeInstruction)instr;
                var localType = GetLocal(parameters, locals, lt.LocalIndex);
                context.Pop(localType);
                context.InitializeLocal(lt.LocalIndex);
                context.Push(localType);
                break;
            }
            case WasmOpCodes.GlobalGet:
            {
                var gg = (GlobalGetInstruction)instr;
                context.Push(GetGlobal(module, gg.GlobalIndex).ValueType);
                break;
            }
            case WasmOpCodes.GlobalSet:
            {
                var gs = (GlobalSetInstruction)instr;
                var global = GetGlobal(module, gs.GlobalIndex);
                if (!global.Mutable)
                    WasmValidationException.Throw("Cannot set an immutable global.");
                context.Pop(global.ValueType);
                break;
            }
            case WasmOpCodes.TableGet:
            {
                var tg = (TableGetInstruction)instr;
                var table = GetTable(module, tg.TableIndex);
                context.Pop(GetAddressType(table));
                context.Push(table.ElementType);
                break;
            }
            case WasmOpCodes.TableSet:
            {
                var ts = (TableSetInstruction)instr;
                var table = GetTable(module, ts.TableIndex);
                context.Pop(table.ElementType);
                context.Pop(GetAddressType(table));
                break;
            }
            case WasmOpCodes.I32Load:
            {
                var m = (I32LoadInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I32, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Load8S:
            {
                var m = (I32Load8SInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I32, m.Alignment, 0, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Load8U:
            {
                var m = (I32Load8UInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I32, m.Alignment, 0, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Load16S:
            {
                var m = (I32Load16SInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I32, m.Alignment, 1, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Load16U:
            {
                var m = (I32Load16UInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I32, m.Alignment, 1, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load:
            {
                var m = (I64LoadInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 3, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load8S:
            {
                var m = (I64Load8SInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 0, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load8U:
            {
                var m = (I64Load8UInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 0, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load16S:
            {
                var m = (I64Load16SInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 1, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load16U:
            {
                var m = (I64Load16UInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 1, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load32S:
            {
                var m = (I64Load32SInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Load32U:
            {
                var m = (I64Load32UInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.I64, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.F32Load:
            {
                var m = (F32LoadInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.F32, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.F64Load:
            {
                var m = (F64LoadInstruction)instr;
                ValidateMemoryAccess(module, context, WasmTypes.F64, m.Alignment, 3, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Store:
            {
                var m = (I32StoreInstruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I32, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Store8:
            {
                var m = (I32Store8Instruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I32, m.Alignment, 0, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I32Store16:
            {
                var m = (I32Store16Instruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I32, m.Alignment, 1, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Store:
            {
                var m = (I64StoreInstruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I64, m.Alignment, 3, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Store8:
            {
                var m = (I64Store8Instruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I64, m.Alignment, 0, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Store16:
            {
                var m = (I64Store16Instruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I64, m.Alignment, 1, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.I64Store32:
            {
                var m = (I64Store32Instruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.I64, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.F32Store:
            {
                var m = (F32StoreInstruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.F32, m.Alignment, 2, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.F64Store:
            {
                var m = (F64StoreInstruction)instr;
                ValidateMemoryStore(module, context, WasmTypes.F64, m.Alignment, 3, m.MemoryIndex);
                break;
            }
            case WasmOpCodes.MemorySize:
            {
                var ms = (MemorySizeInstruction)instr;
                context.Push(GetAddressType(GetMemory(module, ms.MemoryIndex)));
                break;
            }
            case WasmOpCodes.MemoryGrow:
            {
                var mg = (MemoryGrowInstruction)instr;
                var addressType = GetAddressType(GetMemory(module, mg.MemoryIndex));
                context.Pop(addressType);
                context.Push(addressType);
                break;
            }
            case WasmOpCodes.RefNull:
            {
                var refNull = (RefNullInstruction)instr;
                context.Push(refNull.Type);
                break;
            }
            case WasmOpCodes.RefIsNull:
                context.Pop();
                context.Push(WasmTypes.I32);
                break;
            case WasmOpCodes.RefEq:
                context.Pop(WasmTypes.EqRef(isNullable: true));
                context.Pop(WasmTypes.EqRef(isNullable: true));
                context.Push(WasmTypes.I32);
                break;
            case WasmOpCodes.RefAsNonNull:
            {
                var nonNullReferenceType = context.Pop();
                context.Push(nonNullReferenceType.AsNonNullable());
                break;
            }
            case WasmOpCodes.RefFunc:
            {
                var refFunc = (RefFuncInstruction)instr;
                EnsureIndex(refFunc.FunctionIndex, FunctionCount(module), "function");
                EnsureDeclaredFunctionReference(module, refFunc.FunctionIndex);
                var typeIndex = GetFunctionTypeIndex(module, refFunc.FunctionIndex);
                if (typeIndex is { } ti)
                    context.Push(WasmTypes.ConcreteType(ti, isNullable: false));
                else
                {
                    var functionType = GetFunctionType(module, refFunc.FunctionIndex);
                    context.Push(
                        WasmTypes.Func(
                            isNullable: false,
                            functionType.Parameters,
                            functionType.Results
                        )
                    );
                }
                break;
            }
            case WasmOpCodes.BrOnNull:
            {
                var bon = (BrOnNullInstruction)instr;
                EnsureLabel(context, bon.LabelIndex);
                var referenceType = context.Pop();
                var nullLabelResults = context.GetLabelResults(bon.LabelIndex);
                PopResults(context, nullLabelResults);
                foreach (var result in nullLabelResults)
                    context.Push(result);
                context.Push(referenceType.AsNonNullable());
                break;
            }
            case WasmOpCodes.BrOnNonNull:
            {
                var bonn = (BrOnNonNullInstruction)instr;
                EnsureLabel(context, bonn.LabelIndex);
                var referenceType = context.Pop();
                context.Push(referenceType.AsNonNullable());
                var nonNullLabelResults = context.GetLabelResults(bonn.LabelIndex);
                if (nonNullLabelResults.Length == 0 || !nonNullLabelResults[^1].IsRefType)
                    WasmValidationException.Throw("Type mismatch.");
                PopResults(context, nonNullLabelResults);
                foreach (var result in nonNullLabelResults[..^1])
                    context.Push(result);
                break;
            }
            case WasmOpCodes.I32Const:
                context.Push(WasmTypes.I32);
                break;
            case WasmOpCodes.I64Const:
                context.Push(WasmTypes.I64);
                break;
            case WasmOpCodes.F32Const:
                context.Push(WasmTypes.F32);
                break;
            case WasmOpCodes.F64Const:
                context.Push(WasmTypes.F64);
                break;
            case WasmOpCodes.I32Eqz:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.I32);
                break;
            case >= WasmOpCodes.I32Eq and <= WasmOpCodes.I32GeU:
                ApplyBinary(context, WasmTypes.I32, WasmTypes.I32);
                break;
            case WasmOpCodes.I64Eqz:
                ApplyUnary(context, WasmTypes.I64, WasmTypes.I32);
                break;
            case >= WasmOpCodes.I64Eq and <= WasmOpCodes.I64GeU:
                ApplyBinary(context, WasmTypes.I64, WasmTypes.I32);
                break;
            case >= WasmOpCodes.F32Eq and <= WasmOpCodes.F32Ge:
                ApplyBinary(context, WasmTypes.F32, WasmTypes.I32);
                break;
            case >= WasmOpCodes.F64Eq and <= WasmOpCodes.F64Ge:
                ApplyBinary(context, WasmTypes.F64, WasmTypes.I32);
                break;
            case WasmOpCodes.I32Clz:
            case WasmOpCodes.I32Ctz:
            case WasmOpCodes.I32Popcnt:
            case WasmOpCodes.I32Extend8S:
            case WasmOpCodes.I32Extend16S:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.I32);
                break;
            case >= WasmOpCodes.I32Add and <= WasmOpCodes.I32Rotr:
                ApplyBinary(context, WasmTypes.I32, WasmTypes.I32);
                break;
            case WasmOpCodes.I64Clz:
            case WasmOpCodes.I64Ctz:
            case WasmOpCodes.I64Popcnt:
            case WasmOpCodes.I64Extend8S:
            case WasmOpCodes.I64Extend16S:
            case WasmOpCodes.I64Extend32S:
                ApplyUnary(context, WasmTypes.I64, WasmTypes.I64);
                break;
            case >= WasmOpCodes.I64Add and <= WasmOpCodes.I64Rotr:
                ApplyBinary(context, WasmTypes.I64, WasmTypes.I64);
                break;
            case >= WasmOpCodes.F32Abs and <= WasmOpCodes.F32Sqrt:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.F32);
                break;
            case >= WasmOpCodes.F32Add and <= WasmOpCodes.F32Copysign:
                ApplyBinary(context, WasmTypes.F32, WasmTypes.F32);
                break;
            case >= WasmOpCodes.F64Abs and <= WasmOpCodes.F64Sqrt:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.F64);
                break;
            case >= WasmOpCodes.F64Add and <= WasmOpCodes.F64Copysign:
                ApplyBinary(context, WasmTypes.F64, WasmTypes.F64);
                break;
            case WasmOpCodes.I32WrapI64:
                ApplyUnary(context, WasmTypes.I64, WasmTypes.I32);
                break;
            case WasmOpCodes.I64ExtendI32S:
            case WasmOpCodes.I64ExtendI32U:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.I64);
                break;
            case WasmOpCodes.F32DemoteF64:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.F32);
                break;
            case WasmOpCodes.F64PromoteF32:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.F64);
                break;
            case WasmOpCodes.I32ReinterpretF32:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.I32);
                break;
            case WasmOpCodes.I64ReinterpretF64:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.I64);
                break;
            case WasmOpCodes.F32ReinterpretI32:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.F32);
                break;
            case WasmOpCodes.F64ReinterpretI64:
                ApplyUnary(context, WasmTypes.I64, WasmTypes.F64);
                break;
            case WasmOpCodes.I32TruncF32S:
            case WasmOpCodes.I32TruncF32U:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.I32);
                break;
            case WasmOpCodes.I32TruncF64S:
            case WasmOpCodes.I32TruncF64U:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.I32);
                break;
            case WasmOpCodes.I64TruncF32S:
            case WasmOpCodes.I64TruncF32U:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.I64);
                break;
            case WasmOpCodes.I64TruncF64S:
            case WasmOpCodes.I64TruncF64U:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.I64);
                break;
            case WasmOpCodes.F32ConvertI32S:
            case WasmOpCodes.F32ConvertI32U:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.F32);
                break;
            case WasmOpCodes.F32ConvertI64S:
            case WasmOpCodes.F32ConvertI64U:
                ApplyUnary(context, WasmTypes.I64, WasmTypes.F32);
                break;
            case WasmOpCodes.F64ConvertI32S:
            case WasmOpCodes.F64ConvertI32U:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.F64);
                break;
            case WasmOpCodes.F64ConvertI64S:
            case WasmOpCodes.F64ConvertI64U:
                ApplyUnary(context, WasmTypes.I64, WasmTypes.F64);
                break;
            case WasmOpCodes.FCExtension:
                ValidatePrefixedInstruction(module, context, instr);
                break;
            case WasmOpCodes.GCExtension:
                ValidateGCInstruction(module, context, instr);
                break;
            case WasmOpCodes.SIMDExtension:
                ValidateSIMDInstruction(module, context, instr);
                break;
            default:
                WasmValidationException.Throw(
                    $"Opcode 0x{instr.OpCode:X2} is not implemented in validation."
                );
                break;
        }
    }

    static void ValidateGCInstruction(
        WasmModule module,
        ValidationContext context,
        Instruction instr
    )
    {
        var gc = (GCExtensionInstruction)instr;
        switch (gc.ExtensionCode)
        {
            case WasmOpCodes.StructNew:
            {
                var sn = (StructNewInstruction)instr;
                var type = GetDefinedStructType(module, sn.TypeIndex);
                for (var i = type.Fields.Length - 1; i >= 0; i--)
                    context.Pop(UnpackStorageType(type.Fields[i].StorageType));
                context.Push(WasmTypes.ConcreteType(sn.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.StructNewDefault:
            {
                var sn = (StructNewDefaultInstruction)instr;
                var type = GetDefinedStructType(module, sn.TypeIndex);
                EnsureDefaultable(type.Fields.AsSpan());
                context.Push(WasmTypes.ConcreteType(sn.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.StructGet:
            case WasmOpCodes.StructGetS:
            case WasmOpCodes.StructGetU:
            {
                var sg = (StructGetInstruction)instr;
                var field = GetStructField(module, sg.TypeIndex, sg.FieldIndex);
                EnsurePackedAccess(field.StorageType, sg.Signedness);
                context.Pop(WasmTypes.ConcreteType(sg.TypeIndex, isNullable: true));
                context.Push(UnpackStorageType(field.StorageType));
                break;
            }
            case WasmOpCodes.StructSet:
            {
                var ss = (StructSetInstruction)instr;
                var field = GetStructField(module, ss.TypeIndex, ss.FieldIndex);
                if (!field.Mutable)
                    WasmValidationException.Throw("Cannot set an immutable struct field.");
                context.Pop(UnpackStorageType(field.StorageType));
                context.Pop(WasmTypes.ConcreteType(ss.TypeIndex, isNullable: true));
                break;
            }
            case WasmOpCodes.ArrayNew:
            {
                var an = (ArrayNewInstruction)instr;
                var type = GetDefinedArrayType(module, an.TypeIndex);
                context.Pop(WasmTypes.I32);
                context.Pop(UnpackStorageType(type.Field.StorageType));
                context.Push(WasmTypes.ConcreteType(an.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.ArrayNewDefault:
            {
                var an = (ArrayNewDefaultInstruction)instr;
                var type = GetDefinedArrayType(module, an.TypeIndex);
                EnsureDefaultable(type.Field.StorageType);
                context.Pop(WasmTypes.I32);
                context.Push(WasmTypes.ConcreteType(an.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.ArrayNewFixed:
            {
                var an = (ArrayNewFixedInstruction)instr;
                var type = GetDefinedArrayType(module, an.TypeIndex);
                var elementType = UnpackStorageType(type.Field.StorageType);
                for (var i = 0u; i < an.Length; i++)
                    context.Pop(elementType);
                context.Push(WasmTypes.ConcreteType(an.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.ArrayNewData:
            {
                var an = (ArrayNewDataInstruction)instr;
                EnsureIndex(an.DataIndex, (uint)module.Data.Length, "data");
                GetDefinedArrayType(module, an.TypeIndex);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Push(WasmTypes.ConcreteType(an.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.ArrayNewElem:
            {
                var an = (ArrayNewElemInstruction)instr;
                EnsureIndex(an.ElementIndex, (uint)module.Elements.Length, "element");
                GetDefinedArrayType(module, an.TypeIndex);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Push(WasmTypes.ConcreteType(an.TypeIndex, isNullable: false));
                break;
            }
            case WasmOpCodes.ArrayGet:
            case WasmOpCodes.ArrayGetS:
            case WasmOpCodes.ArrayGetU:
            {
                var ag = (ArrayGetInstruction)instr;
                var type = GetDefinedArrayType(module, ag.TypeIndex);
                EnsurePackedAccess(type.Field.StorageType, ag.Signedness);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(ag.TypeIndex, isNullable: true));
                context.Push(UnpackStorageType(type.Field.StorageType));
                break;
            }
            case WasmOpCodes.ArraySet:
            {
                var aset = (ArraySetInstruction)instr;
                var type = GetDefinedArrayType(module, aset.TypeIndex);
                if (!type.Field.Mutable)
                    WasmValidationException.Throw("Cannot set an immutable array field.");
                context.Pop(UnpackStorageType(type.Field.StorageType));
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(aset.TypeIndex, isNullable: true));
                break;
            }
            case WasmOpCodes.ArrayLen:
                context.Pop(WasmTypes.ArrayRef(isNullable: true));
                context.Push(WasmTypes.I32);
                break;
            case WasmOpCodes.ArrayFill:
            {
                var af = (ArrayFillInstruction)instr;
                var type = GetDefinedArrayType(module, af.TypeIndex);
                if (!type.Field.Mutable)
                    WasmValidationException.Throw("Cannot fill an immutable array field.");
                context.Pop(WasmTypes.I32);
                context.Pop(UnpackStorageType(type.Field.StorageType));
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(af.TypeIndex, isNullable: true));
                break;
            }
            case WasmOpCodes.ArrayCopy:
            {
                var ac = (ArrayCopyInstruction)instr;
                var dst = GetDefinedArrayType(module, ac.DestinationTypeIndex);
                var src = GetDefinedArrayType(module, ac.SourceTypeIndex);
                if (!dst.Field.Mutable)
                    WasmValidationException.Throw("Cannot copy into an immutable array field.");
                if (
                    !AreArrayCopyTypesCompatible(
                        context.TypeGraph!,
                        src.Field.StorageType,
                        dst.Field.StorageType
                    )
                )
                    WasmValidationException.Throw("Array element types are incompatible.");
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(ac.SourceTypeIndex, isNullable: true));
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(ac.DestinationTypeIndex, isNullable: true));
                break;
            }
            case WasmOpCodes.ArrayInitData:
            {
                var ai = (ArrayInitDataInstruction)instr;
                EnsureIndex(ai.DataIndex, (uint)module.Data.Length, "data");
                var type = GetDefinedArrayType(module, ai.TypeIndex);
                if (!type.Field.Mutable)
                    WasmValidationException.Throw("Cannot initialize an immutable array field.");
                if (!IsNumericOrVector(type.Field.StorageType))
                    WasmValidationException.Throw("array type is not numeric or vector");
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(ai.TypeIndex, isNullable: true));
                break;
            }
            case WasmOpCodes.ArrayInitElem:
            {
                var ai = (ArrayInitElemInstruction)instr;
                EnsureIndex(ai.ElementIndex, (uint)module.Elements.Length, "element");
                var type = GetDefinedArrayType(module, ai.TypeIndex);
                if (!type.Field.Mutable)
                    WasmValidationException.Throw("Cannot initialize an immutable array field.");
                var elemType = module.Elements[(int)ai.ElementIndex].ElementType;
                var arrayElemType = UnpackStorageType(type.Field.StorageType);
                if (!elemType.IsSubtypeOf(arrayElemType, context.TypeGraph))
                    WasmValidationException.Throw("type mismatch");
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.ConcreteType(ai.TypeIndex, isNullable: true));
                break;
            }
            case WasmOpCodes.RefTest:
            case WasmOpCodes.RefTestNull:
            {
                var rt = (RefTestInstruction)instr;
                context.Pop(WasmTypes.AnyRef(isNullable: true));
                context.Push(WasmTypes.I32);
                break;
            }
            case WasmOpCodes.RefCast:
            case WasmOpCodes.RefCastNull:
            {
                var rc = (RefCastInstruction)instr;
                context.Pop(WasmTypes.AnyRef(isNullable: true));
                context.Push(rc.ReferenceType);
                break;
            }
            case WasmOpCodes.BrOnCast:
            case WasmOpCodes.BrOnCastFail:
                ValidateBrOnCast(context, instr);
                break;
            case WasmOpCodes.AnyConvertExtern:
                ApplyUnary(context, WasmTypes.ExternRef(true), WasmTypes.AnyRef(true));
                break;
            case WasmOpCodes.ExternConvertAny:
                ApplyUnary(context, WasmTypes.AnyRef(true), WasmTypes.ExternRef(true));
                break;
            case WasmOpCodes.RefI31:
                ApplyUnary(context, WasmTypes.I32, WasmTypes.I31Ref(false));
                break;
            case WasmOpCodes.I31GetS:
            case WasmOpCodes.I31GetU:
                ApplyUnary(context, WasmTypes.I31Ref(true), WasmTypes.I32);
                break;
            default:
                WasmValidationException.Throw(
                    $"GC opcode 0x{gc.ExtensionCode:X} is not implemented in validation."
                );
                break;
        }
    }

    static void ValidatePrefixedInstruction(
        WasmModule module,
        ValidationContext context,
        Instruction instr
    )
    {
        var fc = (FCExtensionInstruction)instr;
        switch (fc.ExtensionCode)
        {
            case WasmOpCodes.I32TruncSatF32S:
            case WasmOpCodes.I32TruncSatF32U:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.I32);
                break;
            case WasmOpCodes.I32TruncSatF64S:
            case WasmOpCodes.I32TruncSatF64U:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.I32);
                break;
            case WasmOpCodes.I64TruncSatF32S:
            case WasmOpCodes.I64TruncSatF32U:
                ApplyUnary(context, WasmTypes.F32, WasmTypes.I64);
                break;
            case WasmOpCodes.I64TruncSatF64S:
            case WasmOpCodes.I64TruncSatF64U:
                ApplyUnary(context, WasmTypes.F64, WasmTypes.I64);
                break;
            case WasmOpCodes.MemoryInit:
            {
                var memInit = (MemoryInitInstruction)instr;
                EnsureIndex(memInit.DataIndex, (uint)module.Data.Length, "data");
                var addressType = GetAddressType(GetMemory(module, memInit.MemoryIndex));
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(addressType);
                break;
            }
            case WasmOpCodes.DataDrop:
            {
                var dd = (DataDropInstruction)instr;
                EnsureIndex(dd.DataIndex, (uint)module.Data.Length, "data");
                break;
            }
            case WasmOpCodes.MemoryCopy:
            {
                var mc = (MemoryCopyInstruction)instr;
                var dstAddressType = GetAddressType(GetMemory(module, mc.DestinationMemoryIndex));
                var srcAddressType = GetAddressType(GetMemory(module, mc.SourceMemoryIndex));
                var sizeAddressType =
                    dstAddressType == srcAddressType ? dstAddressType : WasmTypes.I32;
                context.Pop(sizeAddressType);
                context.Pop(srcAddressType);
                context.Pop(dstAddressType);
                break;
            }
            case WasmOpCodes.MemoryFill:
            {
                var mf = (MemoryFillInstruction)instr;
                var addressType = GetAddressType(GetMemory(module, mf.MemoryIndex));
                context.Pop(addressType);
                context.Pop(WasmTypes.I32);
                context.Pop(addressType);
                break;
            }
            case WasmOpCodes.TableInit:
            {
                var ti = (TableInitInstruction)instr;
                EnsureIndex(ti.TableIndex, TableCount(module), "table");
                EnsureIndex(ti.ElementIndex, (uint)module.Elements.Length, "element");
                EnsureElementMatchesTable(
                    context.TypeGraph!,
                    GetTable(module, ti.TableIndex),
                    module.Elements[checked((int)ti.ElementIndex)]
                );
                var tableAddressType = GetAddressType(GetTable(module, ti.TableIndex));
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.I32);
                context.Pop(tableAddressType);
                break;
            }
            case WasmOpCodes.ElemDrop:
            {
                var ed = (ElemDropInstruction)instr;
                EnsureIndex(ed.ElementIndex, (uint)module.Elements.Length, "element");
                break;
            }
            case WasmOpCodes.TableCopy:
            {
                var tc = (TableCopyInstruction)instr;
                EnsureIndex(tc.DestinationTableIndex, TableCount(module), "table");
                EnsureIndex(tc.SourceTableIndex, TableCount(module), "table");
                EnsureTableTypesMatch(
                    context.TypeGraph!,
                    GetTable(module, tc.DestinationTableIndex),
                    GetTable(module, tc.SourceTableIndex)
                );
                var dstAddressType = GetAddressType(GetTable(module, tc.DestinationTableIndex));
                var srcAddressType = GetAddressType(GetTable(module, tc.SourceTableIndex));
                var sizeAddressType =
                    dstAddressType == srcAddressType ? dstAddressType : WasmTypes.I32;
                context.Pop(sizeAddressType);
                context.Pop(srcAddressType);
                context.Pop(dstAddressType);
                break;
            }
            case WasmOpCodes.TableGrow:
            {
                var tg = (TableGrowInstruction)instr;
                var table = GetTable(module, tg.TableIndex);
                var addressType = GetAddressType(table);
                context.Pop(addressType);
                context.Pop(table.ElementType);
                context.Push(addressType);
                break;
            }
            case WasmOpCodes.TableSize:
            {
                var ts = (TableSizeInstruction)instr;
                context.Push(GetAddressType(GetTable(module, ts.TableIndex)));
                break;
            }
            case WasmOpCodes.TableFill:
            {
                var tf = (TableFillInstruction)instr;
                var table = GetTable(module, tf.TableIndex);
                var addressType = GetAddressType(table);
                context.Pop(addressType);
                context.Pop(table.ElementType);
                context.Pop(addressType);
                break;
            }
            default:
                WasmValidationException.Throw(
                    $"Opcode 0xFC 0x{fc.ExtensionCode:X} is not implemented in validation."
                );
                break;
        }
    }

    static void ValidateSIMDInstruction(
        WasmModule module,
        ValidationContext context,
        Instruction instr
    )
    {
        var simd = (SIMDExtensionInstruction)instr;
        switch (simd.ExtensionCode)
        {
            case <= WasmOpCodes.V128Load32x2U:
            case >= WasmOpCodes.V128Load32Zero and <= WasmOpCodes.V128Load64Zero:
            {
                var (align, memIdx) = GetSIMDMemArg(instr);
                ValidateMemoryAccess(
                    module,
                    context,
                    WasmTypes.V128,
                    align,
                    SimdMemoryAlignment(simd.ExtensionCode),
                    memIdx
                );
                break;
            }
            case >= WasmOpCodes.V128Load8Splat and <= WasmOpCodes.V128Load64Splat:
            {
                var (align, memIdx) = GetSIMDMemArg(instr);
                ValidateMemoryAccess(
                    module,
                    context,
                    WasmTypes.V128,
                    align,
                    SimdMemoryAlignment(simd.ExtensionCode),
                    memIdx
                );
                break;
            }
            case WasmOpCodes.V128Store:
            {
                var (align, memIdx) = GetSIMDMemArg(instr);
                ValidateMemoryStore(module, context, WasmTypes.V128, align, 4, memIdx);
                break;
            }
            case WasmOpCodes.V128Const:
                context.Push(WasmTypes.V128);
                break;
            case WasmOpCodes.I8x16Shuffle:
            {
                var shuffle = (I8x16ShuffleInstruction)instr;
                foreach (var lane in shuffle.Lanes)
                {
                    if (lane >= 32)
                        WasmValidationException.Throw("Lane index out of bounds.");
                }
                context.Pop(WasmTypes.V128);
                context.Pop(WasmTypes.V128);
                context.Push(WasmTypes.V128);
                break;
            }
            case WasmOpCodes.I8x16Swizzle:
            case WasmOpCodes.I8x16RelaxedSwizzle:
                ApplyBinary(context, WasmTypes.V128, WasmTypes.V128);
                break;
            case >= WasmOpCodes.I32x4RelaxedTruncF32x4S
            and <= WasmOpCodes.I32x4RelaxedTruncF64x2UZero:
                ApplyUnary(context, WasmTypes.V128, WasmTypes.V128);
                break;
            case >= WasmOpCodes.F32x4RelaxedMAdd and <= WasmOpCodes.F64x2RelaxedNMAdd:
            case >= WasmOpCodes.I8x16RelaxedLaneSelect and <= WasmOpCodes.I64x2RelaxedLaneSelect:
            case WasmOpCodes.I32x4RelaxedDotI8x16I7x16AddS:
                context.Pop(WasmTypes.V128);
                context.Pop(WasmTypes.V128);
                context.Pop(WasmTypes.V128);
                context.Push(WasmTypes.V128);
                break;
            case >= WasmOpCodes.F32x4RelaxedMin and <= WasmOpCodes.F64x2RelaxedMax:
            case WasmOpCodes.I16x8RelaxedQ15MulrS:
            case WasmOpCodes.I16x8RelaxedDotI8x16I7x16S:
                ApplyBinary(context, WasmTypes.V128, WasmTypes.V128);
                break;
            case >= WasmOpCodes.I8x16Splat and <= WasmOpCodes.F64x2Splat:
                context.Pop(SimdSplatOperand(simd.ExtensionCode));
                context.Push(WasmTypes.V128);
                break;
            case >= WasmOpCodes.I8x16ExtractLaneS and <= WasmOpCodes.F64x2ReplaceLane:
                ValidateSIMDLaneInstruction(context, simd.ExtensionCode, GetSIMDLaneIndex(instr));
                break;
            case >= WasmOpCodes.I8x16Eq and <= WasmOpCodes.F64x2Ge:
            case >= WasmOpCodes.I64x2Eq and <= WasmOpCodes.I64x2GeS:
                ApplyBinary(context, WasmTypes.V128, WasmTypes.V128);
                break;
            case WasmOpCodes.V128Not:
                ApplyUnary(context, WasmTypes.V128, WasmTypes.V128);
                break;
            case >= WasmOpCodes.V128And and <= WasmOpCodes.V128Xor:
                ApplyBinary(context, WasmTypes.V128, WasmTypes.V128);
                break;
            case WasmOpCodes.V128BitSelect:
                context.Pop(WasmTypes.V128);
                context.Pop(WasmTypes.V128);
                context.Pop(WasmTypes.V128);
                context.Push(WasmTypes.V128);
                break;
            case WasmOpCodes.V128AnyTrue:
                ApplyUnary(context, WasmTypes.V128, WasmTypes.I32);
                break;
            case >= WasmOpCodes.V128Load8Lane and <= WasmOpCodes.V128Load64Lane:
                ValidateSIMDLoadLane(module, context, instr);
                break;
            case >= WasmOpCodes.V128Store8Lane and <= WasmOpCodes.V128Store64Lane:
                ValidateSIMDStoreLane(module, context, instr);
                break;
            case WasmOpCodes.F32x4DemoteF64x2Zero:
            case WasmOpCodes.F64x2PromoteLowF32x4:
            case >= WasmOpCodes.I8x16Abs and <= WasmOpCodes.F64x2ConvertLowI32x4U:
                ValidateSIMDNumericInstruction(context, simd.ExtensionCode);
                break;
            default:
                WasmValidationException.Throw(
                    $"SIMD opcode 0x{simd.ExtensionCode:X} is not implemented in validation."
                );
                break;
        }
    }

    static uint SimdMemoryAlignment(uint opcode) =>
        opcode switch
        {
            WasmOpCodes.V128Load or WasmOpCodes.V128Store => 4,
            WasmOpCodes.V128Load8x8S
            or WasmOpCodes.V128Load8x8U
            or WasmOpCodes.V128Load16x4S
            or WasmOpCodes.V128Load16x4U
            or WasmOpCodes.V128Load32x2S
            or WasmOpCodes.V128Load32x2U => 3,
            WasmOpCodes.V128Load8Splat or WasmOpCodes.V128Load8Lane or WasmOpCodes.V128Store8Lane =>
                0,
            WasmOpCodes.V128Load16Splat
            or WasmOpCodes.V128Load16Lane
            or WasmOpCodes.V128Store16Lane => 1,
            WasmOpCodes.V128Load32Splat
            or WasmOpCodes.V128Load32Lane
            or WasmOpCodes.V128Store32Lane
            or WasmOpCodes.V128Load32Zero => 2,
            _ => 3,
        };

    static WasmValueType SimdSplatOperand(uint opcode) =>
        opcode switch
        {
            WasmOpCodes.I8x16Splat or WasmOpCodes.I16x8Splat or WasmOpCodes.I32x4Splat =>
                WasmTypes.I32,
            WasmOpCodes.I64x2Splat => WasmTypes.I64,
            WasmOpCodes.F32x4Splat => WasmTypes.F32,
            _ => WasmTypes.F64,
        };

    static (uint Alignment, uint MemoryIndex) GetSIMDMemArg(Instruction instr)
    {
        return instr switch
        {
            V128LoadInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load8x8SInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load8x8UInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load16x4SInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load16x4UInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load32x2SInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load32x2UInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load32ZeroInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load64ZeroInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load8SplatInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load16SplatInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load32SplatInstruction m => (m.Alignment, m.MemoryIndex),
            V128Load64SplatInstruction m => (m.Alignment, m.MemoryIndex),
            V128StoreInstruction m => (m.Alignment, m.MemoryIndex),
            _ => default,
        };
    }

    static byte GetSIMDLaneIndex(Instruction instr)
    {
        return instr switch
        {
            I8x16ExtractLaneSInstruction m => m.LaneIndex,
            I8x16ExtractLaneUInstruction m => m.LaneIndex,
            I8x16ReplaceLaneInstruction m => m.LaneIndex,
            I16x8ExtractLaneSInstruction m => m.LaneIndex,
            I16x8ExtractLaneUInstruction m => m.LaneIndex,
            I16x8ReplaceLaneInstruction m => m.LaneIndex,
            I32x4ExtractLaneInstruction m => m.LaneIndex,
            I32x4ReplaceLaneInstruction m => m.LaneIndex,
            I64x2ExtractLaneInstruction m => m.LaneIndex,
            I64x2ReplaceLaneInstruction m => m.LaneIndex,
            F32x4ExtractLaneInstruction m => m.LaneIndex,
            F32x4ReplaceLaneInstruction m => m.LaneIndex,
            F64x2ExtractLaneInstruction m => m.LaneIndex,
            F64x2ReplaceLaneInstruction m => m.LaneIndex,
            _ => 0,
        };
    }

    static (uint Alignment, uint MemoryIndex, byte LaneIndex) GetSIMDLaneMemArg(Instruction instr)
    {
        return instr switch
        {
            V128Load8LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Load16LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Load32LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Load64LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Store8LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Store16LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Store32LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            V128Store64LaneInstruction m => (m.Alignment, m.MemoryIndex, m.LaneIndex),
            _ => default,
        };
    }

    static FuncType GetBlockFuncType(
        WasmModule module,
        ImmutableArray<WasmValueType> paramTypes,
        ImmutableArray<WasmValueType> resultTypes,
        int paramCount,
        int resultCount
    )
    {
        if (!paramTypes.IsDefault)
            return new FuncType { Parameters = paramTypes, Results = resultTypes };

        if (paramCount == 0 && resultCount == 0)
            return new FuncType { Parameters = [], Results = [] };

        for (var i = 0u; i < DefinedTypeCount(module); i++)
        {
            var ft = GetDefinedFunctionType(module, i);
            if (ft.Parameters.Length == paramCount && ft.Results.Length == resultCount)
                return ft;
        }

        return new FuncType
        {
            Parameters =
                paramCount == 0 ? [] : ImmutableArray.Create(new WasmValueType[paramCount]),
            Results = resultCount == 0 ? [] : ImmutableArray.Create(new WasmValueType[resultCount]),
        };
    }

    static void ValidateSIMDLaneInstruction(ValidationContext context, uint opcode, byte lane)
    {
        var laneCount = opcode switch
        {
            >= WasmOpCodes.I8x16ExtractLaneS and <= WasmOpCodes.I8x16ReplaceLane => 16,
            >= WasmOpCodes.I16x8ExtractLaneS and <= WasmOpCodes.I16x8ReplaceLane => 8,
            >= WasmOpCodes.I32x4ExtractLane
            and <= WasmOpCodes.I32x4ReplaceLane
            or >= WasmOpCodes.F32x4ExtractLane
            and <= WasmOpCodes.F32x4ReplaceLane => 4,
            _ => 2,
        };
        if (lane >= laneCount)
            WasmValidationException.Throw("Lane index out of bounds.");

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
            context.Pop(
                opcode switch
                {
                    WasmOpCodes.I8x16ReplaceLane
                    or WasmOpCodes.I16x8ReplaceLane
                    or WasmOpCodes.I32x4ReplaceLane => WasmTypes.I32,
                    WasmOpCodes.I64x2ReplaceLane => WasmTypes.I64,
                    WasmOpCodes.F32x4ReplaceLane => WasmTypes.F32,
                    _ => WasmTypes.F64,
                }
            );
            context.Pop(WasmTypes.V128);
            context.Push(WasmTypes.V128);
        }
        else
        {
            context.Pop(WasmTypes.V128);
            context.Push(
                opcode switch
                {
                    <= WasmOpCodes.I32x4ReplaceLane => WasmTypes.I32,
                    WasmOpCodes.I64x2ExtractLane => WasmTypes.I64,
                    WasmOpCodes.F32x4ExtractLane => WasmTypes.F32,
                    _ => WasmTypes.F64,
                }
            );
        }
    }

    static void ValidateSIMDLoadLane(
        WasmModule module,
        ValidationContext context,
        Instruction instr
    )
    {
        var (alignment, memoryIndex, lane) = GetSIMDLaneMemArg(instr);
        var ext = ((SIMDExtensionInstruction)instr).ExtensionCode;
        ValidateMemoryAlignment(alignment, SimdMemoryAlignment(ext));
        EnsureIndex(memoryIndex, MemoryCount(module), "memory");
        var laneCount = ext switch
        {
            WasmOpCodes.V128Load8Lane => 16,
            WasmOpCodes.V128Load16Lane => 8,
            WasmOpCodes.V128Load32Lane => 4,
            _ => 2,
        };
        if (lane >= laneCount)
            WasmValidationException.Throw("Lane index out of bounds.");
        context.Pop(WasmTypes.V128);
        context.Pop(WasmTypes.I32);
        context.Push(WasmTypes.V128);
    }

    static void ValidateSIMDStoreLane(
        WasmModule module,
        ValidationContext context,
        Instruction instr
    )
    {
        var (alignment, memoryIndex, lane) = GetSIMDLaneMemArg(instr);
        var ext = ((SIMDExtensionInstruction)instr).ExtensionCode;
        ValidateMemoryAlignment(alignment, SimdMemoryAlignment(ext));
        EnsureIndex(memoryIndex, MemoryCount(module), "memory");
        var laneCount = ext switch
        {
            WasmOpCodes.V128Store8Lane => 16,
            WasmOpCodes.V128Store16Lane => 8,
            WasmOpCodes.V128Store32Lane => 4,
            _ => 2,
        };
        if (lane >= laneCount)
            WasmValidationException.Throw("Lane index out of bounds.");
        context.Pop(WasmTypes.V128);
        context.Pop(WasmTypes.I32);
    }

    static void ValidateSIMDNumericInstruction(ValidationContext context, uint opcode)
    {
        switch (opcode)
        {
            case WasmOpCodes.I8x16AllTrue:
            case WasmOpCodes.I8x16Bitmask:
            case WasmOpCodes.I16x8AllTrue:
            case WasmOpCodes.I16x8Bitmask:
            case WasmOpCodes.I32x4AllTrue:
            case WasmOpCodes.I32x4Bitmask:
            case WasmOpCodes.I64x2AllTrue:
            case WasmOpCodes.I64x2Bitmask:
                ApplyUnary(context, WasmTypes.V128, WasmTypes.I32);
                break;
            case WasmOpCodes.I8x16Shl:
            case WasmOpCodes.I8x16ShrS:
            case WasmOpCodes.I8x16ShrU:
            case WasmOpCodes.I16x8Shl:
            case WasmOpCodes.I16x8ShrS:
            case WasmOpCodes.I16x8ShrU:
            case WasmOpCodes.I32x4Shl:
            case WasmOpCodes.I32x4ShrS:
            case WasmOpCodes.I32x4ShrU:
            case WasmOpCodes.I64x2Shl:
            case WasmOpCodes.I64x2ShrS:
            case WasmOpCodes.I64x2ShrU:
                context.Pop(WasmTypes.I32);
                context.Pop(WasmTypes.V128);
                context.Push(WasmTypes.V128);
                break;
            default:
                if (
                    opcode
                    is WasmOpCodes.I8x16NarrowI16x8S
                        or WasmOpCodes.I8x16NarrowI16x8U
                        or WasmOpCodes.I16x8NarrowI32x4S
                        or WasmOpCodes.I16x8NarrowI32x4U
                        or >= WasmOpCodes.I16x8ExtMulLowI8x16S
                        and <= WasmOpCodes.I16x8ExtMulHighI8x16U
                        or WasmOpCodes.I32x4DotI16x8S
                        or >= WasmOpCodes.I32x4ExtMulLowI16x8S
                        and <= WasmOpCodes.I32x4ExtMulHighI16x8U
                        or >= WasmOpCodes.I64x2ExtMulLowI32x4S
                        and <= WasmOpCodes.I64x2ExtMulHighI32x4U
                )
                    ApplyBinary(context, WasmTypes.V128, WasmTypes.V128);
                else
                    ApplyUnaryOrBinarySIMD(context, opcode);
                break;
        }
    }

    static void ApplyUnaryOrBinarySIMD(ValidationContext context, uint opcode)
    {
        if (
            opcode
            is WasmOpCodes.I8x16Abs
                or WasmOpCodes.I8x16Neg
                or WasmOpCodes.I8x16Popcnt
                or WasmOpCodes.F32x4Ceil
                or WasmOpCodes.F32x4Floor
                or WasmOpCodes.F32x4Trunc
                or WasmOpCodes.F32x4Nearest
                or WasmOpCodes.F64x2Ceil
                or WasmOpCodes.F64x2Floor
                or WasmOpCodes.F64x2Trunc
                or WasmOpCodes.I16x8ExtaddPairwiseI8x16S
                or WasmOpCodes.I16x8ExtaddPairwiseI8x16U
                or WasmOpCodes.I32x4ExtaddPairwiseI16x8S
                or WasmOpCodes.I32x4ExtaddPairwiseI16x8U
                or WasmOpCodes.I16x8Abs
                or WasmOpCodes.I16x8Neg
                or WasmOpCodes.I16x8ExtendLowI8x16S
                or WasmOpCodes.I16x8ExtendHighI8x16S
                or WasmOpCodes.I16x8ExtendLowI8x16U
                or WasmOpCodes.I16x8ExtendHighI8x16U
                or WasmOpCodes.F64x2Nearest
                or WasmOpCodes.I32x4Abs
                or WasmOpCodes.I32x4Neg
                or WasmOpCodes.I32x4ExtendLowI16x8S
                or WasmOpCodes.I32x4ExtendHighI16x8S
                or WasmOpCodes.I32x4ExtendLowI16x8U
                or WasmOpCodes.I32x4ExtendHighI16x8U
                or WasmOpCodes.I64x2Abs
                or WasmOpCodes.I64x2Neg
                or WasmOpCodes.I64x2ExtendLowI32x4S
                or WasmOpCodes.I64x2ExtendHighI32x4S
                or WasmOpCodes.I64x2ExtendLowI32x4U
                or WasmOpCodes.I64x2ExtendHighI32x4U
                or WasmOpCodes.F32x4Abs
                or WasmOpCodes.F32x4Neg
                or WasmOpCodes.F32x4Sqrt
                or WasmOpCodes.F64x2Abs
                or WasmOpCodes.F64x2Neg
                or WasmOpCodes.F64x2Sqrt
                or WasmOpCodes.F32x4DemoteF64x2Zero
                or WasmOpCodes.F64x2PromoteLowF32x4
                or >= WasmOpCodes.I32x4TruncSatF32x4S
                and <= WasmOpCodes.F64x2ConvertLowI32x4U
        )
            ApplyUnary(context, WasmTypes.V128, WasmTypes.V128);
        else
            ApplyBinary(context, WasmTypes.V128, WasmTypes.V128);
    }

    static void ApplyFunctionType(ValidationContext context, FuncType type)
    {
        for (var i = type.Parameters.Length - 1; i >= 0; i--)
            context.Pop(type.Parameters[i]);

        foreach (var result in type.Results.AsSpan())
            context.Push(result);
    }

    static void PushUntypedSelectResult(ValidationContext context, WasmValueType result)
    {
        if (result.IsRefType && !result.IsBottom)
            WasmValidationException.Throw("select operands must have numeric types.");

        context.Push(result);
    }

    static void ApplyTailCallFunctionType(ValidationContext context, FuncType type)
    {
        for (var i = type.Parameters.Length - 1; i >= 0; i--)
            context.Pop(type.Parameters[i]);

        EnsureSameResults(context, type.Results.AsSpan(), context.GetFunctionResults());
        context.MarkUnreachable();
    }

    static WasmValueType[] PopResults(
        ValidationContext context,
        ReadOnlySpan<WasmValueType> results
    )
    {
        var actual = new WasmValueType[results.Length];
        for (var i = results.Length - 1; i >= 0; i--)
            actual[i] = context.Pop(results[i]);
        return actual;
    }

    static WasmValueType[] PopBottomResults(int count)
    {
        var results = new WasmValueType[count];
        Array.Fill(results, WasmTypes.Bottom);
        return results;
    }

    static void EnsureFunctionTable(WasmModule module, TypeGraph graph, uint tableIndex)
    {
        if (!GetTable(module, tableIndex).ElementType.IsSubtypeOf(WasmTypes.FuncRef(true), graph))
            WasmValidationException.Throw("Indirect calls require a funcref table.");
    }

    static void EnsureSameResults(
        ValidationContext context,
        ReadOnlySpan<WasmValueType> actual,
        ReadOnlySpan<WasmValueType> expected
    )
    {
        if (!IsSubtypeSequence(context, actual, expected))
            WasmValidationException.Throw("br_table label types must match.");
    }

    static bool TryMergeBranchTableResults(
        ValidationContext context,
        Span<WasmValueType> branchResults,
        ReadOnlySpan<WasmValueType> labelResults
    )
    {
        if (branchResults.Length != labelResults.Length)
            return false;

        for (var i = 0; i < branchResults.Length; i++)
        {
            var branchResult = branchResults[i];
            var labelResult = labelResults[i];
            if (branchResult.IsSubtypeOf(labelResult, context.TypeGraph))
                continue;

            if (labelResult.IsSubtypeOf(branchResult, context.TypeGraph))
            {
                branchResults[i] = labelResult;
                continue;
            }

            return false;
        }

        return true;
    }

    static void EnsureBranchValuesMatchLabel(
        ValidationContext context,
        ReadOnlySpan<WasmValueType> branchValues,
        ReadOnlySpan<WasmValueType> labelResults
    )
    {
        if (!IsSubtypeSequence(context, branchValues, labelResults))
            WasmValidationException.Throw(
                $"br_table label types must match. Branch [{string.Join(", ", branchValues.ToArray())}], label [{string.Join(", ", labelResults.ToArray())}]."
            );
    }

    static void ValidateTryTableCatchClauses(
        WasmModule module,
        ValidationContext context,
        ReadOnlySpan<CatchClause> clauses
    )
    {
        foreach (var clause in clauses)
        {
            EnsureLabel(context, clause.LabelIndex);
            var labelResults = context.GetLabelResults(clause.LabelIndex);

            switch (clause.Kind)
            {
                case 0:
                {
                    EnsureCatchPayloadMatchesLabel(
                        context,
                        GetTag(module, clause.TagIndex).Type.Parameters.AsSpan(),
                        labelResults
                    );
                    break;
                }
                case 1:
                {
                    var tagParameters = GetTag(module, clause.TagIndex).Type.Parameters.AsSpan();
                    var payload = new WasmValueType[tagParameters.Length + 1];
                    tagParameters.CopyTo(payload);
                    payload[^1] = WasmTypes.ExnRef(isNullable: false);
                    EnsureCatchPayloadMatchesLabel(context, payload, labelResults);
                    break;
                }
                case 2:
                    EnsureCatchPayloadMatchesLabel(context, [], labelResults);
                    break;
                case 3:
                    EnsureCatchPayloadMatchesLabel(
                        context,
                        [WasmTypes.ExnRef(isNullable: false)],
                        labelResults
                    );
                    break;
                default:
                    WasmValidationException.Throw("Invalid catch clause kind.");
                    break;
            }
        }
    }

    static void EnsureCatchPayloadMatchesLabel(
        ValidationContext context,
        ReadOnlySpan<WasmValueType> payload,
        ReadOnlySpan<WasmValueType> labelResults
    )
    {
        if (!IsSubtypeSequence(context, payload, labelResults))
            WasmValidationException.Throw(
                $"try_table catch type mismatch. Payload [{string.Join(", ", payload.ToArray())}], label [{string.Join(", ", labelResults.ToArray())}]."
            );
    }

    static bool IsSubtypeSequence(
        ValidationContext context,
        ReadOnlySpan<WasmValueType> actual,
        ReadOnlySpan<WasmValueType> expected
    )
    {
        if (actual.Length != expected.Length)
            return false;

        for (var i = 0; i < actual.Length; i++)
        {
            if (!actual[i].IsSubtypeOf(expected[i], context.TypeGraph))
                return false;
        }

        return true;
    }

    static void ApplyUnary(ValidationContext context, WasmValueType operand, WasmValueType result)
    {
        context.Pop(operand);
        context.Push(result);
    }

    static void ApplyBinary(ValidationContext context, WasmValueType operand, WasmValueType result)
    {
        context.Pop(operand);
        context.Pop(operand);
        context.Push(result);
    }

    static void ValidateMemoryAccess(
        WasmModule module,
        ValidationContext context,
        WasmValueType result,
        uint alignment,
        uint maxAlignment,
        uint memoryIndex
    )
    {
        var memory = GetMemory(module, memoryIndex);
        ValidateMemoryAlignment(alignment, maxAlignment);
        context.Pop(GetAddressType(memory));
        context.Push(result);
    }

    static void ValidateMemoryStore(
        WasmModule module,
        ValidationContext context,
        WasmValueType value,
        uint alignment,
        uint maxAlignment,
        uint memoryIndex
    )
    {
        var memory = GetMemory(module, memoryIndex);
        ValidateMemoryAlignment(alignment, maxAlignment);
        context.Pop(value);
        context.Pop(GetAddressType(memory));
    }

    static void ValidateMemoryAlignment(uint alignment, uint maxAlignment)
    {
        if (alignment > maxAlignment)
            WasmValidationException.Throw("Memory alignment must not be larger than natural.");
    }

    static WasmValueType GetLocal(
        ReadOnlySpan<WasmValueType> parameters,
        ReadOnlySpan<WasmValueType> locals,
        uint index
    )
    {
        var parameterCount = (uint)parameters.Length;
        if (index < parameterCount)
            return parameters[checked((int)index)];

        var localIndex = index - parameterCount;
        if (localIndex < locals.Length)
            return locals[checked((int)localIndex)];

        WasmValidationException.Throw($"Local index {index} is out of bounds.");
        return default;
    }

    static uint? GetFunctionTypeIndex(WasmModule module, uint functionIndex)
    {
        var importedFunctions = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind != ImportExportKind.Function)
                continue;

            if (importedFunctions == functionIndex)
                return null;

            importedFunctions++;
        }

        var localIndex = functionIndex - importedFunctions;
        EnsureIndex(localIndex, (uint)module.Functions.Length, "function");
        return module.Functions[checked((int)localIndex)].TypeIndex;
    }

    static FuncType GetFunctionType(WasmModule module, uint functionIndex)
    {
        var importedFunctions = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind != ImportExportKind.Function)
                continue;

            if (importedFunctions == functionIndex)
                return import.Type switch
                {
                    FuncType type => type,
                    _ => ThrowMismatchedImportType<FuncType>("function"),
                };

            importedFunctions++;
        }

        var localIndex = functionIndex - importedFunctions;
        EnsureIndex(localIndex, (uint)module.Functions.Length, "function");
        return GetDefinedFunctionType(module, module.Functions[checked((int)localIndex)].TypeIndex);
    }

    static uint DefinedTypeCount(WasmModule module)
    {
        var count = 0u;
        foreach (var recursiveType in module.Types.AsSpan())
            count += checked((uint)recursiveType.SubTypes.Length);
        return count;
    }

    static FuncType GetDefinedFunctionType(WasmModule module, uint typeIndex)
    {
        return GetDefinedSubType(module, typeIndex).CompositeType switch
        {
            FuncType type => type,
            _ => ThrowMismatchedImportType<FuncType>("function"),
        };
    }

    static StructType GetDefinedStructType(WasmModule module, uint typeIndex)
    {
        return GetDefinedSubType(module, typeIndex).CompositeType switch
        {
            StructType type => type,
            _ => ThrowMismatchedImportType<StructType>("struct"),
        };
    }

    static ArrayType GetDefinedArrayType(WasmModule module, uint typeIndex)
    {
        return GetDefinedSubType(module, typeIndex).CompositeType switch
        {
            ArrayType type => type,
            _ => ThrowMismatchedImportType<ArrayType>("array"),
        };
    }

    static FieldType GetStructField(WasmModule module, uint typeIndex, uint fieldIndex)
    {
        var type = GetDefinedStructType(module, typeIndex);
        EnsureIndex(fieldIndex, (uint)type.Fields.Length, "field");
        return type.Fields[checked((int)fieldIndex)];
    }

    static SubType GetDefinedSubType(WasmModule module, uint typeIndex)
    {
        var remaining = typeIndex;
        foreach (var recursiveType in module.Types.AsSpan())
        {
            if (remaining >= recursiveType.SubTypes.Length)
            {
                remaining -= checked((uint)recursiveType.SubTypes.Length);
                continue;
            }

            return recursiveType.SubTypes[checked((int)remaining)];
        }

        WasmValidationException.Throw($"Type index {typeIndex} is out of bounds.");
        return default;
    }

    static WasmValueType UnpackStorageType(StorageType storageType)
    {
        return storageType switch
        {
            WasmValueType valueType => valueType,
            PackedType => WasmTypes.I32,
            _ => throw new InvalidOperationException("Unknown storage type."),
        };
    }

    static bool IsNumericOrVector(StorageType storageType) =>
        storageType.Value switch
        {
            PackedType => true,
            WasmValueType v => v.Value is I32Type or I64Type or F32Type or F64Type or V128Type,
            _ => false,
        };

    static bool AreArrayCopyTypesCompatible(
        TypeGraph graph,
        StorageType srcType,
        StorageType dstType
    ) =>
        (srcType.Value, dstType.Value) switch
        {
            (PackedType p1, PackedType p2) => p1 == p2,
            (WasmValueType v1, WasmValueType v2) => TypeRelations.IsEquivalent(v1, v2, graph),
            _ => false,
        };

    static void EnsureDefaultable(ReadOnlySpan<FieldType> fields)
    {
        foreach (var field in fields)
            EnsureDefaultable(field.StorageType);
    }

    static void EnsureDefaultable(StorageType storageType)
    {
        if (!UnpackStorageType(storageType).IsDefaultable)
            WasmValidationException.Throw("Field type is not defaultable.");
    }

    static void EnsurePackedAccess(StorageType storageType, Signedness signedness)
    {
        if (signedness == Signedness.None)
            return;
        if (storageType is not PackedType)
            WasmValidationException.Throw("Signed packed access requires a packed field.");
    }

    static void ValidateBrOnCast(ValidationContext context, Instruction instr)
    {
        var (labelIndex, sourceType, targetType, isFail) = instr switch
        {
            BrOnCastInstruction cast => (
                cast.LabelIndex,
                cast.SourceReferenceType,
                cast.TargetReferenceType,
                false
            ),
            BrOnCastFailInstruction castFail => (
                castFail.LabelIndex,
                castFail.SourceReferenceType,
                castFail.TargetReferenceType,
                true
            ),
            _ => throw new InvalidOperationException("Expected br_on_cast instruction."),
        };

        EnsureLabel(context, labelIndex);
        context.Pop(sourceType);
        var labelResults = context.GetLabelResults(labelIndex);

        if (!targetType.IsSubtypeOf(sourceType, context.TypeGraph))
            WasmValidationException.Throw(
                "br_on_cast type mismatch: target is not a subtype of source."
            );

        if (isFail)
        {
            var diffType = RefTypeDifference(sourceType, targetType);
            if (
                labelResults.Length == 0
                || !diffType.IsSubtypeOf(labelResults[^1], context.TypeGraph)
            )
                WasmValidationException.Throw("br_on_cast label type mismatch.");

            PopResults(context, labelResults[..^1]);
            foreach (var result in labelResults[..^1])
                context.Push(result);
            context.Push(diffType);
            PopResults(context, labelResults);
            foreach (var result in labelResults[..^1])
                context.Push(result);
            context.Push(targetType);
            return;
        }

        if (
            labelResults.Length == 0
            || !targetType.IsSubtypeOf(labelResults[^1], context.TypeGraph)
        )
            WasmValidationException.Throw("br_on_cast label type mismatch.");

        PopResults(context, labelResults[..^1]);
        context.Push(targetType);
        PopResults(context, labelResults);
        foreach (var result in labelResults[..^1])
            context.Push(result);
        context.Push(RefTypeDifference(sourceType, targetType));
    }

    static WasmValueType RefTypeDifference(WasmValueType from, WasmValueType to)
    {
        return from switch
        {
            RefType fromRef when to is RefType toRef && toRef.IsNullable => fromRef with
            {
                IsNullable = false,
            },
            _ => from,
        };
    }

    static TableType GetTable(WasmModule module, uint tableIndex)
    {
        var importedTables = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind != ImportExportKind.Table)
                continue;

            if (importedTables == tableIndex)
                return import.Type switch
                {
                    TableType type => type,
                    _ => ThrowMismatchedImportType<TableType>("table"),
                };

            importedTables++;
        }

        var localIndex = tableIndex - importedTables;
        EnsureIndex(localIndex, (uint)module.Tables.Length, "table");
        return module.Tables[checked((int)localIndex)];
    }

    static GlobalType GetGlobal(WasmModule module, uint globalIndex)
    {
        var importedGlobals = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind != ImportExportKind.Global)
                continue;

            if (importedGlobals == globalIndex)
                return import.Type switch
                {
                    GlobalType type => type,
                    _ => ThrowMismatchedImportType<GlobalType>("global"),
                };

            importedGlobals++;
        }

        var localIndex = globalIndex - importedGlobals;
        EnsureIndex(localIndex, (uint)module.Globals.Length, "global");
        return module.Globals[checked((int)localIndex)].Type;
    }

    static MemoryType GetMemory(WasmModule module, uint memoryIndex)
    {
        var importedMemories = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind != ImportExportKind.Memory)
                continue;

            if (importedMemories == memoryIndex)
                return import.Type switch
                {
                    MemoryType type => type,
                    _ => ThrowMismatchedImportType<MemoryType>("memory"),
                };

            importedMemories++;
        }

        var localIndex = memoryIndex - importedMemories;
        EnsureIndex(localIndex, (uint)module.Memories.Length, "memory");
        return module.Memories[checked((int)localIndex)];
    }

    static WasmValueType GetAddressType(MemoryType memory) =>
        memory.AddressType == AddressType.I64 ? WasmTypes.I64 : WasmTypes.I32;

    static WasmValueType GetAddressType(TableType table) =>
        table.AddressType == AddressType.I64 ? WasmTypes.I64 : WasmTypes.I32;

    static TagType GetTag(WasmModule module, uint tagIndex)
    {
        var importedTags = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind != ImportExportKind.Tag)
                continue;

            if (importedTags == tagIndex)
                return import.Type switch
                {
                    TagType type => type,
                    _ => ThrowMismatchedImportType<TagType>("tag"),
                };

            importedTags++;
        }

        var localIndex = tagIndex - importedTags;
        EnsureIndex(localIndex, (uint)module.Tags.Length, "tag");
        return module.Tags[checked((int)localIndex)];
    }

    static T ThrowMismatchedImportType<T>(string kind)
    {
        WasmValidationException.Throw($"Imported {kind} has a mismatched external type.");
        return default!;
    }

    static uint FunctionCount(WasmModule module) =>
        CountImports(module, ImportExportKind.Function) + (uint)module.Functions.Length;

    static uint TableCount(WasmModule module) =>
        CountImports(module, ImportExportKind.Table) + (uint)module.Tables.Length;

    static uint MemoryCount(WasmModule module) =>
        CountImports(module, ImportExportKind.Memory) + (uint)module.Memories.Length;

    static uint GlobalCount(WasmModule module) =>
        CountImports(module, ImportExportKind.Global) + (uint)module.Globals.Length;

    static uint TagCount(WasmModule module) =>
        CountImports(module, ImportExportKind.Tag) + (uint)module.Tags.Length;

    static uint CountImports(WasmModule module, ImportExportKind kind)
    {
        var count = 0u;
        foreach (var import in module.Imports.AsSpan())
        {
            if (import.Kind == kind)
                count++;
        }

        return count;
    }

    static uint GetExportCount(WasmModule module, ImportExportKind kind) =>
        kind switch
        {
            ImportExportKind.Function => FunctionCount(module),
            ImportExportKind.Table => TableCount(module),
            ImportExportKind.Memory => MemoryCount(module),
            ImportExportKind.Global => GlobalCount(module),
            ImportExportKind.Tag => TagCount(module),
            _ => 0,
        };

    static void EnsureLabel(ValidationContext context, uint labelIndex)
    {
        if (labelIndex > context.LabelDepth)
            WasmValidationException.Throw($"Label index {labelIndex} is out of bounds.");
    }

    static void EnsureDeclaredFunctionReference(WasmModule module, uint functionIndex)
    {
        foreach (var export in module.Exports.AsSpan())
        {
            if (export.Kind == ImportExportKind.Function && export.Index == functionIndex)
                return;
        }

        foreach (var global in module.Globals.AsSpan())
        {
            if (ContainsRefFunc(module, global.InitExpression, functionIndex))
                return;
        }

        foreach (var element in module.Elements.AsSpan())
        {
            foreach (var initializer in element.Initializers.AsSpan())
            {
                if (ContainsRefFunc(module, initializer, functionIndex))
                    return;
            }
        }

        WasmValidationException.Throw("Undeclared function reference.");
    }

    static bool ContainsRefFunc(WasmModule module, Expression expression, uint functionIndex)
    {
        foreach (var instr in expression.Instructions)
        {
            if (instr is RefFuncInstruction refFunc && refFunc.FunctionIndex == functionIndex)
                return true;
        }
        return false;
    }

    static bool IsRefNullExpression(Expression expression)
    {
        var instructions = expression.Instructions;
        return instructions.Length == 2
            && instructions[0] is RefNullInstruction
            && instructions[^1] is EndInstruction;
    }

    static void EnsureElementMatchesTable(TypeGraph graph, TableType table, Element element)
    {
        if (!element.ElementType.IsSubtypeOf(table.ElementType, graph))
            WasmValidationException.Throw("Element segment type must match table type.");
    }

    static void EnsureTableTypesMatch(TypeGraph graph, TableType destination, TableType source)
    {
        if (!source.ElementType.IsSubtypeOf(destination.ElementType, graph))
            WasmValidationException.Throw("Table element types must match.");
    }

    static void EnsureIndex(uint index, uint count, string kind)
    {
        if (index >= count)
            WasmValidationException.Throw($"{kind} index {index} is out of bounds.");
    }

    static void ValidateTable(TableType table)
    {
        ValidateReferenceType(table.ElementType);
        ValidateLimit(
            table.Minimum,
            table.Maximum,
            table.AddressType == AddressType.I64 ? ulong.MaxValue : uint.MaxValue
        );
    }

    static void ValidateMemory(MemoryType memory)
    {
        ValidateLimit(
            memory.Minimum,
            memory.Maximum,
            memory.AddressType == AddressType.I64 ? 1UL << 48 : 1UL << 16
        );
    }

    static void ValidateReferenceType(WasmValueType type)
    {
        if (!type.IsRefType)
            WasmValidationException.Throw($"Expected reference type, found {type}.");
    }

    static void ValidateLimit(ulong min, ulong? max, ulong k)
    {
        if (min > k)
            WasmValidationException.Throw($"Minimum {min} cannot exceed the limit {k}.");

        if (max.HasValue)
        {
            if (min > max.Value)
            {
                WasmValidationException.Throw(
                    $"Minimum {min} cannot be greater than maximum {max.Value}."
                );
            }

            if (max.Value > k)
            {
                WasmValidationException.Throw($"Maximum {max.Value} cannot exceed the limit {k}.");
            }
        }
    }
}
