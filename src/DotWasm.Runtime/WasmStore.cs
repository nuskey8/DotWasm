using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotWasm.Runtime;

public sealed class WasmStore
{
    public const int PageSize = 65536;

    GlobalInstance[] globals = new GlobalInstance[128];
    TableInstance[] tables = new TableInstance[128];
    MemoryInstance[] memories = new MemoryInstance[128];
    FunctionInstance[] functions = new FunctionInstance[128];
    TagInstance[] tags = new TagInstance[128];

    int globalCount;
    int tableCount;
    int memoryCount;
    int functionCount;
    int tagCount;

    public int GlobalCount => globalCount;
    public int TableCount => tableCount;
    public int MemoryCount => memoryCount;
    public int FunctionCount => functionCount;
    public int TagCount => tagCount;

    internal GlobalAddress AddGlobalInstance(GlobalInstance instance)
    {
        if (globalCount >= globals.Length)
            Array.Resize(ref globals, globals.Length * 2);
        globals[globalCount++] = instance;
        return new GlobalAddress(globalCount - 1);
    }

    internal TableAddress AddTableInstance(TableInstance instance)
    {
        if (tableCount >= tables.Length)
            Array.Resize(ref tables, tables.Length * 2);
        tables[tableCount++] = instance;
        return new TableAddress(tableCount - 1);
    }

    internal MemoryAddress AddMemoryInstance(MemoryInstance instance)
    {
        if (memoryCount >= memories.Length)
            Array.Resize(ref memories, memories.Length * 2);
        memories[memoryCount++] = instance;
        return new MemoryAddress(memoryCount - 1);
    }

    internal FunctionAddress AddFunctionInstance(FunctionInstance instance)
    {
        if (functionCount >= functions.Length)
            Array.Resize(ref functions, functions.Length * 2);
        functions[functionCount++] = instance;
        return new FunctionAddress(functionCount - 1);
    }

    internal TagAddress AddTagInstance(TagInstance instance)
    {
        if (tagCount >= tags.Length)
            Array.Resize(ref tags, tags.Length * 2);
        tags[tagCount++] = instance;
        return new TagAddress(tagCount - 1);
    }

    public GlobalInstance GetGlobalInstance(GlobalAddress address)
    {
        if ((uint)address.Value >= (uint)globalCount)
            WasmTrapException.Throw($"Invalid global address: {address.Value}");
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(globals), (int)address.Value);
    }

    public TableInstance GetTableInstance(TableAddress address)
    {
        if ((uint)address.Value >= (uint)tableCount)
            WasmTrapException.Throw($"Invalid table address: {address.Value}");
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(tables), (int)address.Value);
    }

    public MemoryInstance GetMemoryInstance(MemoryAddress address)
    {
        if ((uint)address.Value >= (uint)memoryCount)
            WasmTrapException.Throw($"Invalid memory address: {address.Value}");
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(memories), (int)address.Value);
    }

    public FunctionInstance GetFunctionInstance(FunctionAddress address)
    {
        if ((uint)address.Value >= (uint)functionCount)
            WasmTrapException.Throw($"Invalid function address: {address.Value}");
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(functions), (int)address.Value);
    }

    public TagInstance GetTagInstance(TagAddress address)
    {
        if ((uint)address.Value >= (uint)tagCount)
            WasmTrapException.Throw($"Invalid tag address: {address.Value}");
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(tags), (int)address.Value);
    }
}
