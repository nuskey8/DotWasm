using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotWasm.Runtime;

internal struct MinimumStackCore<T>
{
    const int InitialSize = 16;

    T[]? values;
    int count;

    public int Count => count;

    public MinimumStackCore(int initialCapacity)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialCapacity),
                "Initial capacity must be non-negative."
            );

        values = new T[initialCapacity];
        count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan()
    {
        return values.AsSpan(0, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in T value)
    {
        if (values == null)
        {
            values = new T[InitialSize];
        }
        else if (count == values.Length)
        {
            Array.Resize(ref values, values.Length * 2);
        }
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), count++) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushRange(ReadOnlySpan<T> items)
    {
        if (values == null)
        {
            values = new T[Math.Max(InitialSize, items.Length)];
        }
        else if (count + items.Length > values.Length)
        {
            Array.Resize(ref values, Math.Max(values.Length * 2, count + items.Length));
        }
        items.CopyTo(values.AsSpan(count));
        count += items.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Allocate(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 0);

        if (values == null)
        {
            values = new T[Math.Max(InitialSize, itemCount)];
        }
        else if (count + itemCount > values.Length)
        {
            Array.Resize(ref values, Math.Max(values.Length * 2, count + itemCount));
        }

        var start = count;
        count += itemCount;
        return values.AsSpan(start, itemCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Pop()
    {
        if (count <= 0)
            throw new InvalidOperationException($"Stack is empty");

        var buffer = values!;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), --count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T UnsafePop()
    {
        AssertNotEmpty();
        var buffer = values!;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), --count);
    }

    [Conditional("DEBUG")]
    readonly void AssertNotEmpty()
    {
        if (count <= 0)
            throw new InvalidOperationException($"Stack is empty");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Take(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, count);
        count -= n;
        return values.AsSpan(count, n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Truncate(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, count);
        count = n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T Peek()
    {
        if (count <= 0)
            throw new InvalidOperationException($"Stack is empty");

        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values!), count - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T UnsafeGet(int index)
    {
        Debug.Assert(values is not null);
        Debug.Assert((uint)index < (uint)count);
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values!), index);
    }

    public void Clear()
    {
        if (values != null && !typeof(T).IsValueType)
        {
            Array.Clear(values, 0, count);
        }
        count = 0;
    }
}
