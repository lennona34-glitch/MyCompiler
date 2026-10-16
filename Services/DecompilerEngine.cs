using System.IO.Compression;
using System.Reflection;
using System.Text;
using MyCompiler.Models;

namespace MyCompiler.Services;

public enum BinaryKind { DotNet, DotNetBundle, DotNetAppHost, PyInstaller, Android, Jvm, Native, Unknown }

public sealed record DecompileResult(bool Success, string OutputDirectory, string Summary);

/// <summary>
/// Detects what kind of binary a file is and routes it to the matching decompiler:
/// ilspycmd (.NET), pyinstxtractor + pycdc (PyInstaller), jadx (APK/DEX/JAR), Ghidra headless (native PE).
/// Tools are discovered on PATH or under %LOCALAPPDATA%\MyCompiler\Decompilers; nothing is installed automatically.
/// </summary>
public sealed class DecompilerEngine
{
    public static readonly string ToolsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "Decompilers");
    // TODO: make this a user setting; hard-coded per user request for now.
    public static readonly string OutputRoot = @"C:\Users\adria\Desktop\MyCompiler\Decompiled";

    public static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".apk", ".aab", ".xapk", ".dex", ".jar", ".class", ".so", ".sys", ".bin"
    };

    // .NET single-file bundle marker (SHA-256 of ".net core bundle"), preceded by the 8-byte header offset.
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    private static readonly byte[] PyInstallerCookie = "MEI\x0C\x0B\x0A\x0B\x0E"u8.ToArray();

    private static readonly string[] FrameworkPrefixes =
    [
        "System.", "Microsoft.", "mscorlib", "netstandard", "WindowsBase", "PresentationCore", "PresentationFramework",
        "PresentationUI", "ReachFramework", "UIAutomation", "DirectWriteForwarder", "WindowsFormsIntegration",
        "Accessibility", "System", "Windows."
    ];

    private string InstallScript => Path.Combine(ToolsRoot, "Install-Decompilers.ps1");
    private string SetupCommand(string tool) => $"powershell -ExecutionPolicy Bypass -File \"{InstallScript}\" -Tool {tool}";

    // ───────────────────────── Tool discovery ─────────────────────────

    public string? IlSpy => ToolLocator.Resolve("ilspycmd",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "ilspycmd.exe"));

    public string? Jadx => ToolLocator.Resolve(Path.Combine(ToolsRoot, "jadx", "bin", "jadx.bat"), "jadx");

    public string? GhidraHeadless
    {
        get
        {
            var roots = new List<string>();
            if (Environment.GetEnvironmentVariable("GHIDRA_INSTALL_DIR") is { Length: > 0 } env) roots.Add(env);
            var local = Path.Combine(ToolsRoot, "ghidra");
            if (Directory.Exists(local))
            {
                roots.Add(local);
                roots.AddRange(Directory.GetDirectories(local).OrderDescending());
            }
            return roots.Select(root => Path.Combine(root, "support", "analyzeHeadless.bat")).FirstOrDefault(File.Exists);
        }
    }

    public string? PyInstxtractor => File.Exists(Path.Combine(ToolsRoot, "pyinstxtractor", "pyinstxtractor.py"))
        ? Path.Combine(ToolsRoot, "pyinstxtractor", "pyinstxtractor.py") : null;

    public string? Pycdc => ToolLocator.Resolve(Path.Combine(ToolsRoot, "pycdc", "pycdc.exe"), "pycdc");
    public string? Java => ToolLocator.Resolve("java");
    public string? Python => ToolLocator.Resolve("py", "python");

    public IReadOnlyList<ToolchainStatus> ScanTools()
    {
        EnsureEmbeddedFiles();
        var ilspy = IlSpy;
        var jadx = Jadx;
        var ghidra = GhidraHeadless;
        var java = Java;
        var extractor = PyInstxtractor;
        var python = Python;
        var pycdc = Pycdc;

        return
        [
            new("Decompile · .NET", "ilspycmd", ilspy is not null,
                ilspy is null ? "ILSpy command line not installed" : ".exe/.dll → C# project (incl. single-file)",
                ilspy is null ? "dotnet tool install --global ilspycmd" : "dotnet tool update --global ilspycmd", ilspy),
            new("Decompile · APK / JAR", "jadx", jadx is not null && java is not null,
                jadx is null ? "jadx not installed" : java is null ? "jadx needs Java on PATH" : ".apk/.dex/.jar → Java + resources",
                SetupCommand("jadx"), jadx),
            new("Rebuild · APK", "apktool + SDK build-tools",
                AndroidToolchain.ApktoolJar is not null && java is not null && AndroidToolchain.BuildTools is not null,
                AndroidToolchain.ApktoolJar is null ? "apktool not installed" : java is null ? "apktool needs Java on PATH" :
                AndroidToolchain.BuildTools is null ? "needs Android SDK build-tools (zipalign/apksigner)" :
                AndroidToolchain.Adb is null ? "decode → rebuild → sign (no adb: install manually)" : "decode → rebuild → sign → adb install",
                SetupCommand("apktool"), AndroidToolchain.ApktoolJar),
            new("Decompile · Native", "Ghidra headless", ghidra is not null && java is not null,
                ghidra is null ? "Ghidra not installed" : java is null ? "Ghidra needs JDK 21 on PATH" : "C/C++/Rust/Go .exe → approximate C",
                SetupCommand("ghidra"), ghidra),
            new("Decompile · Python EXE", pycdc is null ? "pyinstxtractor" : "pyinstxtractor + pycdc", extractor is not null && python is not null,
                extractor is null ? "PyInstaller extractor not installed" : python is null ? "needs Python on PATH" :
                pycdc is null ? "extracts .pyc (add pycdc for .py)" : "PyInstaller .exe → .py",
                SetupCommand(extractor is null ? "pyinstxtractor" : "pycdc"), extractor)
        ];
    }

    private void EnsureEmbeddedFiles()
    {
        Directory.CreateDirectory(Path.Combine(ToolsRoot, "scripts"));
        WriteResource("Install-Decompilers.ps1", InstallScript);
        WriteResource("MyCompilerExportC.java", Path.Combine(ToolsRoot, "scripts", "MyCompilerExportC.java"));
    }

    private static void WriteResource(string name, string target)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return;
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        if (File.Exists(target) && File.ReadAllText(target) == content) return;
        File.WriteAllText(target, content);
    }

    // ───────────────────────── Detection ─────────────────────────

    public static BinaryKind Detect(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".apk" or ".aab" or ".xapk" or ".dex") return BinaryKind.Android;
        if (extension is ".jar" or ".class") return BinaryKind.Jvm;

        var bytes = File.ReadAllBytes(path);
        if (!TryReadPe(bytes, out var isManaged)) return BinaryKind.Unknown;
        if (isManaged) return BinaryKind.DotNet;
        if (FindBundleHeader(bytes) > 0) return BinaryKind.DotNetBundle;
        if (bytes.AsSpan().LastIndexOf(PyInstallerCookie) >= 0) return BinaryKind.PyInstaller;
        if (FindAppHostDll(path) is not null) return BinaryKind.DotNetAppHost;
        return BinaryKind.Native;
    }

    public static string Describe(BinaryKind kind) => kind switch
    {
        BinaryKind.DotNet => ".NET assembly",
        BinaryKind.DotNetBundle => ".NET single-file app",
        BinaryKind.DotNetAppHost => ".NET app launcher",
        BinaryKind.PyInstaller => "PyInstaller Python app",
        BinaryKind.Android => "Android package",
        BinaryKind.Jvm => "Java archive",
        BinaryKind.Native => "native Windows binary",
        _ => "unrecognised file"
    };

    private static bool TryReadPe(ReadOnlySpan<byte> bytes, out bool isManaged)
    {
        isManaged = false;
        if (bytes.Length < 0x100 || bytes[0] != 'M' || bytes[1] != 'Z') return false;
        var pe = BitConverter.ToInt32(bytes[0x3C..]);
        if (pe <= 0 || pe + 0x18 >= bytes.Length || bytes[pe] != 'P' || bytes[pe + 1] != 'E') return false;
        var optional = pe + 0x18;
        var magic = BitConverter.ToUInt16(bytes[optional..]);
        var dataDirectories = optional + (magic == 0x20B ? 0x70 : 0x60);
        var clrEntry = dataDirectories + 14 * 8;
        if (clrEntry + 8 > bytes.Length) return true;
        isManaged = BitConverter.ToUInt32(bytes[clrEntry..]) != 0;
        return true;
    }

    private static bool IsManagedAssembly(string file)
    {
        try
        {
            var head = new byte[4096];
            using var stream = File.OpenRead(file);
            var read = stream.Read(head, 0, head.Length);
            return TryReadPe(head.AsSpan(0, read), out var managed) && managed;
        }
        catch { return false; }
    }

    private static long FindBundleHeader(byte[] bytes)
    {
        var index = bytes.AsSpan().IndexOf(BundleSignature);
        return index >= 8 ? BitConverter.ToInt64(bytes, index - 8) : -1;
    }

    private static string? FindAppHostDll(string exePath)
    {
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var dll = Path.ChangeExtension(exePath, ".dll");
        return File.Exists(dll) && IsManagedAssembly(dll) ? dll : null;
    }

    // ───────────────────────── Running ─────────────────────────

    public async Task<DecompileResult> RunAsync(
        string inputPath,
        Func<CommandStep, Task<bool>> execute,
        Action<string> log,
        CancellationToken token)
    {
        EnsureEmbeddedFiles();
        var kind = Detect(inputPath);
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var output = Path.Combine(OutputRoot, $"{name}_{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(output);
        log($"Detected: {Describe(kind)}");
        log($"Output:   {output}");
        log(string.Empty);

        var result = kind switch
        {
            BinaryKind.DotNet => await DecompileDotNetAsync([inputPath], Path.GetDirectoryName(inputPath)!, output, execute, log),
            BinaryKind.DotNetAppHost => await DecompileDotNetAsync([FindAppHostDll(inputPath)!], Path.GetDirectoryName(inputPath)!, output, execute, log),
            BinaryKind.DotNetBundle => await DecompileBundleAsync(inputPath, output, execute, log, token),
            BinaryKind.PyInstaller => await DecompilePyInstallerAsync(inputPath, output, execute, log),
            BinaryKind.Android or BinaryKind.Jvm => await DecompileJadxAsync(inputPath, output, execute, log),
            BinaryKind.Native => await DecompileNativeAsync(inputPath, output, execute, log),
            _ => Fail(output, "This file isn't a PE executable, APK or JAR that MyCompiler knows how to decompile.", log)
        };

        await File.WriteAllTextAsync(Path.Combine(output, "_DECOMPILED.txt"),
            $"Source:   {inputPath}{Environment.NewLine}Type:     {Describe(kind)}{Environment.NewLine}" +
            $"Created:  {DateTime.Now:F}{Environment.NewLine}Result:   {result.Summary}{Environment.NewLine}", CancellationToken.None);
        return result;
    }

    private static DecompileResult Fail(string output, string message, Action<string> log)
    {
        log($"✕ {message}");
        return new DecompileResult(false, output, message);
    }

    private async Task<DecompileResult> DecompileDotNetAsync(
        IReadOnlyList<string> assemblies, string referenceDir, string output,
        Func<CommandStep, Task<bool>> execute, Action<string> log)
    {
        var ilspy = IlSpy;
        if (ilspy is null) return Fail(output, "ilspycmd is not installed. Run: dotnet tool install --global ilspycmd --version 9.*", log);

        var ok = 0;
        foreach (var assembly in assemblies)
        {
            var target = Path.Combine(output, Path.GetFileNameWithoutExtension(assembly));
            Directory.CreateDirectory(target);
            var step = new CommandStep($"Decompile {Path.GetFileName(assembly)}", ilspy,
                ["-p", "-o", target, "-r", referenceDir, assembly], output);
            if (!await execute(step)) continue;
            ok++;
            DecompiledProjectRepair.Repair(target, assembly, log);
        }
        return ok > 0
            ? new DecompileResult(true, output, $"{ok} of {assemblies.Count} assemblies decompiled to C# projects")
            : Fail(output, "ILSpy could not decompile the assemblies.", log);
    }

    private async Task<DecompileResult> DecompileBundleAsync(
        string exePath, string output, Func<CommandStep, Task<bool>> execute, Action<string> log, CancellationToken token)
    {
        var bundleDir = Path.Combine(output, "_bundle");
        log("› Extract .NET single-file bundle");
        int count;
        try
        {
            count = await Task.Run(() => ExtractBundle(exePath, bundleDir), token);
        }
        catch (Exception ex)
        {
            return Fail(output, $"Bundle extraction failed: {ex.Message}", log);
        }
        log($"✓ Extracted {count} files to _bundle");

        var mainName = Path.GetFileNameWithoutExtension(exePath);
        var userAssemblies = Directory.EnumerateFiles(bundleDir, "*.dll", SearchOption.AllDirectories)
            .Where(file => !IsFrameworkAssembly(Path.GetFileNameWithoutExtension(file)) && IsManagedAssembly(file))
            .OrderByDescending(file => Path.GetFileNameWithoutExtension(file).Equals(mainName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (userAssemblies.Count == 0)
            return Fail(output, "No application assemblies found inside the bundle.", log);

        log($"  App assemblies: {string.Join(", ", userAssemblies.Select(Path.GetFileName))}");
        log(string.Empty);
        return await DecompileDotNetAsync(userAssemblies, bundleDir, output, execute, log);
    }

    private static bool IsFrameworkAssembly(string name) =>
        name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System", StringComparison.OrdinalIgnoreCase)
        || FrameworkPrefixes.Where(prefix => prefix != "System").Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts a .NET 5+ single-file bundle (format v2–v6, including compressed entries).</summary>
    public static int ExtractBundle(string exePath, string targetDir)
    {
        var bytes = File.ReadAllBytes(exePath);
        var headerOffset = FindBundleHeader(bytes);
        if (headerOffset <= 0 || headerOffset >= bytes.Length) throw new InvalidDataException("bundle header not found");

        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        stream.Position = headerOffset;
        var major = reader.ReadUInt32();
        reader.ReadUInt32(); // minor
        var fileCount = reader.ReadInt32();
        reader.ReadString(); // bundle id
        if (major >= 2)
        {
            reader.ReadInt64(); reader.ReadInt64(); // deps.json offset/size
            reader.ReadInt64(); reader.ReadInt64(); // runtimeconfig.json offset/size
            reader.ReadUInt64(); // flags
        }

        Directory.CreateDirectory(targetDir);
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        for (var index = 0; index < fileCount; index++)
        {
            var offset = reader.ReadInt64();
            var size = reader.ReadInt64();
            var compressedSize = major >= 6 ? reader.ReadInt64() : 0;
            reader.ReadByte(); // file type
            var relative = reader.ReadString().Replace('/', Path.DirectorySeparatorChar);

            var destination = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // path traversal guard
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var file = File.Create(destination);
            if (compressedSize > 0)
            {
                using var source = new MemoryStream(bytes, (int)offset, (int)compressedSize, writable: false);
                using var inflate = new DeflateStream(source, CompressionMode.Decompress);
                inflate.CopyTo(file);
            }
            else
            {
                file.Write(bytes, (int)offset, (int)size);
            }
        }
        return fileCount;
    }

    private async Task<DecompileResult> DecompilePyInstallerAsync(
        string exePath, string output, Func<CommandStep, Task<bool>> execute, Action<string> log)
    {
        var extractor = PyInstxtractor;
        var python = Python;
        if (extractor is null || python is null)
            return Fail(output, $"PyInstaller support needs Python and pyinstxtractor. Run: {SetupCommand("pyinstxtractor")}", log);

        var pythonArgs = Path.GetFileNameWithoutExtension(python).Equals("py", StringComparison.OrdinalIgnoreCase) ? new[] { "-3" } : [];
        if (!await execute(new CommandStep("Extract PyInstaller archive", python, [.. pythonArgs, extractor, exePath], output)))
            return Fail(output, "pyinstxtractor could not unpack this executable.", log);

        var extracted = Directory.GetDirectories(output, "*_extracted").FirstOrDefault();
        if (extracted is null) return Fail(output, "Extraction produced no output folder.", log);

        // Entry-point scripts live at the top level of the archive; PYZ holds bundled libraries.
        var scripts = Directory.GetFiles(extracted, "*.pyc")
            .Where(file => !Path.GetFileName(file).StartsWith("pyi", StringComparison.OrdinalIgnoreCase)
                           && !Path.GetFileName(file).StartsWith("pyimod", StringComparison.OrdinalIgnoreCase)
                           && !Path.GetFileName(file).StartsWith("struct", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pycdc = Pycdc;
        if (pycdc is null)
        {
            log(string.Empty);
            log($"Entry scripts (.pyc): {string.Join(", ", scripts.Select(Path.GetFileName))}");
            log($"Install pycdc to turn these into .py: {SetupCommand("pycdc")}");
            return new DecompileResult(true, output, $"Extracted {scripts.Count} entry script(s) as .pyc");
        }

        var sourceDir = Path.Combine(output, "source");
        Directory.CreateDirectory(sourceDir);
        var ok = 0;
        var disassembled = 0;
        foreach (var script in scripts)
        {
            var target = Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(script) + ".py");
            if (await execute(new CommandStep($"Decompile {Path.GetFileName(script)}", pycdc, ["-o", target, script], output)))
            {
                ok++;
                continue;
            }
            // pycdc lags behind new Python releases; fall back to a readable bytecode listing via the local interpreter.
            log("  pycdc can't read this bytecode version — writing a disassembly with the local Python instead.");
            var listing = Path.Combine(sourceDir, Path.GetFileNameWithoutExtension(script) + ".dis.txt");
            const string disScript = "import dis,marshal,sys\nf=open(sys.argv[1],'rb');f.read(16);c=marshal.load(f)\nwith open(sys.argv[2],'w',encoding='utf-8') as o: dis.dis(c,file=o)";
            File.Delete(target);
            if (await execute(new CommandStep($"Disassemble {Path.GetFileName(script)}", python, [.. pythonArgs, "-c", disScript, script, listing], output)))
                disassembled++;
        }
        var summary = $"{ok} of {scripts.Count} entry script(s) decompiled to source\\";
        if (disassembled > 0) summary += $", {disassembled} disassembled (.dis.txt)";
        return new DecompileResult(ok + disassembled > 0, output, summary);
    }

    private async Task<DecompileResult> DecompileJadxAsync(
        string path, string output, Func<CommandStep, Task<bool>> execute, Action<string> log)
    {
        var jadx = Jadx;
        if (jadx is null) return Fail(output, $"jadx is not installed. Run: {SetupCommand("jadx")}", log);

        var clean = await execute(new CommandStep("Decompile with jadx", jadx,
            ["-d", output, "--show-bad-code", "--deobf", "--log-level", "progress", path], output));
        var sources = Path.Combine(output, "sources");
        var javaFiles = Directory.Exists(sources) ? Directory.EnumerateFiles(sources, "*.java", SearchOption.AllDirectories).Count() : 0;
        if (javaFiles == 0) return Fail(output, "jadx produced no Java sources.", log);
        if (!clean) log("  jadx reported errors for some classes; partial output is included (look for /* JADX WARNING */).");

        var summary = $"{javaFiles:N0} Java files in sources\\, resources in resources\\";
        var isApk = Path.GetExtension(path).ToLowerInvariant() is ".apk";
        if (isApk && AndroidToolchain.ApktoolJar is { } jar && Java is { } java)
        {
            log(string.Empty);
            if (await execute(AndroidToolchain.DecodeStep(path, output, java, jar)))
            {
                summary += $"; rebuildable smali project in {AndroidToolchain.ProjectFolder}\\ (Build & Run = rebuild, sign, install)";
            }
            else log("  apktool could not decode this APK; the jadx output above is still usable for reading.");
        }
        else if (isApk)
        {
            log($"  Tip: install apktool to also get a rebuildable project: {SetupCommand("apktool")}");
        }
        return new DecompileResult(true, output, summary);
    }

    private async Task<DecompileResult> DecompileNativeAsync(
        string path, string output, Func<CommandStep, Task<bool>> execute, Action<string> log)
    {
        var ghidra = GhidraHeadless;
        if (ghidra is null) return Fail(output, $"Ghidra is not installed. Run: {SetupCommand("ghidra")}", log);
        if (Java is null) return Fail(output, "Ghidra needs a Java 21 JDK on PATH.", log);

        var project = Path.Combine(Path.GetTempPath(), "MyCompiler-Ghidra", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        var cFile = Path.Combine(output, Path.GetFileNameWithoutExtension(path) + ".c");
        log("Ghidra runs full auto-analysis first; large binaries can take several minutes. Esc stops it.");
        try
        {
            var ok = await execute(new CommandStep("Analyse and decompile with Ghidra", ghidra,
            [
                project, "MyCompiler", "-import", path, "-overwrite",
                "-scriptPath", Path.Combine(ToolsRoot, "scripts"),
                "-postScript", "MyCompilerExportC.java", cFile,
                "-deleteProject"
            ], output));
            if (!ok || !File.Exists(cFile)) return Fail(output, "Ghidra did not produce output.", log);
            return new DecompileResult(true, output, $"Approximate C written to {Path.GetFileName(cFile)}");
        }
        finally
        {
            try { Directory.Delete(project, true); } catch { /* best effort */ }
        }
    }
}
