using DotWasm.Models;

namespace DotWasm.Runtime;

internal static class TypeExtensions
{
    public static bool IsRefType(this StorageType storageType) =>
        storageType is WasmValueType valueType && valueType.IsRefType;
}
