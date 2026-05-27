using System.Runtime.InteropServices;

namespace DotWasm.Models;

[StructLayout(LayoutKind.Auto)]
public readonly record struct BlockType(WasmValueType? ValueType, uint? TypeIndex)
{
    public static BlockType Empty => new(null, null);

    public static BlockType FromValueType(WasmValueType valueType) => new(valueType, null);

    public static BlockType FromTypeIndex(uint typeIndex) => new(null, typeIndex);

    public static BlockType FromNullableValueType(WasmValueType? valueType) =>
        valueType.HasValue ? FromValueType(valueType.Value) : Empty;
}
