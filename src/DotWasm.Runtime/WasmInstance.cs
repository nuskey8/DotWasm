using System.Buffers;
using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using DotWasm.Models;

namespace DotWasm.Runtime;

public sealed class WasmInstance
{
    readonly WasmModule module;
    readonly WasmLinker linker;
    readonly bool useInterpreter;

    readonly FunctionAddress[] functions;
    readonly TableAddress[] tables;
    readonly MemoryAddress[] memories;
    readonly GlobalAddress[] globals;
    readonly TagAddress[] tags;
    readonly BitArray droppedDataSegments;
    readonly BitArray droppedElementSegments;
    readonly WasmValue[][] elementSegments;

    readonly FrozenDictionary<string, Export> exports;

    public WasmModule Module => module;
    public WasmLinker Linker => linker;
    public WasmStore Store => linker.Store;
    public bool UseInterpreter => useInterpreter;

    internal bool IsDataSegmentDropped(int index) => droppedDataSegments[index];

    internal void DropDataSegment(int index) => droppedDataSegments[index] = true;

    internal bool IsElementSegmentDropped(int index) => droppedElementSegments[index];

    internal void DropElementSegment(int index) => droppedElementSegments[index] = true;

    internal ReadOnlySpan<WasmValue> GetElementSegment(int index) => elementSegments[index];

    internal void SetElementSegment(int index, WasmValue[] values) =>
        elementSegments[index] = values;

    public FunctionAddress GetFunctionAddress(int index) => functions[index];

    public TableAddress GetTableAddress(int index) => tables[index];

    public MemoryAddress GetMemoryAddress(int index) => memories[index];

    public GlobalAddress GetGlobalAddress(int index) => globals[index];

    public TagAddress GetTagAddress(int index) => tags[index];

    public FunctionInstance GetFunction(int index) =>
        linker.Store.GetFunctionInstance(functions[index]);

    public TableInstance GetTableInstance(int index) =>
        linker.Store.GetTableInstance(tables[index]);

    public MemoryInstance GetMemoryInstance(int index) =>
        linker.Store.GetMemoryInstance(memories[index]);

    public GlobalInstance GetGlobalInstance(int index) =>
        linker.Store.GetGlobalInstance(globals[index]);

    public TagInstance GetTagInstance(int index) => linker.Store.GetTagInstance(tags[index]);

    internal WasmInstance(
        WasmModule module,
        WasmLinker linker,
        FunctionAddress[] functions,
        TableAddress[] tables,
        MemoryAddress[] memories,
        GlobalAddress[] globals,
        TagAddress[] tags,
        bool useInterpreter = false
    )
    {
        this.module = module;
        this.linker = linker;
        this.useInterpreter = useInterpreter;
        this.functions = functions;
        this.tables = tables;
        this.memories = memories;
        this.globals = globals;
        this.tags = tags;
        droppedDataSegments = new BitArray(module.Data.Length);
        droppedElementSegments = new BitArray(module.Elements.Length);
        elementSegments = new WasmValue[module.Elements.Length][];

        var buffer = ArrayPool<KeyValuePair<string, Export>>.Shared.Rent(module.Exports.Length);
        try
        {
            for (int i = 0; i < module.Exports.Length; i++)
            {
                var export = module.Exports.AsSpan()[i];
                buffer[i] = new KeyValuePair<string, Export>(export.Name, export);
            }

            exports = FrozenDictionary.Create(buffer.AsSpan(0, module.Exports.Length));
        }
        finally
        {
            ArrayPool<KeyValuePair<string, Export>>.Shared.Return(buffer);
        }
    }

    public bool TryGetExportedGlobal(
        string exportName,
        [NotNullWhen(true)] out GlobalInstance? global
    )
    {
        if (
            !exports.TryGetValue(exportName, out var export)
            || export.Kind != ImportExportKind.Global
        )
        {
            global = default;
            return false;
        }

        global = GetGlobalInstance((int)export.Index);
        return true;
    }

    public bool TryGetExportedMemory(
        string exportName,
        [NotNullWhen(true)] out MemoryInstance? memory
    )
    {
        if (
            !exports.TryGetValue(exportName, out var export)
            || export.Kind != ImportExportKind.Memory
        )
        {
            memory = default;
            return false;
        }

        memory = GetMemoryInstance((int)export.Index);
        return true;
    }

    public bool TryGetExportedTable(string exportName, [NotNullWhen(true)] out TableInstance? table)
    {
        if (
            !exports.TryGetValue(exportName, out var export)
            || export.Kind != ImportExportKind.Table
        )
        {
            table = default;
            return false;
        }

        table = GetTableInstance((int)export.Index);
        return true;
    }

    public bool TryGetExportedFunction(string exportName, out FunctionInstance function)
    {
        if (
            !exports.TryGetValue(exportName, out var export)
            || export.Kind != ImportExportKind.Function
        )
        {
            function = default;
            return false;
        }

        function = GetFunction((int)export.Index);
        return true;
    }

    public bool TryGetExportedTag(string exportName, [NotNullWhen(true)] out TagInstance? tag)
    {
        if (!exports.TryGetValue(exportName, out var export) || export.Kind != ImportExportKind.Tag)
        {
            tag = default;
            return false;
        }

        tag = GetTagInstance((int)export.Index);
        return true;
    }

    public void Invoke(
        string exportName,
        ReadOnlySpan<WasmValue> arguments,
        Span<WasmValue> results
    )
    {
        if (!exports.TryGetValue(exportName, out var export))
            throw new ArgumentException($"Export not found: {exportName}", nameof(exportName));

        if (export.Kind != ImportExportKind.Function)
            WasmTrapException.Throw($"Export is not a function: {exportName}");

        var context = WasmExecutionContext.Rent();
        try
        {
            var function = GetFunction((int)export.Index);
            context.PushValues(arguments);
            context.ExecuteFunction(this, function);
            context.TakeValues(results);
        }
        finally
        {
            WasmExecutionContext.Return(context);
        }
    }
}
