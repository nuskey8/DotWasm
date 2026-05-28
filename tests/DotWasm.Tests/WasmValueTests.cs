using DotWasm.Runtime;
using TUnit.Assertions;
using TUnit.Core;

namespace DotWasm.Tests;

public sealed class WasmValueTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task FromI32StoresIntegerValue(int value)
    {
        var wasmValue = WasmValue.FromI32(value);

        await Assert.That(wasmValue.I32).IsEqualTo(value);
        await Assert.That(wasmValue.Bits).IsEqualTo(unchecked((ulong)value));
        await Assert.That(wasmValue.Reference).IsNull();
    }
}
