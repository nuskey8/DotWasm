using System.Diagnostics.CodeAnalysis;

namespace DotWasm.Runtime;

internal static class ThrowHelper
{
    [DoesNotReturn]
    public static T ThrowInvalidOperation<T>(string message) =>
        throw new InvalidOperationException(message);

    [DoesNotReturn]
    public static void ThrowIndexOutOfRange() => throw new IndexOutOfRangeException();
}
