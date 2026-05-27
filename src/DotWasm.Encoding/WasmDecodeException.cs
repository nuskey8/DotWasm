using System.Diagnostics.CodeAnalysis;

namespace DotWasm.Encoding;

public sealed class WasmDecodeException(string message) : Exception(message)
{
    [DoesNotReturn]
    internal static void Throw(string message)
    {
        throw new WasmDecodeException(message);
    }
}
