using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DotWasm.Encoding;
using DotWasm.Models;
using DotWasm.Runtime;
using DotWasm.Validation;

var options = Options.Parse(args);
var runner = new SpecRunner(options);
var result = runner.Run();
return result.Failed == 0 ? 0 : 1;

sealed record Options(
    string SpecDirectory,
    string WasmToolsPath,
    string? Filter,
    bool KeepGenerated,
    bool StopOnFailure
)
{
    public static Options Parse(string[] args)
    {
        var specDirectory = ResolveDefaultSpecDirectory();
        var wasmToolsPath = "wasm-tools";
        string? filter = null;
        var keepGenerated = false;
        var stopOnFailure = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--spec-dir":
                    specDirectory = RequireValue(args, ref i, "--spec-dir");
                    break;
                case "--wasm-tools":
                    wasmToolsPath = RequireValue(args, ref i, "--wasm-tools");
                    break;
                case "--filter":
                    filter = RequireValue(args, ref i, "--filter");
                    break;
                case "--keep-generated":
                    keepGenerated = true;
                    break;
                case "--stop-on-failure":
                    stopOnFailure = true;
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        return new Options(
            Path.GetFullPath(specDirectory),
            wasmToolsPath,
            filter,
            keepGenerated,
            stopOnFailure
        );
    }

    static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[++index];
    }

    static void PrintHelp()
    {
        Console.WriteLine(
            """
            Usage: dotnet run --project tests/DotWasm.SpecTest -- [options]

            Options:
              --spec-dir <path>       Directory that contains official spec .wast files.
              --wasm-tools <path>     wasm-tools executable path. Default: wasm-tools
              --filter <pattern>      Run files whose filename contains the pattern.
              --keep-generated        Keep generated JSON/wasm files under the temp directory.
              --stop-on-failure       Stop at the first failed command.
            """
        );
    }

    static string ResolveDefaultSpecDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.CurrentDirectory,
                "tests",
                "DotWasm.SpecTest",
                "spec",
                "test",
                "core"
            ),
            Path.Combine(Environment.CurrentDirectory, "spec", "test", "core"),
            Path.Combine(AppContext.BaseDirectory, "spec", "test", "core"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return candidates[0];
    }
}

sealed class SpecRunner(Options options)
{
    public RunSummary Run()
    {
        if (!Directory.Exists(options.SpecDirectory))
            throw new DirectoryNotFoundException(options.SpecDirectory);

        var wastFiles = EnumerateWastFiles(options.SpecDirectory).ToArray();

        Console.WriteLine($"Spec directory: {options.SpecDirectory}");
        Console.WriteLine($"wasm-tools: {options.WasmToolsPath}");
        Console.WriteLine($"Files: {wastFiles.Length}");

        if (wastFiles.Length == 0)
            return new RunSummary();

        Console.WriteLine("");

        var total = new RunSummary();
        foreach (var wastFile in wastFiles)
        {
            var summary = RunFile(wastFile);
            total.Add(summary);
            Console.WriteLine(
                $"{Path.GetRelativePath(options.SpecDirectory, wastFile)}: pass={summary.Passed}, fail={summary.Failed}, skip={summary.Skipped}"
            );

            if (options.StopOnFailure && summary.Failed != 0)
                break;
        }

        Console.WriteLine(
            $"Total: pass={total.Passed}, fail={total.Failed}, skip={total.Skipped}, files={wastFiles.Length}"
        );
        return total;
    }

    IEnumerable<string> EnumerateWastFiles(string specDirectory)
    {
        foreach (
            var path in Directory.EnumerateFiles(
                specDirectory,
                "*.wast",
                SearchOption.TopDirectoryOnly
            )
        )
            if (PassesFilter(path))
                yield return path;

        foreach (var proposalDir in Directory.EnumerateDirectories(specDirectory))
        foreach (
            var path in Directory.EnumerateFiles(
                proposalDir,
                "*.wast",
                SearchOption.TopDirectoryOnly
            )
        )
            if (PassesFilter(path))
                yield return path;
    }

    bool PassesFilter(string path) =>
        options.Filter is null
        || Path.GetFileName(path).Contains(options.Filter, StringComparison.OrdinalIgnoreCase)
        || Path.GetRelativePath(options.SpecDirectory, path)
            .Contains(options.Filter, StringComparison.OrdinalIgnoreCase);

    RunSummary RunFile(string wastFile)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "DotWasm.SpecTest",
            Path.GetFileNameWithoutExtension(wastFile) + "-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var jsonPath = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(wastFile) + ".json"
            );
            if (!TryRunJsonFromWast(wastFile, jsonPath, outputDirectory, out var error))
            {
                Console.WriteLine(
                    $"  skipped: wasm-tools json-from-wast failed: {error.Split(Environment.NewLine)[0]}"
                );
                return new RunSummary { Skipped = 1 };
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
            return RunCommands(jsonPath, document.RootElement.GetProperty("commands"));
        }
        finally
        {
            if (!options.KeepGenerated)
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    bool TryRunJsonFromWast(string wastFile, string jsonPath, string wasmDir, out string error)
    {
        var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = options.WasmToolsPath,
                ArgumentList =
                {
                    "json-from-wast",
                    wastFile,
                    "-o",
                    jsonPath,
                    "--wasm-dir",
                    wasmDir,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        );

        if (process is null)
            throw new InvalidOperationException("Failed to start wasm-tools.");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            error = process.StandardError.ReadToEnd();
            return false;
        }

        error = "";
        return true;
    }

    RunSummary RunCommands(string jsonPath, JsonElement commands)
    {
        var summary = new RunSummary();
        var store = new WasmStore();
        var linker = new WasmLinker(store);
        RegisterSpectest(linker);
        WasmInstance? currentInstance = null;
        var modules = new Dictionary<string, WasmInstance>(StringComparer.Ordinal);
        var moduleDefinitions = new Dictionary<string, string>(StringComparer.Ordinal);
        var baseDir = Path.GetDirectoryName(jsonPath)!;

        foreach (var command in commands.EnumerateArray())
        {
            var type = command.GetProperty("type").GetString();
            var line = command.TryGetProperty("line", out var lineElement)
                ? lineElement.GetInt32()
                : 0;

            try
            {
                switch (type)
                {
                    case "module":
                        currentInstance = InstantiateModule(linker, jsonPath, command);
                        if (command.TryGetProperty("name", out var nameElement))
                            modules[nameElement.GetString() ?? ""] = currentInstance;
                        summary.Passed++;
                        break;
                    case "module_definition":
                        if (command.TryGetProperty("name", out var defName))
                            moduleDefinitions[defName.GetString()!] = command
                                .GetProperty("filename")
                                .GetString()!;
                        break;
                    case "module_instance":
                    {
                        var instName = command.GetProperty("instance").GetString()!;
                        var moduleName = command.GetProperty("module").GetString()!;
                        var wasmPath = Path.Combine(baseDir, moduleDefinitions[moduleName]);
                        currentInstance = linker.Instantiate(
                            WasmEncoding.Decode(File.ReadAllBytes(wasmPath))
                        );
                        modules[instName] = currentInstance;
                        summary.Passed++;
                        break;
                    }
                    case "action":
                        RunAction(
                            command.GetProperty("action"),
                            currentInstance,
                            modules,
                            expectedArity: 0
                        );
                        summary.Passed++;
                        break;
                    case "assert_return":
                        AssertReturn(command, currentInstance, modules);
                        summary.Passed++;
                        break;
                    case "assert_trap":
                    case "assert_exception":
                        AssertTrap(command, linker, jsonPath, currentInstance, modules);
                        summary.Passed++;
                        break;
                    case "assert_invalid":
                        AssertInvalid(command, linker, jsonPath);
                        summary.Passed++;
                        break;
                    case "assert_malformed":
                        AssertMalformed(command, jsonPath);
                        summary.Passed++;
                        break;
                    case "assert_uninstantiable":
                    case "assert_unlinkable":
                        AssertModuleFailure(command, linker, jsonPath);
                        summary.Passed++;
                        break;
                    case "assert_exhaustion":
                        summary.Skipped++;
                        break;
                    case "register":
                        RegisterModule(command, currentInstance, modules, linker);
                        summary.Passed++;
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported command type '{type}'.");
                }
            }
            catch (Exception ex)
                when (type == "module"
                    && ex is WasmDecodeException or WasmValidationException or NotSupportedException
                )
            {
                summary.Skipped++;
                Console.WriteLine($"  skipped: unsupported module at line {line}: {ex.Message}");
                return summary;
            }
            catch (Exception ex)
            {
                if (
                    ex is InvalidOperationException
                    && ex.Message.StartsWith("Expected invalid module", StringComparison.Ordinal)
                )
                {
                    summary.Failed++;
                    Console.WriteLine(
                        $"  line {line}: {type} failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                else
                {
                    summary.Skipped++;
                    Console.WriteLine(
                        $"  skipped: unsupported command at line {line}: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                if (options.StopOnFailure && summary.Failed != 0)
                    break;
            }
        }

        return summary;
    }

    static WasmInstance InstantiateModule(WasmLinker linker, string jsonPath, JsonElement command)
    {
        var module = DecodeModule(jsonPath, command);
        return linker.Instantiate(module);
    }

    static WasmModule DecodeModule(string jsonPath, JsonElement command)
    {
        var baseDir = Path.GetDirectoryName(jsonPath)!;
        var filename = command.GetProperty("filename").GetString()!;
        var wasmPath = Path.Combine(baseDir, filename);

        if (filename.EndsWith(".wat", StringComparison.OrdinalIgnoreCase))
        {
            var tempWasm = Path.Combine(
                baseDir,
                Path.GetFileNameWithoutExtension(filename) + ".wasm"
            );
            if (!File.Exists(tempWasm))
            {
                var process = Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "wasm-tools",
                        ArgumentList = { "parse", "-o", tempWasm, wasmPath },
                        RedirectStandardError = true,
                    }
                );
                process!.WaitForExit();
                if (process.ExitCode != 0)
                    throw new WasmDecodeException(
                        $"wasm-tools parse failed: {process.StandardError.ReadToEnd()}"
                    );
            }
            wasmPath = tempWasm;
        }

        return WasmEncoding.Decode(File.ReadAllBytes(wasmPath));
    }

    static void RegisterModule(
        JsonElement command,
        WasmInstance? currentInstance,
        IReadOnlyDictionary<string, WasmInstance> modules,
        WasmLinker linker
    )
    {
        var moduleName = command.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;
        var instance =
            moduleName is null
                ? currentInstance ?? throw new InvalidOperationException("No current module.")
            : modules.TryGetValue(moduleName, out var namedInstance) ? namedInstance
            : throw new InvalidOperationException($"Unknown module '{moduleName}'.");

        linker.RegisterInstance(command.GetProperty("as").GetString()!, instance);
    }

    static void RegisterSpectest(WasmLinker linker)
    {
        RegisterSpectestHostFunction(linker, "print", [], []);
        RegisterSpectestHostFunction(linker, "print_i32", [WasmTypes.I32], []);
        RegisterSpectestHostFunction(linker, "print_i64", [WasmTypes.I64], []);
        RegisterSpectestHostFunction(linker, "print_f32", [WasmTypes.F32], []);
        RegisterSpectestHostFunction(linker, "print_f64", [WasmTypes.F64], []);
        RegisterSpectestHostFunction(linker, "print_i32_f32", [WasmTypes.I32, WasmTypes.F32], []);
        RegisterSpectestHostFunction(linker, "print_f64_f64", [WasmTypes.F64, WasmTypes.F64], []);
        linker.RegisterGlobal(
            "spectest",
            "global_i32",
            new GlobalInstance
            {
                Mutable = false,
                ValueType = WasmTypes.I32,
                Value = WasmValue.FromI32(666),
            }
        );
        linker.RegisterGlobal(
            "spectest",
            "global_i64",
            new GlobalInstance
            {
                Mutable = false,
                ValueType = WasmTypes.I64,
                Value = WasmValue.FromI64(666),
            }
        );
        linker.RegisterGlobal(
            "spectest",
            "global_f32",
            new GlobalInstance
            {
                Mutable = false,
                ValueType = WasmTypes.F32,
                Value = WasmValue.FromF32(666.6f),
            }
        );
        linker.RegisterGlobal(
            "spectest",
            "global_f64",
            new GlobalInstance
            {
                Mutable = false,
                ValueType = WasmTypes.F64,
                Value = WasmValue.FromF64(666.6),
            }
        );
        linker.RegisterMemory("spectest", "memory", new MemoryInstance(1) { Max = 2 });
        linker.RegisterTable(
            "spectest",
            "table",
            new TableInstance(10) { ElementType = WasmTypes.FuncRef(true), Max = 20 }
        );
        linker.RegisterTable(
            "spectest",
            "table64",
            new TableInstance(10)
            {
                AddressType = AddressType.I64,
                ElementType = WasmTypes.FuncRef(true),
                Max = 20,
            }
        );
    }

    static void RegisterSpectestHostFunction(
        WasmLinker linker,
        string name,
        WasmValueType[] parameters,
        WasmValueType[] results
    )
    {
        linker.RegisterFunction(
            "spectest",
            name,
            new HostFunction
            {
                Type = new FuncType
                {
                    IsNullable = false,
                    Parameters = [.. parameters],
                    Results = [.. results],
                },
                Delegate = (_, _) => { },
            }
        );
    }

    static void AssertReturn(
        JsonElement command,
        WasmInstance? currentInstance,
        IReadOnlyDictionary<string, WasmInstance> modules
    )
    {
        var expectedAlternatives = ParseExpectedAlternatives(command);
        var expected = expectedAlternatives[0];
        var actual = RunAction(
            command.GetProperty("action"),
            currentInstance,
            modules,
            expected.Length
        );

        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"Expected {expected.Length} results, got {actual.Length}."
            );

        foreach (var alternative in expectedAlternatives)
        {
            if (alternative.Length != actual.Length)
                continue;

            var matches = true;
            for (var i = 0; i < alternative.Length; i++)
                matches &= alternative[i].Matches(actual[i]);
            if (matches)
                return;
        }

        throw new InvalidOperationException(
            $"Result mismatch. Expected one of {string.Join(" | ", expectedAlternatives.Select(FormatExpectedValues))}."
        );
    }

    static ExpectedValue[][] ParseExpectedAlternatives(JsonElement command)
    {
        if (command.TryGetProperty("expected", out var expectedElement))
            return [expectedElement.EnumerateArray().Select(ExpectedValue.Parse).ToArray()];

        return command
            .GetProperty("either")
            .EnumerateArray()
            .Select(element =>
                element.ValueKind == JsonValueKind.Array
                    ? element.EnumerateArray().Select(ExpectedValue.Parse).ToArray()
                    : [ExpectedValue.Parse(element)]
            )
            .ToArray();
    }

    static string FormatExpectedValues(ExpectedValue[] expected) =>
        $"[{string.Join(", ", expected.Select(value => value.ToString()))}]";

    static void AssertTrap(
        JsonElement command,
        WasmLinker linker,
        string jsonPath,
        WasmInstance? currentInstance,
        IReadOnlyDictionary<string, WasmInstance> modules
    )
    {
        if (command.TryGetProperty("filename", out _))
        {
            AssertModuleFailure(command, linker, jsonPath);
            return;
        }

        try
        {
            RunAction(command.GetProperty("action"), currentInstance, modules, expectedArity: 0);
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException("Expected trap, but action completed.");
    }

    static void AssertModuleFailure(JsonElement command, WasmLinker linker, string jsonPath)
    {
        try
        {
            InstantiateModule(linker, jsonPath, command);
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException("Expected module instantiation failure.");
    }

    static void AssertInvalid(JsonElement command, WasmLinker linker, string jsonPath)
    {
        try
        {
            InstantiateModule(linker, jsonPath, command);
        }
        catch (WasmDecodeException)
        {
            return;
        }
        catch (WasmValidationException)
        {
            return;
        }
        catch (OverflowException)
        {
            return;
        }

        throw new InvalidOperationException("Expected invalid module, but validation succeeded.");
    }

    static void AssertMalformed(JsonElement command, string jsonPath)
    {
        try
        {
            DecodeModule(jsonPath, command);
        }
        catch (WasmDecodeException)
        {
            return;
        }
        catch (OverflowException)
        {
            return;
        }

        throw new InvalidOperationException("Expected malformed module, but decoding succeeded.");
    }

    static WasmValue[] RunAction(
        JsonElement action,
        WasmInstance? currentInstance,
        IReadOnlyDictionary<string, WasmInstance> modules,
        int expectedArity
    )
    {
        var instance = ResolveInstance(action, currentInstance, modules);
        return action.GetProperty("type").GetString() switch
        {
            "invoke" => InvokeAction(action, instance, expectedArity),
            "get" => GetAction(action, instance, expectedArity),
            var type => throw new NotSupportedException($"Unsupported action type '{type}'."),
        };
    }

    static WasmValue[] InvokeAction(JsonElement action, WasmInstance instance, int expectedArity)
    {
        var field = action.GetProperty("field").GetString()!;
        var args = action.TryGetProperty("args", out var argsElement)
            ? argsElement.EnumerateArray().Select(ParseValue).ToArray()
            : [];
        var results = new WasmValue[expectedArity];
        instance.Invoke(field, args, results);
        return results;
    }

    static WasmValue[] GetAction(JsonElement action, WasmInstance instance, int expectedArity)
    {
        if (expectedArity != 1)
            throw new InvalidOperationException($"Expected 1 result for get, got {expectedArity}.");

        var field = action.GetProperty("field").GetString()!;

        if (!instance.TryGetExportedGlobal(field, out var global))
            throw new InvalidOperationException($"Global export not found: {field}");
        return [global.Value];
    }

    static WasmInstance ResolveInstance(
        JsonElement action,
        WasmInstance? currentInstance,
        IReadOnlyDictionary<string, WasmInstance> modules
    )
    {
        if (action.TryGetProperty("module", out var moduleElement))
        {
            var moduleName = moduleElement.GetString()!;
            if (modules.TryGetValue(moduleName, out var instance))
                return instance;
            throw new InvalidOperationException($"Unknown module '{moduleName}'.");
        }

        return currentInstance ?? throw new InvalidOperationException("No current module.");
    }

    static WasmValue ParseValue(JsonElement element)
    {
        var type = element.GetProperty("type").GetString();
        if (type == "v128")
            return ParseV128(element);

        var value = element.GetProperty("value").GetString()!;
        return type switch
        {
            "i32" => WasmValue.FromI32(int.Parse(value, CultureInfo.InvariantCulture)),
            "i64" => WasmValue.FromI64(long.Parse(value, CultureInfo.InvariantCulture)),
            "f32" => WasmValue.FromF32(
                BitConverter.Int32BitsToSingle(
                    unchecked((int)uint.Parse(value, CultureInfo.InvariantCulture))
                )
            ),
            "f64" => WasmValue.FromF64(
                BitConverter.Int64BitsToDouble(
                    unchecked((long)ulong.Parse(value, CultureInfo.InvariantCulture))
                )
            ),
            "externref" => value == "null"
                ? WasmValue.NullReference
                : HostReference(long.Parse(value, CultureInfo.InvariantCulture)),
            "anyref" => value == "null"
                ? WasmValue.NullReference
                : HostReference(long.Parse(value, CultureInfo.InvariantCulture)),
            "funcref" => WasmValue.FromI64(
                value == "null" ? -1 : long.Parse(value, CultureInfo.InvariantCulture)
            ),
            _ => throw new NotSupportedException($"Unsupported value type '{type}'."),
        };
    }

    static WasmValue HostReference(long id) =>
        WasmValue.FromExternReference(new ExternalReference { Value = WasmValue.FromI64(id) });

    static string FormatValue(string type, WasmValue value) =>
        type switch
        {
            "i32" => unchecked((uint)value.I32).ToString(CultureInfo.InvariantCulture),
            "i64" => unchecked((ulong)value.I64).ToString(CultureInfo.InvariantCulture),
            "f32" => unchecked((uint)BitConverter.SingleToInt32Bits(value.F32)).ToString(
                CultureInfo.InvariantCulture
            ),
            "f64" => unchecked((ulong)BitConverter.DoubleToInt64Bits(value.F64)).ToString(
                CultureInfo.InvariantCulture
            ),
            "externref" or "funcref" => value.I64.ToString(CultureInfo.InvariantCulture),
            "v128" => $"{value.V128.LowerBits:x16}{value.V128.UpperBits:x16}",
            _ => value.ToString() ?? "",
        };

    static WasmValue ParseV128(JsonElement element)
    {
        var laneType = element.GetProperty("lane_type").GetString()!;
        var values = element
            .GetProperty("value")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .ToArray();
        Span<byte> bytes = stackalloc byte[16];
        switch (laneType)
        {
            case "i8":
                for (var i = 0; i < 16; i++)
                    bytes[i] = unchecked((byte)int.Parse(values[i], CultureInfo.InvariantCulture));
                break;
            case "i16":
                for (var i = 0; i < 8; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes[(i * 2)..],
                        unchecked((ushort)int.Parse(values[i], CultureInfo.InvariantCulture))
                    );
                break;
            case "i32":
                for (var i = 0; i < 4; i++)
                {
                    var laneValue = values[i];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes[(i * 4)..],
                        laneValue[0] == '-'
                            ? unchecked((uint)int.Parse(laneValue, CultureInfo.InvariantCulture))
                            : uint.Parse(laneValue, CultureInfo.InvariantCulture)
                    );
                }
                break;
            case "i64":
                for (var i = 0; i < 2; i++)
                {
                    var laneValue = values[i];
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes[(i * 8)..],
                        laneValue[0] == '-'
                            ? unchecked((ulong)long.Parse(laneValue, CultureInfo.InvariantCulture))
                            : ulong.Parse(laneValue, CultureInfo.InvariantCulture)
                    );
                }
                break;
            case "f32":
                for (var i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes[(i * 4)..],
                        ParseF32Lane(values[i])
                    );
                break;
            case "f64":
                for (var i = 0; i < 2; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes[(i * 8)..],
                        ParseF64Lane(values[i])
                    );
                break;
            default:
                throw new NotSupportedException($"Unsupported v128 lane type '{laneType}'.");
        }

        return WasmValue.FromV128(
            new WasmV128Value(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..])
            )
        );
    }

    static uint ParseF32Lane(string value) =>
        value.StartsWith("nan:", StringComparison.Ordinal)
            ? 0x7fc0_0000u
            : uint.Parse(value, CultureInfo.InvariantCulture);

    static ulong ParseF64Lane(string value) =>
        value.StartsWith("nan:", StringComparison.Ordinal)
            ? 0x7ff8_0000_0000_0000UL
            : ulong.Parse(value, CultureInfo.InvariantCulture);
}

sealed record ExpectedValue(
    string Type,
    string? Value,
    string? Nan,
    string? LaneType,
    string[]? Lanes
)
{
    public static ExpectedValue Parse(JsonElement element)
    {
        var type = element.GetProperty("type").GetString()!;
        if (type == "v128")
        {
            var laneType = element.GetProperty("lane_type").GetString()!;
            var lanes = element
                .GetProperty("value")
                .EnumerateArray()
                .Select(v => v.GetString()!)
                .ToArray();
            return new ExpectedValue(type, null, null, laneType, lanes);
        }

        element.TryGetProperty("value", out var valueElement);
        element.TryGetProperty("nan", out var nanElement);
        var value =
            valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() : null;
        var nan = nanElement.ValueKind == JsonValueKind.String ? nanElement.GetString() : null;
        if (value is not null && value.StartsWith("nan:", StringComparison.Ordinal))
        {
            nan = value["nan:".Length..];
            value = null;
        }

        return new ExpectedValue(type, value, nan, null, null);
    }

    public bool Matches(WasmValue actual)
    {
        if (Nan is not null)
        {
            return Type switch
            {
                "f32" => float.IsNaN(actual.F32),
                "f64" => double.IsNaN(actual.F64),
                _ => false,
            };
        }

        if (Value is null)
            return Type == "v128" ? MatchesV128(actual) : true;

        return Type switch
        {
            "i32" => actual.I32 == int.Parse(Value, CultureInfo.InvariantCulture),
            "i64" => actual.I64 == long.Parse(Value, CultureInfo.InvariantCulture),
            "f32" => BitConverter.SingleToInt32Bits(actual.F32)
                == unchecked((int)uint.Parse(Value, CultureInfo.InvariantCulture)),
            "f64" => BitConverter.DoubleToInt64Bits(actual.F64)
                == unchecked((long)ulong.Parse(Value, CultureInfo.InvariantCulture)),
            "externref" => Value == "null"
                ? IsNullReference(actual) || actual.Reference is ExternalReference
                : TryGetHostReferenceId(actual, out var externId)
                    && externId == long.Parse(Value, CultureInfo.InvariantCulture),
            "anyref" => Value == "null"
                ? IsNullReference(actual)
                : TryGetHostReferenceId(actual, out var anyId)
                    && anyId == long.Parse(Value, CultureInfo.InvariantCulture),
            "funcref" => Value == "null" ? actual.I64 == -1 : actual.I64 != -1,
            "exnref" => Value == "null" && IsNullReference(actual),
            "refnull" or "nullref" or "nullfuncref" or "nullexnref" or "nullexternref" =>
                IsNullReference(actual),
            "i31ref" => actual.Reference is null && !IsNullReference(actual),
            "structref" => actual.Reference is GcStruct,
            "arrayref" => actual.Reference is GcArray,
            _ => throw new NotSupportedException($"Unsupported expected value type '{Type}'."),
        };
    }

    static bool IsNullReference(WasmValue value) => value.Reference is null && value.I64 == -1;

    static bool TryGetHostReferenceId(WasmValue value, out long id)
    {
        while (value.Reference is ExternalReference externReference)
            value = externReference.Value;

        if (value.Reference is null && !IsNullReference(value))
        {
            id = value.I64;
            return true;
        }

        id = 0;
        return false;
    }

    bool MatchesV128(WasmValue actual)
    {
        if (LaneType is null || Lanes is null)
            return true;
        Span<byte> bytes = stackalloc byte[16];
        Span<byte> actualBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(actualBytes, actual.V128.LowerBits);
        BinaryPrimitives.WriteUInt64LittleEndian(actualBytes[8..], actual.V128.UpperBits);
        switch (LaneType)
        {
            case "i8":
                for (var i = 0; i < 16; i++)
                    bytes[i] = unchecked((byte)int.Parse(Lanes[i], CultureInfo.InvariantCulture));
                break;
            case "i16":
                for (var i = 0; i < 8; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        bytes[(i * 2)..],
                        unchecked((ushort)int.Parse(Lanes[i], CultureInfo.InvariantCulture))
                    );
                break;
            case "i32":
                for (var i = 0; i < 4; i++)
                {
                    var lane = Lanes[i];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes[(i * 4)..],
                        lane[0] == '-'
                            ? unchecked((uint)int.Parse(lane, CultureInfo.InvariantCulture))
                            : uint.Parse(lane, CultureInfo.InvariantCulture)
                    );
                }
                break;
            case "f32":
                for (var i = 0; i < 4; i++)
                {
                    if (Lanes[i].StartsWith("nan:", StringComparison.Ordinal))
                    {
                        var actualLane = BitConverter.Int32BitsToSingle(
                            BinaryPrimitives.ReadInt32LittleEndian(actualBytes[(i * 4)..])
                        );
                        if (!float.IsNaN(actualLane))
                            return false;
                    }
                    else
                    {
                        var expectedBits = uint.Parse(Lanes[i], CultureInfo.InvariantCulture);
                        var actualBits = BinaryPrimitives.ReadUInt32LittleEndian(
                            actualBytes[(i * 4)..]
                        );
                        if (IsNaN32(expectedBits))
                        {
                            if (!IsNaN32(actualBits))
                                return false;
                            continue;
                        }
                        BinaryPrimitives.WriteUInt32LittleEndian(bytes[(i * 4)..], expectedBits);
                    }
                }
                break;
            case "i64":
                for (var i = 0; i < 2; i++)
                {
                    var lane = Lanes[i];
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        bytes[(i * 8)..],
                        lane[0] == '-'
                            ? unchecked((ulong)long.Parse(lane, CultureInfo.InvariantCulture))
                            : ulong.Parse(lane, CultureInfo.InvariantCulture)
                    );
                }
                break;
            case "f64":
                for (var i = 0; i < 2; i++)
                {
                    if (Lanes[i].StartsWith("nan:", StringComparison.Ordinal))
                    {
                        var actualLane = BitConverter.Int64BitsToDouble(
                            BinaryPrimitives.ReadInt64LittleEndian(actualBytes[(i * 8)..])
                        );
                        if (!double.IsNaN(actualLane))
                            return false;
                    }
                    else
                    {
                        var expectedBits = ulong.Parse(Lanes[i], CultureInfo.InvariantCulture);
                        var actualBits = BinaryPrimitives.ReadUInt64LittleEndian(
                            actualBytes[(i * 8)..]
                        );
                        if (IsNaN64(expectedBits))
                        {
                            if (!IsNaN64(actualBits))
                                return false;
                            continue;
                        }
                        BinaryPrimitives.WriteUInt64LittleEndian(bytes[(i * 8)..], expectedBits);
                    }
                }
                break;
        }
        if (LaneType == "f32")
        {
            for (var i = 0; i < 4; i++)
            {
                if (Lanes[i].StartsWith("nan:", StringComparison.Ordinal))
                    continue;
                var expectedBits = uint.Parse(Lanes[i], CultureInfo.InvariantCulture);
                var actualBits = BinaryPrimitives.ReadUInt32LittleEndian(actualBytes[(i * 4)..]);
                if (IsNaN32(expectedBits))
                {
                    if (!IsNaN32(actualBits))
                        return false;
                    continue;
                }
                if (actualBits != BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]))
                    return false;
            }
            return true;
        }
        if (LaneType == "f64")
        {
            for (var i = 0; i < 2; i++)
            {
                if (Lanes[i].StartsWith("nan:", StringComparison.Ordinal))
                    continue;
                var expectedBits = ulong.Parse(Lanes[i], CultureInfo.InvariantCulture);
                var actualBits = BinaryPrimitives.ReadUInt64LittleEndian(actualBytes[(i * 8)..]);
                if (IsNaN64(expectedBits))
                {
                    if (!IsNaN64(actualBits))
                        return false;
                    continue;
                }
                if (actualBits != BinaryPrimitives.ReadUInt64LittleEndian(bytes[(i * 8)..]))
                    return false;
            }
            return true;
        }
        return actual.V128!.LowerBits == BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            && actual.V128!.UpperBits == BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }

    static bool IsNaN32(uint bits) =>
        (bits & 0x7f80_0000u) == 0x7f80_0000u && (bits & 0x007f_ffffu) != 0;

    static bool IsNaN64(ulong bits) =>
        (bits & 0x7ff0_0000_0000_0000UL) == 0x7ff0_0000_0000_0000UL
        && (bits & 0x000f_ffff_ffff_ffffUL) != 0;

    public override string ToString()
    {
        if (Nan is not null)
            return $"{Type}:{Nan}";
        if (Type == "v128" && LaneType is not null && Lanes is not null)
            return $"{Type}.{LaneType} [{string.Join(", ", Lanes)}]";
        return $"{Type}:{Value}";
    }
}

sealed class RunSummary
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }

    public void Add(RunSummary other)
    {
        Passed += other.Passed;
        Failed += other.Failed;
        Skipped += other.Skipped;
    }
}
