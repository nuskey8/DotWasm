using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DotWasm.Validation;

public sealed class WasmValidationException(string message) : Exception(message)
{
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void Throw(string message)
    {
        throw new WasmValidationException(message);
    }
}
