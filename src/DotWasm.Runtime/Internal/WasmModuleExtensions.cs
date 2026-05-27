using DotWasm.Models;

namespace DotWasm.Runtime;

internal static class WasmModuleExtensions
{
    public static SubType GetSubType(this WasmModule module, uint typeIndex)
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

        ThrowHelper.ThrowInvalidOperation<object>("type index is out of bounds");
        return default; // unreachable
    }

    public static ArrayType GetArrayType(this WasmModule module, uint typeIndex) =>
        GetSubType(module, typeIndex).CompositeType switch
        {
            ArrayType type => type,
            _ => ThrowHelper.ThrowInvalidOperation<ArrayType>(
                "type index does not reference an array type"
            ),
        };

    public static StructType GetStructType(this WasmModule module, uint typeIndex) =>
        GetSubType(module, typeIndex).CompositeType switch
        {
            StructType type => type,
            _ => ThrowHelper.ThrowInvalidOperation<StructType>(
                "type index does not reference a struct type"
            ),
        };
}
