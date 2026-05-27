using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DotWasm.Models;

namespace DotWasm.Runtime;

public abstract class GcObject
{
    public required WasmModule Module { get; init; }
    public required uint TypeIndex { get; init; }
    public required byte[] Data { get; init; }
    public required WasmValue[] References { get; init; }

    internal void Write(FieldType field, int index, WasmValue value)
    {
        if (field.StorageType.TryGetByteSize(out var size))
        {
            StorageAccess.Write(Data.AsSpan(index * size), field.StorageType, value);
        }
        else
        {
            References[index] = value;
        }
    }

    internal void Write(FieldLayout field, WasmValue value)
    {
        if (field.ReferenceIndex >= 0)
            References[field.ReferenceIndex] = value;
        else
            StorageAccess.Write(Data.AsSpan(field.DataOffset), field.StorageType, value);
    }

    internal WasmValue Read(FieldType field, int index)
    {
        if (field.StorageType.TryGetByteSize(out var size))
        {
            return StorageAccess.Read(Data.AsSpan(index * size), field.StorageType);
        }
        else
        {
            return References[index];
        }
    }

    internal WasmValue Read(FieldLayout field, Signedness signedness)
    {
        if (field.ReferenceIndex >= 0)
            return References[field.ReferenceIndex];

        var data = Data.AsSpan(field.DataOffset);
        return (field.StorageType.Value, signedness) switch
        {
            (PackedType.I8, Signedness.Unsigned) => WasmValue.FromI32(data[0]),
            (PackedType.I16, Signedness.Unsigned) => WasmValue.FromI32(
                BinaryPrimitives.ReadUInt16LittleEndian(data)
            ),
            _ => StorageAccess.Read(data, field.StorageType),
        };
    }

    internal WasmValue Read(FieldType field, int index, Signedness signedness)
    {
        if (field.StorageType.TryGetByteSize(out var size))
        {
            var data = Data.AsSpan(index * size);
            return (field.StorageType.Value, signedness) switch
            {
                (PackedType.I8, Signedness.Unsigned) => WasmValue.FromI32(data[0]),
                (PackedType.I16, Signedness.Unsigned) => WasmValue.FromI32(
                    BinaryPrimitives.ReadUInt16LittleEndian(data)
                ),
                _ => StorageAccess.Read(data, field.StorageType),
            };
        }
        else
        {
            return References[index];
        }
    }
}

public sealed class GcArray : GcObject
{
    public required int Length { get; init; }

    public static GcArray Create(
        WasmModule module,
        uint typeIndex,
        int length,
        WasmValue initialValue
    )
    {
        var field = module.GetArrayType(typeIndex).Field;
        var elementSize = 0;
        if (field.StorageType.TryGetByteSize(out var size))
        {
            elementSize = size;
        }

        var obj = new GcArray
        {
            Module = module,
            TypeIndex = typeIndex,
            Length = length,
            Data = elementSize == 0 ? [] : new byte[checked(elementSize * length)],
            References = field.StorageType.IsRefType() ? new WasmValue[length] : [],
        };

        for (var i = 0; i < length; i++)
        {
            obj.Write(field, i, initialValue);
        }
        return obj;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WasmValue GetElement(int index)
    {
        var field = Module.GetArrayType(TypeIndex).Field;
        return Read(field, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetElement(int index, WasmValue value)
    {
        var field = Module.GetArrayType(TypeIndex).Field;
        Write(field, index, value);
    }
}

public sealed class GcStruct : GcObject
{
    public required int FieldCount { get; init; }

    public static GcStruct CreateDefault(WasmModule module, uint typeIndex)
    {
        var structType = module.GetStructType(typeIndex);
        var layout = StructLayoutHelper.GetStructLayoutInfo(structType);
        var obj = new GcStruct
        {
            Module = module,
            TypeIndex = typeIndex,
            FieldCount = layout.FieldCount,
            Data = new byte[layout.DataSize],
            References = new WasmValue[layout.ReferenceCount],
        };

        var dataOffset = layout.DataSize;
        var referenceIndex = layout.ReferenceCount;
        for (var i = structType.Fields.Length - 1; i >= 0; i--)
        {
            var storageType = structType.Fields[i].StorageType;
            FieldLayout field;
            if (storageType.TryGetByteSize(out var byteSize))
            {
                field = new FieldLayout(storageType, dataOffset -= byteSize, -1);
            }
            else
            {
                field = new FieldLayout(storageType, -1, --referenceIndex);
            }
            obj.Write(field, WasmValue.Default(storageType));
        }

        return obj;
    }

    public static GcStruct Create(
        WasmModule module,
        uint typeIndex,
        ReadOnlySpan<WasmValue> fieldValues
    )
    {
        var structType = module.GetStructType(typeIndex);
        var layout = StructLayoutHelper.GetStructLayoutInfo(structType);
        var obj = new GcStruct
        {
            Module = module,
            TypeIndex = typeIndex,
            FieldCount = layout.FieldCount,
            Data = new byte[layout.DataSize],
            References = new WasmValue[layout.ReferenceCount],
        };

        var dataOffset = layout.DataSize;
        var referenceIndex = layout.ReferenceCount;
        for (var i = structType.Fields.Length - 1; i >= 0; i--)
        {
            var storageType = structType.Fields[i].StorageType;
            FieldLayout field;
            if (storageType.TryGetByteSize(out var byteSize))
            {
                field = new FieldLayout(storageType, dataOffset -= byteSize, -1);
            }
            else
            {
                field = new FieldLayout(storageType, -1, --referenceIndex);
            }
            obj.Write(field, fieldValues[i]);
        }

        return obj;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WasmValue GetField(int fieldIndex)
    {
        var structType = Module.GetStructType(TypeIndex);
        var field = structType.Fields[fieldIndex];
        return Read(field, fieldIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetField(int fieldIndex, WasmValue value)
    {
        var structType = Module.GetStructType(TypeIndex);
        var field = structType.Fields[fieldIndex];
        Write(field, fieldIndex, value);
    }
}
