using DotWasm.Encoding;
using DotWasm.Models;
using DotWasm.Runtime;

var bytes = File.ReadAllBytes("test.0.wasm");
var module = WasmEncoding.Decode(bytes);

var store = new WasmStore();
var linker = new WasmLinker(store);
var instance = linker.Instantiate(module);

linker.RegisterFunction(
    "env",
    "add_i32",
    new HostFunction
    {
        Type = new FuncType
        {
            Parameters = [WasmTypes.I32, WasmTypes.I32],
            Results = [WasmTypes.I32],
        },
        Delegate = static (ReadOnlySpan<WasmValue> args, Span<WasmValue> results) =>
        {
            var a = args[0].I32;
            var b = args[1].I32;
            results[0] = WasmValue.FromI32(a + b);
        },
    }
);

Span<WasmValue> results = new WasmValue[1];
instance.Invoke("create_point", [12, 34], results);

var point = results[0].AsStruct();
Console.WriteLine(point.GetField(0).I32);
Console.WriteLine(point.GetField(1).I32);

instance.Invoke("getX", [point], results);
Console.WriteLine(results[0].I32);
