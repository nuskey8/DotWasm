using System.Buffers.Binary;
using DotWasm.Models;

namespace DotWasm.Runtime;

internal static class StorageAccess
{
    public static WasmValue Read(Span<byte> data, in StorageType storageType)
    {
        return storageType switch
        {
            PackedType.I8 => WasmValue.FromI32((sbyte)data[0]),
            PackedType.I16 => WasmValue.FromI32(BinaryPrimitives.ReadInt16LittleEndian(data)),
            WasmValueType valueType when valueType == WasmTypes.I32 => WasmValue.FromI32(
                BinaryPrimitives.ReadInt32LittleEndian(data)
            ),
            WasmValueType valueType when valueType == WasmTypes.I64 => WasmValue.FromI64(
                BinaryPrimitives.ReadInt64LittleEndian(data)
            ),
            WasmValueType valueType when valueType == WasmTypes.F32 => WasmValue.FromF32(
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data))
            ),
            WasmValueType valueType when valueType == WasmTypes.F64 => WasmValue.FromF64(
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data))
            ),
            WasmValueType valueType when valueType == WasmTypes.V128 => WasmValue.FromV128(
                new WasmV128Value(
                    BinaryPrimitives.ReadUInt64LittleEndian(data),
                    BinaryPrimitives.ReadUInt64LittleEndian(data[8..])
                )
            ),
            _ => throw new InvalidOperationException("unsupported storage type"),
        };
    }

    public static void Write(Span<byte> data, in StorageType storageType, WasmValue value)
    {
        switch (storageType)
        {
            case PackedType.I8:
                data[0] = (byte)value.I32;
                break;
            case PackedType.I16:
                BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)value.I32);
                break;
            case WasmValueType valueType when valueType == WasmTypes.I32:
                BinaryPrimitives.WriteInt32LittleEndian(data, value.I32);
                break;
            case WasmValueType valueType when valueType == WasmTypes.I64:
                BinaryPrimitives.WriteInt64LittleEndian(data, value.I64);
                break;
            case WasmValueType valueType when valueType == WasmTypes.F32:
                BinaryPrimitives.WriteInt32LittleEndian(
                    data,
                    BitConverter.SingleToInt32Bits(value.F32)
                );
                break;
            case WasmValueType valueType when valueType == WasmTypes.F64:
                BinaryPrimitives.WriteInt64LittleEndian(
                    data,
                    BitConverter.DoubleToInt64Bits(value.F64)
                );
                break;
            case WasmValueType valueType when valueType == WasmTypes.V128:
                BinaryPrimitives.WriteUInt64LittleEndian(data, value.V128.LowerBits);
                BinaryPrimitives.WriteUInt64LittleEndian(data[8..], value.V128.UpperBits);
                break;
            default:
                throw new InvalidOperationException("unsupported storage type");
        }
    }
}
