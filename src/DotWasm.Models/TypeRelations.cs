using System.Collections.Immutable;

namespace DotWasm.Models;

public sealed class TypeGraph
{
    readonly SubType[] definedTypes;
    readonly int[] groupStart;
    readonly int[] groupLength;

    public TypeGraph(ReadOnlySpan<RecursiveType> recursiveTypes)
    {
        var count = 0;
        foreach (var recursiveType in recursiveTypes)
            count += recursiveType.SubTypes.Length;

        definedTypes = new SubType[count];
        groupStart = new int[count];
        groupLength = new int[count];

        var offset = 0;
        foreach (var recursiveType in recursiveTypes)
        {
            var length = recursiveType.SubTypes.Length;
            for (var i = 0; i < length; i++)
            {
                definedTypes[offset + i] = recursiveType.SubTypes[i];
                groupStart[offset + i] = offset;
                groupLength[offset + i] = length;
            }
            offset += length;
        }
    }

    public bool TryGetSubType(uint typeIndex, out SubType subType)
    {
        if (typeIndex < definedTypes.Length)
        {
            subType = definedTypes[(int)typeIndex];
            return true;
        }

        subType = default!;
        return false;
    }

    public bool IsDeclaredSubtype(uint actual, uint expected)
    {
        if (actual == expected || IsEquivalent(actual, expected))
            return true;
        if (!TryGetSubType(actual, out var subType))
            return false;

        foreach (var superType in subType.SuperTypes.AsSpan())
        {
            if (IsDeclaredSubtype(superType, expected))
                return true;
        }

        return false;
    }

    public bool IsCompositeSubtype(CompositeType actual, CompositeType expected) =>
        (actual.Value, expected.Value) switch
        {
            (FuncType a, FuncType e) => TypeRelations.FuncSubtype(a, e, this),
            (ArrayType a, ArrayType e) => FieldSubtype(a.Field, e.Field),
            (StructType a, StructType e) => StructSubtype(a.Fields.AsSpan(), e.Fields.AsSpan()),
            _ => false,
        };

    bool StructSubtype(ReadOnlySpan<FieldType> actual, ReadOnlySpan<FieldType> expected)
    {
        if (actual.Length < expected.Length)
            return false;
        for (var i = 0; i < expected.Length; i++)
        {
            if (!FieldSubtype(actual[i], expected[i]))
                return false;
        }
        return true;
    }

    bool FieldSubtype(FieldType actual, FieldType expected)
    {
        if (actual.Mutable != expected.Mutable)
            return false;
        if (actual.Mutable)
            return StorageEquivalent(actual.StorageType, expected.StorageType, [], -1, -1, 0);
        return StorageSubtype(actual.StorageType, expected.StorageType);
    }

    bool StorageSubtype(StorageType actual, StorageType expected) =>
        (actual, expected) switch
        {
            (PackedType a, PackedType e) => a == e,
            (WasmValueType a, WasmValueType e) => TypeRelations.IsSubtype(a, e, this),
            _ => false,
        };

    public bool IsEquivalent(uint left, uint right)
    {
        if (left == right)
            return true;
        if (!TryGetSubType(left, out _) || !TryGetSubType(right, out _))
            return false;

        var leftStart = groupStart[(int)left];
        var rightStart = groupStart[(int)right];
        var length = groupLength[(int)left];
        if (length != groupLength[(int)right])
            return false;

        return IsEquivalent(left, right, [], leftStart, rightStart, length);
    }

    bool IsEquivalent(
        uint left,
        uint right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    )
    {
        if (left == right && mappedLeftStart == mappedRightStart)
            return true;
        if (!visited.Add((left, right)))
            return true;
        if (!TryGetSubType(left, out var leftType) || !TryGetSubType(right, out var rightType))
            return false;

        var leftStart = groupStart[(int)left];
        var rightStart = groupStart[(int)right];
        var length = groupLength[(int)left];
        if (length != groupLength[(int)right])
            return false;

        if (leftStart != rightStart)
        {
            for (var i = 0; i < length; i++)
            {
                if (
                    !IsEquivalent(
                        (uint)(leftStart + i),
                        (uint)(rightStart + i),
                        visited,
                        leftStart,
                        rightStart,
                        length
                    )
                )
                    return false;
            }
        }

        return leftType.IsFinal == rightType.IsFinal
            && SuperTypesEquivalent(
                leftType.SuperTypes,
                rightType.SuperTypes,
                visited,
                mappedLeftStart,
                mappedRightStart,
                mappedLength
            )
            && CompositeEquivalent(
                leftType.CompositeType,
                rightType.CompositeType,
                visited,
                mappedLeftStart,
                mappedRightStart,
                mappedLength
            );
    }

    bool SuperTypesEquivalent(
        ImmutableArray<uint> left,
        ImmutableArray<uint> right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    )
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (
                !ConcreteEquivalent(
                    left[i],
                    right[i],
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                )
            )
                return false;
        }
        return true;
    }

    bool CompositeEquivalent(
        CompositeType left,
        CompositeType right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    ) =>
        (left.Value, right.Value) switch
        {
            (FuncType l, FuncType r) => ValueSequenceEquivalent(
                l.Parameters.AsSpan(),
                r.Parameters.AsSpan(),
                visited,
                mappedLeftStart,
                mappedRightStart,
                mappedLength
            )
                && ValueSequenceEquivalent(
                    l.Results.AsSpan(),
                    r.Results.AsSpan(),
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                ),
            (StructType l, StructType r) => FieldSequenceEquivalent(
                l.Fields.AsSpan(),
                r.Fields.AsSpan(),
                visited,
                mappedLeftStart,
                mappedRightStart,
                mappedLength
            ),
            (ArrayType l, ArrayType r) => FieldEquivalent(
                l.Field,
                r.Field,
                visited,
                mappedLeftStart,
                mappedRightStart,
                mappedLength
            ),
            _ => false,
        };

    bool FieldSequenceEquivalent(
        ReadOnlySpan<FieldType> left,
        ReadOnlySpan<FieldType> right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    )
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (
                !FieldEquivalent(
                    left[i],
                    right[i],
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                )
            )
                return false;
        }
        return true;
    }

    bool FieldEquivalent(
        FieldType left,
        FieldType right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    ) =>
        left.Mutable == right.Mutable
        && StorageEquivalent(
            left.StorageType,
            right.StorageType,
            visited,
            mappedLeftStart,
            mappedRightStart,
            mappedLength
        );

    bool StorageEquivalent(
        StorageType left,
        StorageType right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    ) =>
        (left.Value, right.Value) switch
        {
            (PackedType l, PackedType r) => l == r,
            (WasmValueType l, WasmValueType r) => ValueEquivalent(
                l,
                r,
                visited,
                mappedLeftStart,
                mappedRightStart,
                mappedLength
            ),
            _ => false,
        };

    bool ValueSequenceEquivalent(
        ReadOnlySpan<WasmValueType> left,
        ReadOnlySpan<WasmValueType> right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    )
    {
        if (left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (
                !ValueEquivalent(
                    left[i],
                    right[i],
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                )
            )
                return false;
        }
        return true;
    }

    bool ValueEquivalent(
        WasmValueType left,
        WasmValueType right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    ) =>
        (left.Value, right.Value) switch
        {
            (ConcreteType l, ConcreteType r) => l.IsNullable == r.IsNullable
                && ConcreteEquivalent(
                    l.TypeIndex,
                    r.TypeIndex,
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                ),
            (FuncType l, FuncType r) => l.IsNullable == r.IsNullable
                && ValueSequenceEquivalent(
                    l.Parameters.AsSpan(),
                    r.Parameters.AsSpan(),
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                )
                && ValueSequenceEquivalent(
                    l.Results.AsSpan(),
                    r.Results.AsSpan(),
                    visited,
                    mappedLeftStart,
                    mappedRightStart,
                    mappedLength
                ),
            _ => left.Equals(right),
        };

    bool ConcreteEquivalent(
        uint left,
        uint right,
        HashSet<(uint Left, uint Right)> visited,
        int mappedLeftStart,
        int mappedRightStart,
        int mappedLength
    )
    {
        var leftMapped = IsMapped(left, mappedLeftStart, mappedLength);
        var rightMapped = IsMapped(right, mappedRightStart, mappedLength);
        if (leftMapped || rightMapped)
        {
            return leftMapped
                && rightMapped
                && left - (uint)mappedLeftStart == right - (uint)mappedRightStart;
        }

        return IsEquivalent(left, right, visited, mappedLeftStart, mappedRightStart, mappedLength);
    }

    static bool IsMapped(uint typeIndex, int start, int length) =>
        start >= 0 && typeIndex >= (uint)start && typeIndex < (uint)(start + length);
}

public static class TypeRelations
{
    public static bool IsSubtype(
        WasmValueType actual,
        WasmValueType expected,
        TypeGraph? graph = null
    )
    {
        if (actual == expected || actual.IsBottom)
            return true;
        if (actual.Value is not RefType actualRef || expected.Value is not RefType expectedRef)
            return false;
        if (!NullableMatches(actualRef, expectedRef))
            return false;
        return HeapSubtype(actualRef, expectedRef, graph);
    }

    public static bool IsEquivalent(
        WasmValueType left,
        WasmValueType right,
        TypeGraph? graph = null
    ) => IsSubtype(left, right, graph) && IsSubtype(right, left, graph);

    public static bool IsSubtype(
        WasmValueType actual,
        WasmValueType expected,
        ReadOnlySpan<RecursiveType> types
    ) => IsSubtype(actual, expected, new TypeGraph(types));

    static bool NullableMatches(RefType actual, RefType expected) =>
        actual.IsNullable == expected.IsNullable || (!actual.IsNullable && expected.IsNullable);

    static bool HeapSubtype(RefType actual, RefType expected, TypeGraph? graph) =>
        (actual, expected) switch
        {
            (ConcreteType a, ConcreteType e) => ConcreteSubtype(a.TypeIndex, e.TypeIndex, graph),
            (ConcreteType a, FuncType e) => graph is not null
                && graph.TryGetSubType(a.TypeIndex, out var subType)
                && subType.CompositeType is FuncType f
                && FuncSubtype(f, e, graph),
            (ConcreteType a, AbstractHeapType e) => ConcreteAbstractSubtype(a.TypeIndex, e, graph),
            (NoFuncRef, ConcreteType e) => ConcreteTypeHasComposite<FuncType>(e.TypeIndex, graph),
            (NoneRef, ConcreteType e) => ConcreteTypeHasComposite<StructType>(e.TypeIndex, graph)
                || ConcreteTypeHasComposite<ArrayType>(e.TypeIndex, graph),
            (FuncType a, FuncType e) => FuncSubtype(a, e, graph),
            (FuncType, FuncRef e) when e.GetType() == typeof(FuncRef) => true,
            (NoFuncRef, FuncRef e) when e.GetType() == typeof(FuncRef) => true,
            (NoExternRef, ExternRef e) when e.GetType() == typeof(ExternRef) => true,
            (NoExnRef, ExnRef e) when e.GetType() == typeof(ExnRef) => true,
            (NoneRef, StructRef) => true,
            (NoneRef, ArrayRef) => true,
            (NoneRef, EqRef e) when e.GetType() == typeof(EqRef) => true,
            (I31Ref, EqRef e) when e.GetType() == typeof(EqRef) => true,
            (StructRef, EqRef e) when e.GetType() == typeof(EqRef) => true,
            (ArrayRef, EqRef e) when e.GetType() == typeof(EqRef) => true,
            (EqRef, AnyRef e) when e.GetType() == typeof(AnyRef) => true,
            (FuncRef a, AnyRef e)
                when a.GetType() == typeof(FuncRef) && e.GetType() == typeof(AnyRef) => true,
            (ExternRef a, AnyRef e)
                when a.GetType() == typeof(ExternRef) && e.GetType() == typeof(AnyRef) => true,
            (AbstractHeapType a, AbstractHeapType e) => a.GetType() == e.GetType(),
            _ => false,
        };

    static bool ConcreteSubtype(uint actual, uint expected, TypeGraph? graph) =>
        actual == expected
        || (
            graph is not null
            && (graph.IsDeclaredSubtype(actual, expected) || graph.IsEquivalent(actual, expected))
        );

    static bool ConcreteAbstractSubtype(uint actual, AbstractHeapType expected, TypeGraph? graph)
    {
        if (graph is null || !graph.TryGetSubType(actual, out var subType))
            return false;

        return (subType.CompositeType.Value, expected) switch
        {
            (FuncType, FuncRef e) when e.GetType() == typeof(FuncRef) => true,
            (FuncType, AnyRef e) when e.GetType() == typeof(AnyRef) => true,
            (StructType, StructRef) => true,
            (StructType, EqRef e) when e.GetType() == typeof(EqRef) => true,
            (StructType, AnyRef e) when e.GetType() == typeof(AnyRef) => true,
            (ArrayType, ArrayRef) => true,
            (ArrayType, EqRef e) when e.GetType() == typeof(EqRef) => true,
            (ArrayType, AnyRef e) when e.GetType() == typeof(AnyRef) => true,
            _ => false,
        };
    }

    static bool ConcreteTypeHasComposite<T>(uint typeIndex, TypeGraph? graph)
    {
        return graph is not null
            && graph.TryGetSubType(typeIndex, out var subType)
            && subType.CompositeType.Value is T;
    }

    public static bool FuncSubtype(FuncType actual, FuncType expected, TypeGraph? graph)
    {
        if (
            actual.Parameters.Length != expected.Parameters.Length
            || actual.Results.Length != expected.Results.Length
        )
            return false;

        for (var i = 0; i < actual.Parameters.Length; i++)
        {
            if (!IsSubtype(expected.Parameters[i], actual.Parameters[i], graph))
                return false;
        }

        for (var i = 0; i < actual.Results.Length; i++)
        {
            if (!IsSubtype(actual.Results[i], expected.Results[i], graph))
                return false;
        }

        return true;
    }
}
