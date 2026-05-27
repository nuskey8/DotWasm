# DotWasm
A WebAssembly runtime implemented in C# for .NET

> [!CAUTION]
> DotWasm is currently in alpha. It is not suitable for production use and may undergo breaking changes without notice.

## Overview

DotWasm is an experimental WebAssembly runtime written in C#. It is implemented in 100% pure C# and supports nearly all WebAssembly 3.0 proposals, including Wasm GC.

## Features

- Implemented entirely in pure C#
- Implements WebAssembly 3.0–equivalent proposals (excluding Threads)
- Nearly 100% passing the official test suite
- No dynamic code generation; compatible with NativeAOT

## Requirements

DotWasm requires .NET 10.0 or above.

### .NET CLI

```bash
dotnet add DotWasm
```

## Package Structure

| Package            | NuGet                                                                                                            | Description                                           |
| ------------------ | ---------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------- |
| DotWasm            | [![NuGet](https://img.shields.io/nuget/v/DotWasm)](https://www.nuget.org/packages/DotWasm)                       | Bundle containing all packages                        |
| DotWasm.Models     | [![NuGet](https://img.shields.io/nuget/v/DotWasm.Models)](https://www.nuget.org/packages/DotWasm.Models)         | C# models for Wasm binaries                           |
| DotWasm.Encoding   | [![NuGet](https://img.shields.io/nuget/v/DotWasm.Encoding)](https://www.nuget.org/packages/DotWasm.Encoding)     | Package providing Wasm binary decoding functionality  |
| DotWasm.Validation | [![NuGet](https://img.shields.io/nuget/v/DotWasm.Validation)](https://www.nuget.org/packages/DotWasm.Validation) | Package providing Wasm binary validation              |
| DotWasm.Runtime    | [![NuGet](https://img.shields.io/nuget/v/DotWasm.Runtime)](https://www.nuget.org/packages/DotWasm.Runtime)       | Package providing Wasm binary execution functionality |

## Quick Start

```wasm
;; example.wat
(module
  (func $add (param $x i32) (param $y i32) (result i32)
    local.get $x
    local.get $y
    i32.add
  )
  (export "add" (func $add))
)
```

```cs
using DotWasm.Encoding;
using DotWasm.Runtime;

var bytes = File.ReadAllBytes("example.wasm");
var module = WasmEncoding.Decode(bytes);

var store = new WasmStore();
var linker = new WasmLinker(store);
var instance = linker.Instantiate(module);

Span<WasmValue> results = new WasmValue[1];
instance.Invoke("add", [1, 2], results);

int result = results[0].I32;
Console.WriteLine(result); // 3
```

## Retrieving Exports

You can obtain exported globals, memories, etc. from the Wasm side using the `instance.TryGetExported**()` family of methods.

```wasm
;; example.wat
(module
  (global $g (export "my_global") (mut i32) (i32.const 42))
)
```

```cs
var instance = linker.Instantiate(module);

if (instance.TryGetExportedGlobal("my_global", out var global))
{
    Console.WriteLine(global.Value.I32);
    global.Value = 100;
}
```

## Integrating with Host Environment

Through the `WasmLinker` API you can provide host functions, memories, and other imports to the Wasm side.

```cs
var addFunc = new HostFunction
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
};
linker.RegisterFunction("env", "hostAdd", addFunc);

var memory = new MemoryInstance(1);
source.CopyTo(memory.Data);
linker.RegisterMemory("env", "hostMemory", memory);

var global = new GlobalInstance
{
    Mutable = true,
    ValueType = WasmTypes.I32,
    Value = 43,
};
linker.RegisterMemory("env", "hostGlobal", global);
```

## ExternalReference

You can pass opaque host references to the Wasm side as `ExternalReference`.

```cs
var gameObject = new GameObject("obj");

var global = new GlobalInstance
{
    Mutable = true,
    ValueType = WasmTypes.ExternRef(isNullable: false),
    Value = ExternalReference.Create(gameObject),
};

linker.RegisterMemory("env", "object", global);
```

## Proposals

DotWasm currently supports the following proposals:

| Proposal                              | Status | Note |
| ------------------------------------- | ------ | ---- |
| WebAssembly 1.0 Core Spec             | ✅      |      |
| Mutable Globals                       | ✅      |      |
| Sign-extension operators              | ✅      |      |
| Non-trapping Float-to-int Conversions | ✅      |      |
| Multi-value                           | ✅      |      |
| Bulk Memory Operations                | ✅      |      |
| Reference Types                       | ✅      |      |
| SIMD                                  | ✅      |      |
| Component Model                       | ❌      |      |
| Relaxed SIMD                          | ✅      |      |
| Multi Memory                          | ✅      |      |
| Tail Call                             | ✅      |      |
| Extended Constant Expressions         | ✅      |      |
| Memory64                              | ✅      |      |
| Exception Handling                    | ✅      |      |
| Typed Function References             | ✅      |      |
| GC                                    | ✅      |      |
| Threads                               | ❌      |      |

## Performance

DotWasm is designed to minimize dynamic GC allocations, but it is not currently heavily optimized for execution speed. Because DotWasm avoids dynamic code generation to support AOT, its performance lags significantly behind JIT-based runtimes like Wasmtime.

Below are benchmark results for converting a 128x128 image to grayscale.

| Method   |         Mean |      Error |     StdDev |
| -------- | -----------: | ---------: | ---------: |
| Wasmtime |     20.79 us |   0.520 us |   1.493 us |
| WaaS     |  4,713.03 us |  94.249 us | 141.067 us |
| DotWasm  | 11,470.14 us | 217.310 us | 213.428 us |
| WACS     | 14,586.17 us | 285.006 us | 279.914 us |

The comparison used the following libraries:

- [wasmtime-dotnet](https://github.com/bytecodealliance/wasmtime-dotnet)
- [WaaS](https://github.com/ruccho/WaaS)
- [WACS](https://github.com/kelnishi/WACS)

As shown above, DotWasm is considerably slower. This is a known issue and is planned to be improved before a stable release.

## License

[MIT License](LICENSE)
