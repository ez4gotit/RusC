using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using RusLang.Compiler;
using RusLang.Pack.Format;
using RusLang.Pack.Writer;

namespace RusLang.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        return args[0] switch
        {
            "версия" or "--версия" => Version(),
            "право" => License(),
            "здравие" => Doctor(),
            "осмотреть" when args.Length == 2 => Inspect(args[1]),
            "проверить" when args.Length == 2 => Verify(args[1]),
            "раскрыть" when args.Length >= 2 => Reveal(args[1]),
            "собрать" when args.Length >= 2 => Build(args[1], args[2..]),
            "запустить" when args.Length >= 2 => RunProgram(args[1], args[2..]),
            _ => UnknownCommand(),
        };
    }

    private static int Build(string sourcePath, string[] arguments)
    {
        if (!sourcePath.EndsWith(".rus", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sourcePath))
        {
            throw new ArgumentException(
                $"RUSC1002: Исходный файл «{sourcePath}» не найден или имеет расширение не .rus.");
        }

        var options = BuildOptions.Parse(sourcePath, arguments);
        var artifact = new RusCompiler().Compile(new CompilationRequest(
            sourcePath,
            Path.GetFileNameWithoutExtension(sourcePath),
            options.Debug,
            options.References));
        PrintDiagnostics(artifact.Diagnostics);
        if (artifact.Diagnostics.Any(static value => value.Severity == RusDiagnosticSeverity.Error))
        {
            return 1;
        }

        if (options.EmitIntermediate)
        {
            File.WriteAllText(
                Path.ChangeExtension(options.OutputPath, ".generated.cs"),
                artifact.GeneratedSource);
        }

        var assemblyName = Path.GetFileNameWithoutExtension(sourcePath) + ".dll";
        var entries = new List<RusPackInputEntry>
        {
            new(
                assemblyName,
                RusPackEntryKind.MainAssembly,
                artifact.AssemblyIdentity,
                null,
                artifact.MainAssemblyBytes),
        };
        if (artifact.PortablePdbBytes is not null && (options.Debug || options.KeepPdb))
        {
            entries.Add(new(
                Path.ChangeExtension(assemblyName, ".pdb"),
                RusPackEntryKind.PortablePdb,
                null,
                null,
                artifact.PortablePdbBytes));
        }

        foreach (var reference in options.References)
        {
            var bytes = File.ReadAllBytes(reference);
            entries.Add(new(
                Path.GetFileName(reference),
                RusPackEntryKind.ManagedAssembly,
                RusCompiler.ReadAssemblyIdentity(bytes),
                null,
                bytes));
        }

        new RusPackWriter().Write(new RusPackWriteRequest(
            options.HostTemplate,
            options.OutputPath,
            Path.GetFileNameWithoutExtension(sourcePath),
            assemblyName,
            options.Rid,
            entries,
            Compress: !options.NoCompress,
            Force: options.Force,
            CompilerVersion: GetVersion()));
        var actualOutput = NormalizeOutput(options.OutputPath, options.Rid);
        Console.WriteLine(
            $"Создано: {Path.GetFullPath(actualOutput)} ({options.Rid}, {new FileInfo(actualOutput).Length} байт)");
        return 0;
    }

    private static int Reveal(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath);
        var (generated, diagnostics) = RusSourceEmitter.Emit(source, Path.GetFullPath(sourcePath));
        PrintDiagnostics(diagnostics);
        Console.WriteLine(generated);
        return diagnostics.Any(static value => value.Severity == RusDiagnosticSeverity.Error) ? 1 : 0;
    }

    private static int RunProgram(string sourcePath, string[] arguments)
    {
        var separator = Array.IndexOf(arguments, "--");
        var buildArguments = separator < 0 ? arguments : arguments[..separator];
        var programArguments = separator < 0 ? [] : arguments[(separator + 1)..];
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "RusLang", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var output = Path.Combine(temporaryDirectory, "program");
            buildArguments = [.. buildArguments, "--выход", output, "--перезаписать"];
            var buildExitCode = Build(sourcePath, buildArguments);
            if (buildExitCode != 0)
            {
                return buildExitCode;
            }

            var executable = NormalizeOutput(output, RuntimeInformation.RuntimeIdentifier);
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                Arguments = string.Join(" ", programArguments.Select(QuoteArgument)),
            }) ?? throw new InvalidOperationException(
                "RUSC1003: Не удалось запустить собранную программу.");
            process.WaitForExit();
            return process.ExitCode;
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static int Version()
    {
        Console.WriteLine(GetVersion());
        return 0;
    }

    private static int Doctor()
    {
        Console.WriteLine($"компилятор: {GetVersion()}");
        Console.WriteLine("автор: ez4gotit");
        Console.WriteLine("истоки: https://github.com/ez4gotit/RusC");
        Console.WriteLine("лицензия: AGPL-3.0-or-later с RusLang Output Exception 1.0");
        Console.WriteLine("язык: 0.1");
        Console.WriteLine("целевая-среда: net10.0");
        Console.WriteLine($"платформа-компилятора: {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"формат-ruspack: {RusPackVersion.Current}");
        Console.WriteLine($"протокол-запуска: {RusPackConstants.RuntimeHostProtocol}");
        Console.WriteLine($"встроенные-ссылки: {ReferencePackLoader.Load().Count}");
        return 0;
    }

    private static int License()
    {
        Console.WriteLine("RusLang, Copyright (C) 2026 ez4gotit");
        Console.WriteLine("AGPL-3.0-or-later с RusLang Output Exception 1.0");
        Console.WriteLine("Исходный проект: https://github.com/ez4gotit/RusC");
        Console.WriteLine("Полные условия: https://github.com/ez4gotit/RusC/blob/master/LICENSE");
        Console.WriteLine(
            "Программы пользователей и созданные ими EXE могут распространяться под любой лицензией.");
        return 0;
    }

    private static int Inspect(string path)
    {
        using var reader = RusPackReader.OpenExecutable(path);
        var manifest = reader.Manifest;
        Console.WriteLine($"{manifest.ApplicationName} ({manifest.TargetRid}, {manifest.TargetFramework})");
        Console.WriteLine($"вход: {manifest.EntryAssembly}");
        foreach (var entry in manifest.Entries)
        {
            Console.WriteLine(
                $"{TranslateEntryKind(entry.Kind),-22} {entry.OriginalLength,10} " +
                $"{TranslateCompression(entry.Compression),-6} {entry.Name}");
        }

        return 0;
    }

    private static int Verify(string path)
    {
        using var reader = RusPackReader.OpenExecutable(path);
        reader.VerifyAllEntries();
        Console.WriteLine($"ИСПРАВЕН: {Path.GetFullPath(path)}");
        return 0;
    }

    private static void PrintDiagnostics(IEnumerable<RusDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Console.Error.WriteLine(
                $"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): " +
                $"{TranslateSeverity(diagnostic.Severity)} {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private static int UnknownCommand()
    {
        Console.Error.WriteLine("RUSC1001: Неизвестная или неполная команда.");
        PrintUsage();
        return 2;
    }

    private static string GetVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return informationalVersion?.Split('+', 2)[0]
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.1.0";
    }

    private static string TranslateSeverity(RusDiagnosticSeverity severity) =>
        severity switch
        {
            RusDiagnosticSeverity.Error => "ошибка",
            RusDiagnosticSeverity.Warning => "предупреждение",
            _ => "сведения",
        };

    private static string TranslateEntryKind(RusPackEntryKind kind) =>
        kind switch
        {
            RusPackEntryKind.MainAssembly => "главная-сборка",
            RusPackEntryKind.ManagedAssembly => "управляемая-сборка",
            RusPackEntryKind.PortablePdb => "отладочные-символы",
            RusPackEntryKind.Resource => "ресурс",
            RusPackEntryKind.SatelliteAssembly => "спутниковая-сборка",
            RusPackEntryKind.NativeLibrary => "нативная-библиотека",
            _ => kind.ToString(),
        };

    private static string TranslateCompression(RusPackCompression compression) =>
        compression == RusPackCompression.None ? "нет" : "brotli";

    private static string NormalizeOutput(string path, string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) &&
        !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? path + ".exe"
            : path;

    private static string QuoteArgument(string value) =>
        value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;

    private static void PrintUsage() =>
        Console.WriteLine(
            "Использование: rusc " +
            "<собрать|запустить|раскрыть|осмотреть|проверить|здравие|версия|право> " +
            "[аргументы]");

    private sealed record BuildOptions(
        string OutputPath,
        string Rid,
        string HostTemplate,
        bool Debug,
        bool NoCompress,
        bool KeepPdb,
        bool EmitIntermediate,
        bool Force,
        IReadOnlyList<string> References)
    {
        public static BuildOptions Parse(string sourcePath, string[] arguments)
        {
            var output = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(sourcePath))!,
                Path.GetFileNameWithoutExtension(sourcePath));
            var rid = RuntimeInformation.RuntimeIdentifier;
            string? host = Environment.GetEnvironmentVariable("RUSLANG_HOST_TEMPLATE");
            var debug = false;
            var noCompress = false;
            var keepPdb = false;
            var emitIntermediate = false;
            var force = false;
            var references = new List<string>();
            for (var index = 0; index < arguments.Length; index++)
            {
                string NextValue() => ++index < arguments.Length
                    ? arguments[index]
                    : throw new ArgumentException("RUSC1004: Не указано значение параметра.");
                switch (arguments[index])
                {
                    case "-в" or "--выход": output = NextValue(); break;
                    case "-ц" or "--цель": rid = NextValue(); break;
                    case "-р" or "--режим":
                        var configuration = NextValue();
                        debug = configuration.ToLowerInvariant() switch
                        {
                            "отладка" => true,
                            "выпуск" => false,
                            _ => throw new ArgumentException(
                                "RUSC1006: Режим должен быть «отладка» или «выпуск»."),
                        };
                        break;
                    case "--ссылка": references.Add(NextValue()); break;
                    case "--основа": host = NextValue(); break;
                    case "--без-сжатия": noCompress = true; break;
                    case "--сохранить-отладку": keepPdb = true; break;
                    case "--раскрыть-код": emitIntermediate = true; break;
                    case "--перезаписать": force = true; break;
                    case "--повторяемо" or "--подробно": break;
                    default:
                        throw new ArgumentException(
                            $"RUSC1005: Неизвестный параметр «{arguments[index]}».");
                }
            }

            host ??= Path.Combine(
                AppContext.BaseDirectory,
                "host-templates",
                rid,
                rid.StartsWith("win-", StringComparison.Ordinal)
                    ? "RusLang.RuntimeHost.exe"
                    : "RusLang.RuntimeHost");
            if (!File.Exists(host))
            {
                host = ExtractEmbeddedHost(rid);
            }

            return new(output, rid, host, debug, noCompress, keepPdb, emitIntermediate, force, references);
        }

        private static string ExtractEmbeddedHost(string rid)
        {
            var suffix = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
            var resourceName = $"RusLang.HostTemplates.{rid}{suffix}";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                throw new FileNotFoundException(
                    $"RUSC2004: Основа запуска для «{rid}» не встроена. " +
                    "Задайте RUSLANG_HOST_TEMPLATE или установите полный rusc.");
            }

            var directory = Path.Combine(Path.GetTempPath(), "RusLang", "host-templates");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"runtime-host-{GetVersion()}-{rid}{suffix}");
            if (File.Exists(path))
            {
                return path;
            }

            var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another compiler process won the atomic extraction race.
            }
            finally
            {
                File.Delete(temporaryPath);
            }

            return path;
        }
    }
}
