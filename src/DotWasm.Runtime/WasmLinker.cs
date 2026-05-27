using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DotWasm.Models;
using DotWasm.Validation;

namespace DotWasm.Runtime;

public sealed class WasmLinker(WasmStore store)
{
    public readonly record struct LinkerKey(string ModuleName, string ItemName);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct LinkerItem
    {
        public required ImportExportKind Kind { get; init; }
        public required long Address { get; init; }
        public required ExternalType Type { get; init; }
        public WasmModule? TypeModule { get; init; }
    }

    readonly Dictionary<LinkerKey, LinkerItem> items = [];

    internal WasmStore Store => store;

    public void RegisterFunction(string moduleName, string functionName, HostFunction function)
    {
        var address = store.AddFunctionInstance(function);

        var key = new LinkerKey(moduleName, functionName);
        if (
            !items.TryAdd(
                key,
                new LinkerItem
                {
                    Kind = ImportExportKind.Function,
                    Address = address,
                    Type = new ExternalType(function.Type),
                }
            )
        )
        {
            throw new InvalidOperationException(
                $"A host function with the name '{functionName}' in module '{moduleName}' is already registered."
            );
        }
    }

    public void RegisterGlobal(string moduleName, string globalName, GlobalInstance global)
    {
        var address = store.AddGlobalInstance(global);

        var key = new LinkerKey(moduleName, globalName);
        if (
            !items.TryAdd(
                key,
                new LinkerItem
                {
                    Kind = ImportExportKind.Global,
                    Address = address,
                    Type = new ExternalType(
                        new GlobalType { Mutable = global.Mutable, ValueType = global.ValueType }
                    ),
                }
            )
        )
        {
            throw new InvalidOperationException(
                $"A global with the name '{globalName}' in module '{moduleName}' is already registered."
            );
        }
    }

    public void RegisterMemory(string moduleName, string memoryName, MemoryInstance memory)
    {
        var address = store.AddMemoryInstance(memory);

        var key = new LinkerKey(moduleName, memoryName);
        if (
            !items.TryAdd(
                key,
                new LinkerItem
                {
                    Kind = ImportExportKind.Memory,
                    Address = address,
                    Type = new ExternalType(
                        new MemoryType
                        {
                            AddressType = memory.AddressType,
                            Minimum = checked((ulong)(memory.Data.Length / WasmStore.PageSize)),
                            Maximum = memory.Max,
                        }
                    ),
                }
            )
        )
        {
            throw new InvalidOperationException(
                $"A memory with the name '{memoryName}' in module '{moduleName}' is already registered."
            );
        }
    }

    public void RegisterTable(string moduleName, string tableName, TableInstance table)
    {
        var address = store.AddTableInstance(table);

        var key = new LinkerKey(moduleName, tableName);
        if (
            !items.TryAdd(
                key,
                new LinkerItem
                {
                    Kind = ImportExportKind.Table,
                    Address = address,
                    Type = new ExternalType(
                        new TableType
                        {
                            AddressType = table.AddressType,
                            Minimum = checked((uint)table.References.Length),
                            Maximum = table.Max,
                            ElementType = table.ElementType,
                        }
                    ),
                }
            )
        )
        {
            throw new InvalidOperationException(
                $"A table with the name '{tableName}' in module '{moduleName}' is already registered."
            );
        }
    }

    public void RegisterInstance(string moduleName, WasmInstance instance)
    {
        foreach (var export in instance.Module.Exports)
        {
            switch (export.Kind)
            {
                case ImportExportKind.Function:
                    items[new LinkerKey(moduleName, export.Name)] = new LinkerItem
                    {
                        Kind = ImportExportKind.Function,
                        Address = instance.GetFunctionAddress((int)export.Index),
                        Type = GetExportType(instance, export),
                        TypeModule = instance.Module,
                    };
                    break;
                case ImportExportKind.Table:
                    items[new LinkerKey(moduleName, export.Name)] = new LinkerItem
                    {
                        Kind = ImportExportKind.Table,
                        Address = instance.GetTableAddress((int)export.Index),
                        Type = GetExportType(instance, export),
                        TypeModule = instance.Module,
                    };
                    break;
                case ImportExportKind.Memory:
                    items[new LinkerKey(moduleName, export.Name)] = new LinkerItem
                    {
                        Kind = ImportExportKind.Memory,
                        Address = instance.GetMemoryAddress((int)export.Index),
                        Type = GetExportType(instance, export),
                        TypeModule = instance.Module,
                    };
                    break;
                case ImportExportKind.Global:
                    items[new LinkerKey(moduleName, export.Name)] = new LinkerItem
                    {
                        Kind = ImportExportKind.Global,
                        Address = instance.GetGlobalAddress((int)export.Index),
                        Type = GetExportType(instance, export),
                        TypeModule = instance.Module,
                    };
                    break;
                case ImportExportKind.Tag:
                    items[new LinkerKey(moduleName, export.Name)] = new LinkerItem
                    {
                        Kind = ImportExportKind.Tag,
                        Address = instance.GetTagAddress((int)export.Index),
                        Type = GetExportType(instance, export),
                        TypeModule = instance.Module,
                    };
                    break;
            }
        }
    }

    internal bool TryResolve(LinkerKey key, out LinkerItem item) =>
        items.TryGetValue(key, out item);

    public WasmInstance Instantiate(WasmModule module)
    {
        WasmValidator.Validate(module);

        var functionImportCount = 0;
        var tableImportCount = 0;
        var memoryImportCount = 0;
        var globalImportCount = 0;
        var tagImportCount = 0;
        foreach (var import in module.Imports)
        {
            switch (import.Kind)
            {
                case ImportExportKind.Function:
                    functionImportCount++;
                    break;
                case ImportExportKind.Table:
                    tableImportCount++;
                    break;
                case ImportExportKind.Memory:
                    memoryImportCount++;
                    break;
                case ImportExportKind.Global:
                    globalImportCount++;
                    break;
                case ImportExportKind.Tag:
                    tagImportCount++;
                    break;
            }
        }

        var functionAddresses = new FunctionAddress[functionImportCount + module.Functions.Length];
        var tableAddresses = new TableAddress[tableImportCount + module.Tables.Length];
        var memoryAddresses = new MemoryAddress[memoryImportCount + module.Memories.Length];
        var globalAddresses = new GlobalAddress[globalImportCount + module.Globals.Length];
        var tagAddresses = new TagAddress[tagImportCount + module.Tags.Length];

        var functionOffset = 0;
        var tableOffset = 0;
        var memoryOffset = 0;
        var globalOffset = 0;
        var tagOffset = 0;

        foreach (var import in module.Imports)
        {
            if (!TryResolve(new LinkerKey(import.Module, import.Name), out var item))
            {
                throw new WasmInstantiationException(
                    $"Import not found: {import.Module}.{import.Name}"
                );
            }

            if (item.Kind != import.Kind)
            {
                throw new WasmInstantiationException(
                    $"Import kind mismatch for {import.Module}.{import.Name}: expected {import.Kind}, got {item.Kind}"
                );
            }
            var actualType = GetCurrentLinkerItemType(item);
            if (!IsImportTypeCompatible(import.Type, actualType, module, item.TypeModule))
            {
                throw new WasmInstantiationException(
                    $"Import type mismatch for {import.Module}.{import.Name}"
                );
            }

            switch (import.Kind)
            {
                case ImportExportKind.Function:
                    functionAddresses[functionOffset++] = item.Address;
                    break;
                case ImportExportKind.Table:
                    tableAddresses[tableOffset++] = item.Address;
                    break;
                case ImportExportKind.Memory:
                    memoryAddresses[memoryOffset++] = item.Address;
                    break;
                case ImportExportKind.Global:
                    globalAddresses[globalOffset++] = item.Address;
                    break;
                case ImportExportKind.Tag:
                    tagAddresses[tagOffset++] = item.Address;
                    break;
            }
        }

        for (int i = 0; i < module.Tables.Length; i++)
        {
            var tableType = module.Tables.AsSpan()[i];
            tableAddresses[tableOffset + i] = store.AddTableInstance(
                new TableInstance(tableType.Minimum)
                {
                    AddressType = tableType.AddressType,
                    ElementType = tableType.ElementType,
                    Max = tableType.Maximum,
                }
            );
        }

        for (int i = 0; i < module.Memories.Length; i++)
        {
            var memoryType = module.Memories.AsSpan()[i];
            memoryAddresses[memoryOffset + i] = store.AddMemoryInstance(
                new MemoryInstance(checked((int)memoryType.Minimum))
                {
                    AddressType = memoryType.AddressType,
                    Max = memoryType.Maximum,
                }
            );
        }

        for (int i = 0; i < module.Globals.Length; i++)
        {
            var global = module.Globals.AsSpan()[i];
            globalAddresses[globalOffset + i] = store.AddGlobalInstance(
                new GlobalInstance
                {
                    Mutable = global.Type.Mutable,
                    ValueType = global.Type.ValueType,
                }
            );
        }

        for (int i = 0; i < module.Tags.Length; i++)
        {
            tagAddresses[tagOffset + i] = store.AddTagInstance(
                new TagInstance { Type = module.Tags.AsSpan()[i] }
            );
        }

        var instance = new WasmInstance(
            module,
            this,
            functionAddresses,
            tableAddresses,
            memoryAddresses,
            globalAddresses,
            tagAddresses
        );

        for (int i = 0; i < module.Functions.Length; i++)
        {
            functionAddresses[functionOffset + i] = store.AddFunctionInstance(
                new FunctionInstance(
                    new RuntimeFunction
                    {
                        Owner = instance,
                        Definition = module.Functions.AsSpan()[i],
                    }
                )
            );
        }

        var context = WasmExecutionContext.Rent();
        try
        {
            Span<WasmValue> result = new WasmValue[1];
            for (int i = 0; i < module.Globals.Length; i++)
            {
                var expr = module.Globals.AsSpan()[i].InitExpression;
                context.Execute(instance, expr, 0, 0, 1);
                context.TakeValues(result);
                store.GetGlobalInstance(globalAddresses[globalImportCount + i]).Value = result[0];
            }

            for (int i = 0; i < module.Tables.Length; i++)
            {
                var tableType = module.Tables.AsSpan()[i];
                if (tableType.InitExpression is null)
                    continue;

                context.Execute(instance, tableType.InitExpression, 0, 0, 1);
                context.TakeValues(result);
                instance.GetTableInstance(tableImportCount + i).References.Fill(result[0]);
            }

            for (var elementIndex = 0; elementIndex < module.Elements.Length; elementIndex++)
            {
                var element = module.Elements[elementIndex];
                var values = new WasmValue[element.Initializers.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    context.Execute(instance, element.Initializers[i], 0, 0, 1);
                    context.TakeValues(result);
                    values[i] = result[0];
                }
                instance.SetElementSegment(elementIndex, values);
            }

            for (var elementIndex = 0; elementIndex < module.Elements.Length; elementIndex++)
            {
                var element = module.Elements[elementIndex];
                if (element.Mode != ElementMode.Active)
                    continue;

                var table = instance.GetTableInstance((int)element.TableIndex);
                context.Execute(instance, element.Expression, 0, 0, 1);
                context.TakeValues(result);

                var offset =
                    table.AddressType == AddressType.I64 ? (int)result[0].I64 : result[0].I32;
                var initializers = element.Initializers.AsSpan();
                WasmTrapException.ThrowIfNot(
                    offset >= 0 && offset + initializers.Length <= table.References.Length,
                    "Element segment is out of bounds"
                );

                instance.GetElementSegment(elementIndex).CopyTo(table.References[offset..]);
            }

            foreach (var dataSegment in module.Data)
            {
                if (dataSegment.Mode != DataSegmentMode.Active)
                    continue;

                var memory = instance.GetMemoryInstance((int)dataSegment.MemoryIndex);
                context.Execute(instance, dataSegment.OffsetExpression, 0, 0, 1);
                context.TakeValues(result);

                var offset = memory.AddressType == AddressType.I64 ? result[0].I64 : result[0].I32;
                var data = dataSegment.Data.Span;
                WasmTrapException.ThrowIfNot(
                    offset >= 0 && (ulong)offset + (uint)data.Length <= (uint)memory.Data.Length,
                    "Data segment is out of bounds"
                );
                data.CopyTo(memory.Data[(int)offset..]);
            }

            if (module.StartFunctionIndex.HasValue)
            {
                var startFunction = instance.GetFunction((int)module.StartFunctionIndex.Value);
                context.ExecuteFunction(instance, startFunction);
            }

            return instance;
        }
        finally
        {
            WasmExecutionContext.Return(context);
        }
    }

    static ExternalType GetExportType(WasmInstance instance, Export export)
    {
        return export.Kind switch
        {
            ImportExportKind.Function => GetFunctionType(instance, (int)export.Index),
            ImportExportKind.Table => GetTableType(instance, (int)export.Index),
            ImportExportKind.Memory => GetMemoryType(instance, (int)export.Index),
            ImportExportKind.Global => GetGlobalType(instance, (int)export.Index),
            ImportExportKind.Tag => GetTagType(instance, (int)export.Index),
            _ => throw new InvalidOperationException("Invalid export kind."),
        };
    }

    ExternalType GetCurrentLinkerItemType(LinkerItem item)
    {
        return item.Kind switch
        {
            ImportExportKind.Table => GetTableType(store.GetTableInstance(item.Address)),
            ImportExportKind.Memory => GetMemoryType(store.GetMemoryInstance(item.Address)),
            ImportExportKind.Global => GetGlobalType(store.GetGlobalInstance(item.Address)),
            ImportExportKind.Tag => GetTagType(store.GetTagInstance(item.Address)),
            _ => item.Type,
        };
    }

    static ExternalType GetFunctionType(WasmInstance instance, int index)
    {
        return instance.GetFunction(index) switch
        {
            RuntimeFunction function => new ExternalType(
                GetFlatType(function.Owner.Module, function.Definition.TypeIndex) with
                {
                    TypeIndex = function.Definition.TypeIndex,
                }
            ),
            HostFunction function => new ExternalType(function.Type),
            _ => throw new InvalidOperationException("Invalid function instance type."),
        };
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
            throw new InvalidOperationException("Expected function type.");
        }
        throw new InvalidOperationException($"Type index {typeIndex} is out of bounds.");
    }

    static ExternalType GetTableType(WasmInstance instance, int index)
    {
        var table = instance.GetTableInstance(index);
        return GetTableType(table);
    }

    static ExternalType GetTableType(TableInstance table)
    {
        return new ExternalType(
            new TableType
            {
                AddressType = table.AddressType,
                Minimum = checked((uint)table.References.Length),
                Maximum = table.Max,
                ElementType = table.ElementType,
            }
        );
    }

    static ExternalType GetMemoryType(WasmInstance instance, int index)
    {
        var memory = instance.GetMemoryInstance(index);
        return GetMemoryType(memory);
    }

    static ExternalType GetMemoryType(MemoryInstance memory)
    {
        return new ExternalType(
            new MemoryType
            {
                AddressType = memory.AddressType,
                Minimum = checked((ulong)(memory.Data.Length / WasmStore.PageSize)),
                Maximum = memory.Max,
            }
        );
    }

    static ExternalType GetGlobalType(WasmInstance instance, int index)
    {
        var global = instance.GetGlobalInstance(index);
        return GetGlobalType(global);
    }

    static ExternalType GetGlobalType(GlobalInstance global)
    {
        return new ExternalType(
            new GlobalType { Mutable = global.Mutable, ValueType = global.ValueType }
        );
    }

    static ExternalType GetTagType(WasmInstance instance, int index)
    {
        var tag = instance.GetTagInstance(index);
        return tag.Type;
    }

    static ExternalType GetTagType(TagInstance tag) => new(tag.Type);

    static bool IsImportTypeCompatible(
        ExternalType expected,
        ExternalType actual,
        WasmModule expectedModule,
        WasmModule? actualModule
    )
    {
        return (expected, actual) switch
        {
            (FuncType expectedType, FuncType actualType) => FuncTypesAreCompatible(
                expectedType,
                actualType,
                expectedModule,
                actualModule
            ),
            (GlobalType expectedType, GlobalType actualType) => expectedType.Mutable
                == actualType.Mutable
                && IsGlobalValueTypeCompatible(
                    expectedType.ValueType,
                    actualType.ValueType,
                    expectedType.Mutable,
                    expectedModule,
                    actualModule
                ),
            (MemoryType expectedType, MemoryType actualType) => expectedType.AddressType
                == actualType.AddressType
                && LimitsMatch(
                    expectedType.Minimum,
                    expectedType.Maximum,
                    actualType.Minimum,
                    actualType.Maximum
                ),
            (TableType expectedType, TableType actualType) => expectedType.AddressType
                == actualType.AddressType
                && expectedType.ElementType == actualType.ElementType
                && LimitsMatch(
                    expectedType.Minimum,
                    expectedType.Maximum,
                    actualType.Minimum,
                    actualType.Maximum
                ),
            (TagType expectedType, TagType actualType) => TagTypesAreCompatible(
                expectedType,
                actualType,
                expectedModule,
                actualModule
            ),
            _ => false,
        };
    }

    static bool IsGlobalValueTypeCompatible(
        WasmValueType expected,
        WasmValueType actual,
        bool isMutable,
        WasmModule expectedModule,
        WasmModule? actualModule
    )
    {
        if (isMutable)
            return expected == actual;

        var expectedGraph = new TypeGraph(expectedModule.Types.AsSpan());
        if (actual.IsSubtypeOf(expected, expectedGraph))
            return true;

        return actualModule is not null
            && actual.Value is ConcreteType actualConcrete
            && expected.Value is FuncRef expectedFuncRef
            && ResolvedTypeIsFuncType(actualConcrete.TypeIndex, CreateSubTypeResolver(actualModule))
            && IsNullableReferenceSubtype(actualConcrete, expectedFuncRef);
    }

    static bool FuncTypesAreCompatible(
        FuncType left,
        FuncType right,
        WasmModule expectedModule,
        WasmModule? actualModule
    )
    {
        if (left.TypeIndex is { } expectedIndex && right.TypeIndex is { } actualIndex)
        {
            if (actualModule is not null && !ReferenceEquals(expectedModule, actualModule))
                return CrossModuleTypeIsSubtype(
                    actualModule,
                    actualIndex,
                    expectedModule,
                    expectedIndex
                );

            var expectedGraph = new TypeGraph(expectedModule.Types.AsSpan());
            return WasmTypes
                .ConcreteType(actualIndex, isNullable: false)
                .IsSubtypeOf(
                    WasmTypes.ConcreteType(expectedIndex, isNullable: false),
                    expectedGraph
                );
        }

        var graph = new TypeGraph(expectedModule.Types.AsSpan());
        var leftWvt = WasmTypes.Func(left.IsNullable, left.Parameters, left.Results);
        var rightWvt = WasmTypes.Func(right.IsNullable, right.Parameters, right.Results);
        return rightWvt.IsSubtypeOf(leftWvt, graph);
    }

    static bool CrossModuleTypeIsSubtype(
        WasmModule actualModule,
        uint actualTypeIndex,
        WasmModule expectedModule,
        uint expectedTypeIndex
    )
    {
        if (
            CrossModuleTypesAreEquivalent(
                actualModule,
                actualTypeIndex,
                expectedModule,
                expectedTypeIndex
            )
        )
            return true;

        if (!TryGetSubType(actualModule, actualTypeIndex, out var actualSubType))
            return false;

        foreach (var superType in actualSubType.SuperTypes.AsSpan())
        {
            if (
                CrossModuleTypeIsSubtype(actualModule, superType, expectedModule, expectedTypeIndex)
            )
                return true;
        }

        return false;
    }

    static bool CrossModuleTypesAreEquivalent(
        WasmModule leftModule,
        uint leftTypeIndex,
        WasmModule rightModule,
        uint rightTypeIndex
    )
    {
        if (
            !TryGetTypeLocation(leftModule, leftTypeIndex, out var leftStart, out var leftLength)
            || !TryGetTypeLocation(
                rightModule,
                rightTypeIndex,
                out var rightStart,
                out var rightLength
            )
        )
            return false;

        if (leftLength != rightLength || leftTypeIndex - leftStart != rightTypeIndex - rightStart)
            return false;

        var visited = new HashSet<(uint Left, uint Right)>();
        for (var i = 0u; i < leftLength; i++)
        {
            if (
                !CrossModuleSubTypesAreEquivalent(
                    leftModule,
                    leftStart + i,
                    rightModule,
                    rightStart + i,
                    leftStart,
                    rightStart,
                    leftLength,
                    visited
                )
            )
                return false;
        }

        return true;
    }

    static bool CrossModuleSubTypesAreEquivalent(
        WasmModule leftModule,
        uint leftTypeIndex,
        WasmModule rightModule,
        uint rightTypeIndex,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    )
    {
        if (!visited.Add((leftTypeIndex, rightTypeIndex)))
            return true;

        if (
            !TryGetSubType(leftModule, leftTypeIndex, out var left)
            || !TryGetSubType(rightModule, rightTypeIndex, out var right)
        )
            return false;

        return left.IsFinal == right.IsFinal
            && CrossModuleSuperTypesAreEquivalent(
                leftModule,
                left.SuperTypes,
                rightModule,
                right.SuperTypes,
                leftMappedStart,
                rightMappedStart,
                mappedLength,
                visited
            )
            && CrossModuleCompositeTypesAreEquivalent(
                leftModule,
                left.CompositeType,
                rightModule,
                right.CompositeType,
                leftMappedStart,
                rightMappedStart,
                mappedLength,
                visited
            );
    }

    static bool CrossModuleSuperTypesAreEquivalent(
        WasmModule leftModule,
        ImmutableArray<uint> left,
        WasmModule rightModule,
        ImmutableArray<uint> right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    )
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (
                !CrossModuleConcreteTypesAreEquivalent(
                    leftModule,
                    left[i],
                    rightModule,
                    right[i],
                    leftMappedStart,
                    rightMappedStart,
                    mappedLength,
                    visited
                )
            )
                return false;
        }

        return true;
    }

    static bool CrossModuleCompositeTypesAreEquivalent(
        WasmModule leftModule,
        CompositeType left,
        WasmModule rightModule,
        CompositeType right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    ) =>
        (left.Value, right.Value) switch
        {
            (FuncType l, FuncType r) => CrossModuleValueSequencesAreEquivalent(
                leftModule,
                l.Parameters.AsSpan(),
                rightModule,
                r.Parameters.AsSpan(),
                leftMappedStart,
                rightMappedStart,
                mappedLength,
                visited
            )
                && CrossModuleValueSequencesAreEquivalent(
                    leftModule,
                    l.Results.AsSpan(),
                    rightModule,
                    r.Results.AsSpan(),
                    leftMappedStart,
                    rightMappedStart,
                    mappedLength,
                    visited
                ),
            (StructType l, StructType r) => CrossModuleFieldSequencesAreEquivalent(
                leftModule,
                l.Fields.AsSpan(),
                rightModule,
                r.Fields.AsSpan(),
                leftMappedStart,
                rightMappedStart,
                mappedLength,
                visited
            ),
            (ArrayType l, ArrayType r) => CrossModuleFieldsAreEquivalent(
                leftModule,
                l.Field,
                rightModule,
                r.Field,
                leftMappedStart,
                rightMappedStart,
                mappedLength,
                visited
            ),
            _ => left.Equals(right),
        };

    static bool CrossModuleFieldSequencesAreEquivalent(
        WasmModule leftModule,
        ReadOnlySpan<FieldType> left,
        WasmModule rightModule,
        ReadOnlySpan<FieldType> right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    )
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (
                !CrossModuleFieldsAreEquivalent(
                    leftModule,
                    left[i],
                    rightModule,
                    right[i],
                    leftMappedStart,
                    rightMappedStart,
                    mappedLength,
                    visited
                )
            )
                return false;
        }

        return true;
    }

    static bool CrossModuleFieldsAreEquivalent(
        WasmModule leftModule,
        FieldType left,
        WasmModule rightModule,
        FieldType right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    ) =>
        left.Mutable == right.Mutable
        && CrossModuleStorageTypesAreEquivalent(
            leftModule,
            left.StorageType,
            rightModule,
            right.StorageType,
            leftMappedStart,
            rightMappedStart,
            mappedLength,
            visited
        );

    static bool CrossModuleStorageTypesAreEquivalent(
        WasmModule leftModule,
        StorageType left,
        WasmModule rightModule,
        StorageType right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    ) =>
        (left.Value, right.Value) switch
        {
            (PackedType l, PackedType r) => l == r,
            (WasmValueType l, WasmValueType r) => CrossModuleValueTypesAreEquivalent(
                leftModule,
                l,
                rightModule,
                r,
                leftMappedStart,
                rightMappedStart,
                mappedLength,
                visited
            ),
            _ => false,
        };

    static bool CrossModuleValueSequencesAreEquivalent(
        WasmModule leftModule,
        ReadOnlySpan<WasmValueType> left,
        WasmModule rightModule,
        ReadOnlySpan<WasmValueType> right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    )
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (
                !CrossModuleValueTypesAreEquivalent(
                    leftModule,
                    left[i],
                    rightModule,
                    right[i],
                    leftMappedStart,
                    rightMappedStart,
                    mappedLength,
                    visited
                )
            )
                return false;
        }

        return true;
    }

    static bool CrossModuleValueTypesAreEquivalent(
        WasmModule leftModule,
        WasmValueType left,
        WasmModule rightModule,
        WasmValueType right,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    ) =>
        (left.Value, right.Value) switch
        {
            (ConcreteType l, ConcreteType r) => l.IsNullable == r.IsNullable
                && CrossModuleConcreteTypesAreEquivalent(
                    leftModule,
                    l.TypeIndex,
                    rightModule,
                    r.TypeIndex,
                    leftMappedStart,
                    rightMappedStart,
                    mappedLength,
                    visited
                ),
            _ => left.Equals(right),
        };

    static bool CrossModuleConcreteTypesAreEquivalent(
        WasmModule leftModule,
        uint leftTypeIndex,
        WasmModule rightModule,
        uint rightTypeIndex,
        uint leftMappedStart,
        uint rightMappedStart,
        uint mappedLength,
        HashSet<(uint Left, uint Right)> visited
    )
    {
        var leftMapped = TypeIndexIsMapped(leftTypeIndex, leftMappedStart, mappedLength);
        var rightMapped = TypeIndexIsMapped(rightTypeIndex, rightMappedStart, mappedLength);
        if (leftMapped || rightMapped)
            return leftMapped
                && rightMapped
                && leftTypeIndex - leftMappedStart == rightTypeIndex - rightMappedStart;

        return CrossModuleTypesAreEquivalent(
            leftModule,
            leftTypeIndex,
            rightModule,
            rightTypeIndex
        );
    }

    static bool TypeIndexIsMapped(uint typeIndex, uint start, uint length) =>
        typeIndex >= start && typeIndex < start + length;

    static bool TagTypesAreCompatible(
        TagType expected,
        TagType actual,
        WasmModule expectedModule,
        WasmModule? actualModule
    )
    {
        if (actualModule is null)
            return expected.Type == actual.Type;

        if (
            !TryGetTypeLocation(
                expectedModule,
                expected.TypeIndex,
                out var expectedStart,
                out var expectedLength
            )
            || !TryGetTypeLocation(
                actualModule,
                actual.TypeIndex,
                out var actualStart,
                out var actualLength
            )
        )
            return false;

        if (
            expectedLength != actualLength
            || expected.TypeIndex - expectedStart != actual.TypeIndex - actualStart
        )
            return false;

        for (var i = 0u; i < expectedLength; i++)
        {
            if (
                !TryGetSubType(expectedModule, expectedStart + i, out var expectedSubType)
                || !TryGetSubType(actualModule, actualStart + i, out var actualSubType)
                || !expectedSubType.CompositeType.Equals(actualSubType.CompositeType)
            )
                return false;
        }

        return true;
    }

    static bool TryGetTypeLocation(
        WasmModule module,
        uint typeIndex,
        out uint groupStart,
        out uint groupLength
    )
    {
        var offset = 0u;
        foreach (var recursiveType in module.Types.AsSpan())
        {
            var length = (uint)recursiveType.SubTypes.Length;
            if (typeIndex < offset + length)
            {
                groupStart = offset;
                groupLength = length;
                return true;
            }
            offset += length;
        }

        groupStart = 0;
        groupLength = 0;
        return false;
    }

    static bool TryGetSubType(WasmModule module, uint typeIndex, out SubType subType)
    {
        var remaining = typeIndex;
        foreach (var recursiveType in module.Types.AsSpan())
        {
            if (remaining >= recursiveType.SubTypes.Length)
            {
                remaining -= checked((uint)recursiveType.SubTypes.Length);
                continue;
            }

            subType = recursiveType.SubTypes[checked((int)remaining)];
            return true;
        }

        subType = default!;
        return false;
    }

    static SubTypeResolver CreateSubTypeResolver(WasmModule module) =>
        typeIndex =>
        {
            var remaining = typeIndex;
            foreach (var rt in module.Types.AsSpan())
            {
                if (remaining >= rt.SubTypes.Length)
                {
                    remaining -= checked((uint)rt.SubTypes.Length);
                    continue;
                }
                return rt.SubTypes[checked((int)remaining)];
            }
            return null;
        };

    static bool ResolvedTypeIsFuncType(uint typeIndex, SubTypeResolver resolver) =>
        resolver(typeIndex)?.CompositeType is FuncType;

    static bool IsNullableReferenceSubtype(RefType actual, RefType expected) =>
        actual.IsNullable == expected.IsNullable || (!actual.IsNullable && expected.IsNullable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool LimitsMatch(
        ulong expectedMinimum,
        ulong? expectedMaximum,
        ulong actualMinimum,
        ulong? actualMaximum
    )
    {
        if (actualMinimum < expectedMinimum)
            return false;
        if (expectedMaximum is null)
            return true;
        return actualMaximum is not null && actualMaximum.Value <= expectedMaximum.Value;
    }
}
