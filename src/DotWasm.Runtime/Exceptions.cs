using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DotWasm.Runtime;

public abstract class WasmRuntimeException(string message) : Exception(message);

public sealed class WasmTrapException(string message) : WasmRuntimeException(message)
{
    [DoesNotReturn]
    [StackTraceHidden]
    internal static void Throw(string message)
    {
        throw new WasmTrapException(message);
    }

    [StackTraceHidden]
    internal static void ThrowIfNot(bool condition, string message)
    {
        if (!condition)
        {
            throw new WasmTrapException(message);
        }
    }
}

public sealed class WasmThrownException(TagAddress tagAddress, WasmValue[] arguments)
    : WasmRuntimeException("WebAssembly exception thrown.")
{
    public TagAddress TagAddress { get; } = tagAddress;
    public WasmValue[] Arguments { get; } = arguments;
}
