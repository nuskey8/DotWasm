namespace DotWasm.Runtime;

public sealed class WasmInstantiationException(string message) : Exception(message);
