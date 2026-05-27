using System;
using System.IO;
using System.Reflection;
using BenchmarkDotNet.Attributes;

public class GrayscaleBenchmark
{
    const int PixelCount = 128 * 128;
    const int ImageBytes = PixelCount * 4;

    byte[] loadedBytes;
    byte[] sourceImage;

    Wasmtime.Engine wasmtimeEngine;
    Wasmtime.Store wasmtimeStore;
    Wasmtime.Module wasmtimeModule;
    Wasmtime.Instance wasmtimeInstance;
    Wasmtime.Memory wasmtimeMemory;

    WaaS.Models.Module waasModule;
    WaaS.Runtime.Instance waasInstance;

    Wacs.Core.Runtime.WasmRuntime wacsRuntime;
    Wacs.Core.Module wacsModule;
    Wacs.Core.Runtime.Types.ModuleInstance wacsInstance;
    Wacs.Core.Runtime.Types.MemoryInstance wacsMemory;
    Action<int, int> wacsGrayscaleBench;

    DotWasm.Models.WasmModule dotWasmModule;
    DotWasm.Runtime.WasmStore dotWasmStore;
    DotWasm.Runtime.WasmLinker dotWasmLinker;
    DotWasm.Runtime.WasmInstance dotWasmInstance;
    DotWasm.Runtime.MemoryInstance dotWasmMemory;

    [GlobalSetup]
    public void Setup()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Benchmark.grayscale_bench.wasm";

        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");

        using MemoryStream ms = new();
        stream.CopyTo(ms);
        loadedBytes = ms.ToArray();

        sourceImage = new byte[ImageBytes];
        var random = new Random(42);
        random.NextBytes(sourceImage);
        for (var i = 3; i < sourceImage.Length; i += 4)
            sourceImage[i] = 255;

        wasmtimeEngine = new Wasmtime.Engine();
        wasmtimeModule = Wasmtime.Module.FromBytes(wasmtimeEngine, "grayscale_bench", loadedBytes);
        dotWasmModule = DotWasm.Encoding.WasmEncoding.Decode(loadedBytes);
        waasModule = WaaS.Models.Module.Create(loadedBytes);
        wacsModule = Wacs.Core.BinaryModuleParser.ParseWasm(new MemoryStream(loadedBytes));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        wasmtimeStore = new Wasmtime.Store(wasmtimeEngine);
        wasmtimeInstance = new Wasmtime.Instance(wasmtimeStore, wasmtimeModule);
        wasmtimeMemory = wasmtimeInstance.GetMemory("memory");
        sourceImage.CopyTo(wasmtimeMemory.GetSpan(0, ImageBytes));

        dotWasmStore = new DotWasm.Runtime.WasmStore();
        dotWasmLinker = new DotWasm.Runtime.WasmLinker(dotWasmStore);
        dotWasmInstance = dotWasmLinker.Instantiate(dotWasmModule);
        if (!dotWasmInstance.TryGetExportedMemory("memory", out dotWasmMemory))
        {
            throw new InvalidOperationException("dotWasm memory export was not found.");
        }
        sourceImage.CopyTo(dotWasmMemory.Data[..ImageBytes]);

        waasInstance = new WaaS.Runtime.Instance(waasModule, new WaaS.Runtime.Imports());
        sourceImage.CopyTo(waasInstance.GetMemory(0)[..ImageBytes]);

        wacsRuntime = new Wacs.Core.Runtime.WasmRuntime();
        wacsInstance = wacsRuntime.InstantiateModule(wacsModule);
        wacsRuntime.RegisterModule("grayscale_bench", wacsInstance);
        wacsMemory = wacsRuntime.GetExportedMemory(("grayscale_bench", "memory"));
        var wacsFuncAddr = wacsRuntime.GetExportedFunction(("grayscale_bench", "grayscale_bench"));
        wacsGrayscaleBench = wacsRuntime.CreateInvokerAction<int, int>(
            wacsFuncAddr,
            new Wacs.Core.Runtime.InvokerOptions()
        );
        sourceImage.CopyTo(wacsMemory.Data[..ImageBytes]);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        wasmtimeStore.Dispose();
        waasInstance.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        wasmtimeEngine.Dispose();
    }

    [Benchmark(Description = "dotWasm")]
    public void Bench_dotWasm()
    {
        dotWasmInstance.Invoke("grayscale_bench", [0, PixelCount], []);
    }

    [Benchmark(Description = "Wasmtime")]
    public void Bench_Wasmtime()
    {
        wasmtimeInstance.GetFunction("grayscale_bench").Invoke(0, PixelCount);
    }

    [Benchmark(Description = "WaaS")]
    public void Bench_WaaS()
    {
        waasInstance.TryGetExport("grayscale_bench", out WaaS.Runtime.IInvocableFunction export);
        using var context = new WaaS.Runtime.ExecutionContext();
        Span<WaaS.Runtime.StackValueItem> args =
        [
            new WaaS.Runtime.StackValueItem(0),
            new WaaS.Runtime.StackValueItem(PixelCount),
        ];
        context.Invoke(export, args);
    }

    [Benchmark(Description = "WACS")]
    public void Bench_WACS()
    {
        wacsGrayscaleBench(0, PixelCount);
    }
}
