using System;
using System.IO;
using System.Reflection;
using BenchmarkDotNet.Attributes;

public class CollatzBenchmark
{
    byte[] loadedBytes;

    Wasmtime.Engine wasmtimeEngine;
    Wasmtime.Store wasmtimeStore;
    Wasmtime.Module wasmtimeModule;
    Wasmtime.Instance wasmtimeInstance;

    WaaS.Models.Module waasModule;
    WaaS.Runtime.Instance waasInstance;

    Wacs.Core.Runtime.WasmRuntime wacsRuntime;
    Wacs.Core.Module wacsModule;
    Wacs.Core.Runtime.Types.ModuleInstance wacsInstance;
    Action<int> wacsCollatzBench;

    DotWasm.Models.WasmModule dotWasmModule;
    DotWasm.Runtime.WasmStore dotWasmStore;
    DotWasm.Runtime.WasmLinker dotWasmLinker;
    DotWasm.Runtime.WasmInstance dotWasmInstance;

    [GlobalSetup]
    public void Setup()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = "Benchmark.collatz_bench.wasm";

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");

        using MemoryStream ms = new();
        stream.CopyTo(ms);
        loadedBytes = ms.ToArray();

        wasmtimeEngine = new Wasmtime.Engine();
        wasmtimeModule = Wasmtime.Module.FromBytes(wasmtimeEngine, "collatz_bench", loadedBytes);
        dotWasmModule = DotWasm.Encoding.WasmEncoding.Decode(loadedBytes);
        waasModule = WaaS.Models.Module.Create(loadedBytes);
        wacsModule = Wacs.Core.BinaryModuleParser.ParseWasm(new MemoryStream(loadedBytes));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        wasmtimeStore = new Wasmtime.Store(wasmtimeEngine);
        wasmtimeInstance = new Wasmtime.Instance(wasmtimeStore, wasmtimeModule);

        dotWasmStore = new DotWasm.Runtime.WasmStore();
        dotWasmLinker = new DotWasm.Runtime.WasmLinker(dotWasmStore);
        dotWasmInstance = dotWasmLinker.Instantiate(dotWasmModule);

        waasInstance = new WaaS.Runtime.Instance(waasModule, new WaaS.Runtime.Imports());

        wacsRuntime = new Wacs.Core.Runtime.WasmRuntime();
        wacsInstance = wacsRuntime.InstantiateModule(wacsModule);
        wacsRuntime.RegisterModule("collatz_bench", wacsInstance);
        var wacsFuncAddr = wacsRuntime.GetExportedFunction(("collatz_bench", "collatz_bench"));
        wacsCollatzBench = wacsRuntime.CreateInvokerAction<int>(
            wacsFuncAddr,
            new Wacs.Core.Runtime.InvokerOptions()
        );
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        wasmtimeStore.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        wasmtimeEngine.Dispose();
    }

    [Benchmark(Description = "dotWasm")]
    public void Bench_dotWasm()
    {
        dotWasmInstance.Invoke("collatz_bench", [10000], []);
    }

    [Benchmark(Description = "Wasmtime")]
    public void Bench_Wasmtime()
    {
        wasmtimeInstance.GetFunction("collatz_bench").Invoke(10000);
    }

    [Benchmark(Description = "WaaS")]
    public void Bench_WaaS()
    {
        waasInstance.TryGetExport("collatz_bench", out WaaS.Runtime.IInvocableFunction export);
        using var context = new WaaS.Runtime.ExecutionContext();
        Span<WaaS.Runtime.StackValueItem> args = [new WaaS.Runtime.StackValueItem(10000)];
        context.Invoke(export, args);
    }

    [Benchmark(Description = "WACS")]
    public void Bench_WACS()
    {
        wacsCollatzBench(10000);
    }
}
