using DotWasm.Models;

namespace DotWasm.Runtime;

internal readonly record struct FieldLayout(
    StorageType StorageType,
    int DataOffset,
    int ReferenceIndex
);

internal readonly record struct StructLayoutInfo(int FieldCount, int DataSize, int ReferenceCount);

internal static class StructLayoutHelper
{
    public static StructLayoutInfo GetStructLayoutInfo(StructType structType)
    {
        var dataSize = 0;
        var referenceCount = 0;
        foreach (var field in structType.Fields.AsSpan())
        {
            var storageType = field.StorageType;
            if (storageType.IsRefType())
                referenceCount++;
            {
                _ = storageType.TryGetByteSize(out var size);
                dataSize += size;
            }
        }

        return new StructLayoutInfo(structType.Fields.Length, dataSize, referenceCount);
    }

    public static FieldLayout GetStructFieldLayout(
        WasmModule module,
        uint typeIndex,
        uint fieldIndex
    )
    {
        var structType = module.GetStructType(typeIndex);
        var index = checked((int)fieldIndex);
        if ((uint)index >= (uint)structType.Fields.Length)
            throw new InvalidOperationException("field index is out of range");

        var dataOffset = 0;
        var refIndex = 0;
        for (var i = 0; i <= index; i++)
        {
            var storageType = structType.Fields[i].StorageType;
            if (storageType.IsRefType())
            {
                if (i == index)
                    return new FieldLayout(storageType, -1, refIndex);
                refIndex++;
                continue;
            }

            if (i == index)
                return new FieldLayout(storageType, dataOffset, -1);

            _ = storageType.TryGetByteSize(out var size);
            dataOffset += size;
        }

        throw new InvalidOperationException("field index is out of range");
    }
}
