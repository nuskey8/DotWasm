using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotWasm.Models;

public delegate SubType? SubTypeResolver(uint typeIndex);

public union WasmValueType(
    I32Type,
    I64Type,
    F32Type,
    F64Type,
    V128Type,
    BottomType,
    RefType) : IEquatable<WasmValueType>
{

    public readonly bool IsRefType => this switch
    {
        BottomType => true,
        RefType => true,
        _ => false
    };

    public readonly bool IsBottom => Value is BottomType;

    public readonly bool IsNullable => this switch
    {
        RefType refType => refType.IsNullable,
        _ => false
    };

    public readonly bool IsDefaultable => this switch
    {
        I32Type or I64Type or F32Type or F64Type or V128Type => true,
        RefType refType => refType.IsNullable,
        _ => false
    };

    public readonly bool IsSubtypeOf(WasmValueType other, TypeGraph? graph = null) =>
        TypeRelations.IsSubtype(this, other, graph);

    public readonly bool IsSubtypeOf(
        WasmValueType other,
        ReadOnlySpan<RecursiveType> types
    ) =>
        TypeRelations.IsSubtype(
            this,
            other,
            types.IsEmpty ? null : new TypeGraph(types)
        );

    public readonly WasmValueType AsNonNullable() => this switch
    {
        RefType refType => refType with { IsNullable = false },
        _ => this
    };

    public readonly bool TryGetByteSize(out int size)
    {
        if (this is I32Type or F32Type)
        {
            size = 4;
            return true;
        }
        else if (this is I64Type or F64Type)
        {
            size = 8;
            return true;
        }
        else if (this is V128Type)
        {
            size = 16;
            return true;
        }
        else
        {
            size = 0;
            return false;
        }
    }

    public override readonly string ToString()
    {
        return this switch
        {
            I32Type => "i32",
            I64Type => "i64",
            F32Type => "f32",
            F64Type => "f64",
            V128Type => "v128",
            BottomType => "bottom",
            RefType refType => refType.ToString(),
            _ => throw new InvalidOperationException("Unknown WasmValueType")
        };
    }

    public readonly bool Equals(WasmValueType other)
    {
        if (Value == null)
            return other.Value == null;
        else
            return Value.Equals(other.Value);
    }

    public override readonly int GetHashCode()
    {
        return Value?.GetHashCode() ?? 0;
    }

    public override readonly bool Equals([NotNullWhen(true)] object? obj)
    {
        return Value == obj;
    }

    public static bool operator ==(WasmValueType left, WasmValueType right) => left.Equals(right);
    public static bool operator !=(WasmValueType left, WasmValueType right) => !left.Equals(right);
}

public sealed record I32Type
{
    internal static readonly I32Type Instance = new();
}

public sealed record I64Type
{
    internal static readonly I64Type Instance = new();
}

public sealed record F32Type
{
    internal static readonly F32Type Instance = new();
}

public sealed record F64Type
{
    internal static readonly F64Type Instance = new();
}

public sealed record V128Type
{
    internal static readonly V128Type Instance = new();
}

public sealed record BottomType
{
    internal static readonly BottomType Instance = new();
}

public abstract record RefType
{
    public bool IsNullable { get; init; } = true;
}

public sealed record ConcreteType(uint TypeIndex) : RefType
{
    public override string ToString() => IsNullable ? $"(ref null ${TypeIndex})" : $"(ref ${TypeIndex})";
}
public sealed record ExactType(uint TypeIndex) : RefType
{
    public override string ToString() => IsNullable ? $"(ref null ${TypeIndex})" : $"(ref ${TypeIndex})";
}

public abstract record AbstractHeapType : RefType;

public record FuncRef : AbstractHeapType
{
    public override string ToString() => IsNullable ? "ref null func" : "ref func";
}

public sealed record NoFuncRef : FuncRef
{
    public override string ToString() => IsNullable ? "ref null nofunc" : "ref nofunc";
}

public sealed record NoneRef : EqRef
{
    public override string ToString() => IsNullable ? "ref null none" : "ref none";
}

public sealed record FuncType : FuncRef, IEquatable<FuncType>
{
    public uint? TypeIndex { get; init; }
    public required ImmutableArray<WasmValueType> Parameters { get; init; }
    public required ImmutableArray<WasmValueType> Results { get; init; }

    public bool Equals(FuncType? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        return IsNullable == other.IsNullable
            && Parameters.AsSpan().SequenceEqual(other.Parameters.AsSpan())
            && Results.AsSpan().SequenceEqual(other.Results.AsSpan());
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsNullable);
        foreach (var param in Parameters)
            hash.Add(param);
        foreach (var result in Results)
            hash.Add(result);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var paramStr = Parameters.Length == 0 ? "" : $"(param {string.Join(' ', Parameters)})";
        var resultStr = Results.Length == 0 ? "" : $"(result {string.Join(' ', Results)})";
        return $"{(IsNullable ? "ref null func" : "ref func")} {paramStr} {resultStr}".Trim();
    }
}

public sealed record RecursiveType
{
    public required ImmutableArray<SubType> SubTypes { get; init; }

    public ImmutableArray<WasmValueType> Parameters => AsFunctionType().Parameters;
    public ImmutableArray<WasmValueType> Results => AsFunctionType().Results;

    public static RecursiveType From(SubType subType) => new()
    {
        SubTypes = [subType]
    };

    public FuncType AsFunctionType()
    {
        if (SubTypes.Length == 1 && SubTypes[0].CompositeType is FuncType funcType)
            return funcType;

        throw new InvalidOperationException("Recursive type is not a single function type.");
    }

    public static implicit operator FuncType(RecursiveType recursiveType) =>
        recursiveType.AsFunctionType();
}

public sealed record SubType
{
    public bool IsFinal { get; init; } = true;
    public ImmutableArray<uint> SuperTypes { get; init; } = [];
    public required CompositeType CompositeType { get; init; }
}

public union CompositeType(FuncType, StructType, ArrayType);

public sealed record StructType
{
    public required ImmutableArray<FieldType> Fields { get; init; }
}

public sealed record ArrayType
{
    public required FieldType Field { get; init; }
}

public sealed record FieldType
{
    public required StorageType StorageType { get; init; }
    public required bool Mutable { get; init; }
}

[Union]
[StructLayout(LayoutKind.Auto)]
public readonly struct StorageType : IUnion, IEquatable<StorageType>
{
    readonly bool isPacked;
    readonly WasmValueType valueType;
    readonly PackedType packedType;

    public StorageType(WasmValueType valueType)
    {
        this.isPacked = false;
        this.valueType = valueType;
        this.packedType = default;
    }

    public StorageType(PackedType packedType)
    {
        this.isPacked = true;
        this.valueType = default;
        this.packedType = packedType;
    }

    public static implicit operator StorageType(WasmValueType valueType) => new(valueType);
    public static implicit operator StorageType(PackedType packedType) => new(packedType);

    public bool TryGetValue(out WasmValueType valueType)
    {
        if (isPacked)
        {
            valueType = default;
            return false;
        }
        else
        {
            valueType = this.valueType;
            return true;
        }
    }

    public bool TryGetValue(out PackedType packedType)
    {
        if (isPacked)
        {
            packedType = this.packedType;
            return true;
        }
        else
        {
            packedType = default;
            return false;
        }
    }

    public bool TryGetByteSize(out int size)
    {
        if (isPacked)
        {
            size = packedType switch
            {
                PackedType.I8 => 1,
                PackedType.I16 => 2,
                _ => throw new InvalidOperationException("Unknown PackedType")
            };
            return true;
        }
        else
        {
            return valueType.TryGetByteSize(out size);
        }
    }

    public object Value => isPacked ? packedType : valueType;

    public override string ToString()
    {
        return isPacked ? packedType.ToString() : valueType.ToString();
    }

    public bool Equals(StorageType other)
    {
        return isPacked == other.isPacked
            && (isPacked ? packedType == other.packedType : valueType.Equals(other.valueType));
    }

    public override bool Equals(object? obj)
    {
        return obj is StorageType other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(isPacked, isPacked ? packedType.GetHashCode() : valueType.GetHashCode());
    }

    public static bool operator ==(StorageType left, StorageType right) => left.Equals(right);
    public static bool operator !=(StorageType left, StorageType right) => !left.Equals(right);
}

public enum PackedType : byte
{
    I8,
    I16,
}

public record AnyRef : AbstractHeapType
{
    public override string ToString() => IsNullable ? "ref null any" : "ref any";
}

public record EqRef : AnyRef
{
    public override string ToString() => IsNullable ? "ref null eq" : "ref eq";
}

public sealed record I31Ref : EqRef
{
    public override string ToString() => IsNullable ? "ref null i31" : "ref i31";
}

public sealed record StructRef : EqRef
{
    public override string ToString() => IsNullable ? "ref null struct" : "ref struct";
}

public sealed record ArrayRef : EqRef
{
    public override string ToString() => IsNullable ? "ref null array" : "ref array";
}

public record ExternRef : AnyRef
{
    public override string ToString() => IsNullable ? "ref null extern" : "ref extern";
}

public sealed record NoExternRef : ExternRef
{
    public override string ToString() => IsNullable ? "ref null noextern" : "ref noextern";
}

public record ExnRef : AbstractHeapType
{
    public override string ToString() => IsNullable ? "exnref" : "ref exn";
}

public sealed record NoExnRef : ExnRef
{
    public override string ToString() => IsNullable ? "ref null noexn" : "ref noexn";
}

public static class WasmTypes
{
    static readonly FuncRef funcRefNullableInstance = new() { IsNullable = true };
    static readonly FuncRef funcRefNonNullableInstance = new() { IsNullable = false };
    static readonly NoFuncRef noFuncRefNullableInstance = new() { IsNullable = true };
    static readonly NoFuncRef noFuncRefNonNullableInstance = new() { IsNullable = false };
    static readonly NoneRef noneRefNullableInstance = new() { IsNullable = true };
    static readonly NoneRef noneRefNonNullableInstance = new() { IsNullable = false };
    static readonly ExternRef externRefNullableInstance = new() { IsNullable = true };
    static readonly ExternRef externRefNonNullableInstance = new() { IsNullable = false };
    static readonly NoExternRef noExternRefNullableInstance = new() { IsNullable = true };
    static readonly NoExternRef noExternRefNonNullableInstance = new() { IsNullable = false };
    static readonly ExnRef exnRefNullableInstance = new() { IsNullable = true };
    static readonly ExnRef exnRefNonNullableInstance = new() { IsNullable = false };
    static readonly NoExnRef noExnRefNullableInstance = new() { IsNullable = true };
    static readonly NoExnRef noExnRefNonNullableInstance = new() { IsNullable = false };
    static readonly I31Ref i31RefNullableInstance = new() { IsNullable = true };
    static readonly I31Ref i31RefNonNullableInstance = new() { IsNullable = false };
    static readonly AnyRef anyRefNullableInstance = new() { IsNullable = true };
    static readonly AnyRef anyRefNonNullableInstance = new() { IsNullable = false };
    static readonly EqRef eqRefNullableInstance = new() { IsNullable = true };
    static readonly EqRef eqRefNonNullableInstance = new() { IsNullable = false };
    static readonly StructRef structRefNullableInstance = new() { IsNullable = true };
    static readonly StructRef structRefNonNullableInstance = new() { IsNullable = false };
    static readonly ArrayRef arrayRefNullableInstance = new() { IsNullable = true };
    static readonly ArrayRef arrayRefNonNullableInstance = new() { IsNullable = false };

    public static WasmValueType I32 => I32Type.Instance;
    public static WasmValueType I64 => I64Type.Instance;
    public static WasmValueType F32 => F32Type.Instance;
    public static WasmValueType F64 => F64Type.Instance;
    public static WasmValueType V128 => V128Type.Instance;
    public static WasmValueType Bottom => BottomType.Instance;

    public static WasmValueType FuncRef(bool isNullable) => isNullable
        ? funcRefNullableInstance
        : funcRefNonNullableInstance;

    public static WasmValueType NoFuncRef(bool isNullable) => isNullable
        ? noFuncRefNullableInstance
        : noFuncRefNonNullableInstance;

    public static WasmValueType NoneRef(bool isNullable) => isNullable
        ? noneRefNullableInstance
        : noneRefNonNullableInstance;

    public static WasmValueType Func(
        bool isNullable,
        ImmutableArray<WasmValueType> parameters,
        ImmutableArray<WasmValueType> results) => new FuncType
        {
            IsNullable = isNullable,
            Parameters = parameters,
            Results = results
        };

    public static WasmValueType ExternRef(bool isNullable) => isNullable
        ? externRefNullableInstance
        : externRefNonNullableInstance;

    public static WasmValueType NoExternRef(bool isNullable) => isNullable
        ? noExternRefNullableInstance
        : noExternRefNonNullableInstance;

    public static WasmValueType ExnRef(bool isNullable) => isNullable
        ? exnRefNullableInstance
        : exnRefNonNullableInstance;

    public static WasmValueType NoExnRef(bool isNullable) => isNullable
        ? noExnRefNullableInstance
        : noExnRefNonNullableInstance;

    public static WasmValueType I31Ref(bool isNullable) => isNullable
        ? i31RefNullableInstance
        : i31RefNonNullableInstance;

    public static WasmValueType AnyRef(bool isNullable) => isNullable
        ? anyRefNullableInstance
        : anyRefNonNullableInstance;

    public static WasmValueType EqRef(bool isNullable) => isNullable
        ? eqRefNullableInstance
        : eqRefNonNullableInstance;

    public static WasmValueType StructRef(bool isNullable) => isNullable
        ? structRefNullableInstance
        : structRefNonNullableInstance;

    public static WasmValueType ArrayRef(bool isNullable) => isNullable
        ? arrayRefNullableInstance
        : arrayRefNonNullableInstance;

    public static WasmValueType ConcreteType(uint typeIndex, bool isNullable) => new ConcreteType(typeIndex)
    {
        IsNullable = isNullable
    };
}

public enum AddressType : byte
{
    I32,
    I64,
}

public sealed record MemoryType
{
    public AddressType AddressType { get; init; } = AddressType.I32;
    public required ulong Minimum { get; init; }
    public ulong? Maximum { get; init; }
}

public sealed record GlobalType
{
    public required WasmValueType ValueType { get; init; }
    public required bool Mutable { get; init; }
}

public sealed record TagType
{
    public required uint TypeIndex { get; init; }
    public required FuncType Type { get; init; }
}

public sealed record TableType
{
    public AddressType AddressType { get; init; } = AddressType.I32;
    public required ulong Minimum { get; init; }
    public ulong? Maximum { get; init; }
    public required WasmValueType ElementType { get; init; }
    public Expression? InitExpression { get; init; }
}

public union ExternalType(FuncType, TableType, MemoryType, GlobalType, TagType);
