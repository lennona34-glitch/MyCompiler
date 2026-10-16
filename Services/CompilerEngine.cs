using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MyCompiler.Models;

namespace MyCompiler.Services;

public sealed partial class CompilerEngine
{
    private static readonly IReadOnlyDictionary<string, ToolchainInstruction> ToolchainInstructions =
        new Dictionary<string, ToolchainInstruction>(StringComparer.OrdinalIgnoreCase)
        {
            ["dotnet"] = new("winget install --id Microsoft.DotNet.SDK.9 -e", "winget upgrade --id Microsoft.DotNet.SDK.9 -e"),
            ["cpp"] = new("winget install --id MSYS2.MSYS2 -e; C:\\msys64\\usr\\bin\\bash.exe -lc \"pacman -S --needed mingw-w64-ucrt-x86_64-gcc\"", "winget upgrade --id MSYS2.MSYS2 -e"),
            ["rust"] = new("winget install --id Rustlang.Rustup -e", "rustup update"),
            ["go"] = new("winget install --id GoLang.Go -e", "winget upgrade --id GoLang.Go -e"),
            ["java"] = new("winget install --id EclipseAdoptium.Temurin.21.JDK -e", "winget upgrade --id EclipseAdoptium.Temurin.21.JDK -e"),
            ["kotlin"] = new("https://kotlinlang.org/docs/command-line.html", "https://kotlinlang.org/docs/command-line.html"),
            ["scala"] = new("winget install --id Scala.Scala.2 -e", "winget upgrade --id Scala.Scala.2 -e"),
            ["swift"] = new("winget install --id Swift.Toolchain -e", "winget upgrade --id Swift.Toolchain -e"),
            ["zig"] = new("winget install --id zig.zig -e", "winget upgrade --id zig.zig -e"),
            ["dart"] = new("winget install --id Google.DartSDK -e", "winget upgrade --id Google.DartSDK -e"),
            ["pascal"] = new("winget install --id FreePascal.FreePascalCompiler -e", "winget upgrade --id FreePascal.FreePascalCompiler -e"),
            ["nim"] = new("https://nim-lang.org/install_windows.html", "https://nim-lang.org/install_windows.html"),
            ["d"] = new("winget install --id Dlang.DMD -e", "winget upgrade --id Dlang.DMD -e"),
            ["haskell"] = new("https://www.haskell.org/ghcup/install/", "ghcup upgrade; ghcup install ghc recommended; ghcup set ghc recommended"),
            ["ocaml"] = new("winget install --id Diskuv.OCaml -e", "opam update; opam upgrade"),
            ["typescript"] = new("npm install --global typescript", "npm update --global typescript"),
            ["javascript"] = new("winget install --id OpenJS.NodeJS.LTS -e", "winget upgrade --id OpenJS.NodeJS.LTS -e"),
            ["python"] = new("winget install --id Python.Python.3.14 -e", "winget upgrade --id Python.Python.3.14 -e; py -3 -m pip install --upgrade pip pyinstaller"),
            ["ruby"] = new("winget install --id RubyInstallerTeam.RubyWithDevKit.3.4 -e", "winget upgrade --id RubyInstallerTeam.RubyWithDevKit.3.4 -e"),
            ["php"] = new("winget install --id PHP.PHP.8.4 -e", "winget upgrade --id PHP.PHP.8.4 -e"),
            ["powershell"] = new("winget install --id Microsoft.PowerShell -e", "winget upgrade --id Microsoft.PowerShell -e"),
            ["bash"] = new("winget install --id Git.Git -e", "winget upgrade --id Git.Git -e"),
            ["lua"] = new("winget install --id DEVCOM.Lua -e", "winget upgrade --id DEVCOM.Lua -e"),
            ["r"] = new("winget install --id RProject.R -e", "winget upgrade --id RProject.R -e"),
            ["elixir"] = new("https://elixir-lang.org/install.html#windows", "https://elixir-lang.org/install.html#windows")
        };

    public IReadOnlyList<LanguageDefinition> Languages { get; } =
    [
        new("dotnet", ".NET (C# / F# / VB)", [".cs", ".fs", ".vb"], ["dotnet"], "compile + run"),
        new("cpp", "C / C++", [".c", ".h", ".cc", ".cpp", ".cxx", ".hpp"], ["clang", "gcc", "cl"], "native compile + run"),
        new("rust", "Rust", [".rs"], ["cargo", "rustc"], "native compile + run"),
        new("go", "Go", [".go"], ["go"], "native compile + run"),
        new("java", "Java", [".java"], ["javac"], "bytecode compile + run"),
        new("kotlin", "Kotlin", [".kt", ".kts"], ["kotlinc"], "JVM compile + run"),
        new("scala", "Scala", [".scala"], ["scalac"], "JVM compile + run"),
        new("swift", "Swift", [".swift"], ["swiftc"], "native compile + run"),
        new("zig", "Zig", [".zig"], ["zig"], "native compile + run"),
        new("dart", "Dart", [".dart"], ["dart"], "native compile + run"),
        new("pascal", "Pascal", [".pas", ".pp"], ["fpc"], "native compile + run"),
        new("nim", "Nim", [".nim"], ["nim"], "native compile + run"),
        new("d", "D", [".d"], ["dmd", "ldc2"], "native compile + run"),
        new("haskell", "Haskell", [".hs"], ["ghc"], "native compile + run"),
        new("ocaml", "OCaml", [".ml"], ["ocamlopt"], "native compile + run"),
        new("typescript", "TypeScript", [".ts", ".tsx"], ["tsc", "npx"], "transpile + run"),
        new("javascript", "JavaScript", [".js", ".mjs", ".cjs"], ["node"], "validate + run"),
        new("python", "Python", [".py", ".pyw"], ["py", "python"], "package .exe + run"),
        new("ruby", "Ruby", [".rb"], ["ruby"], "validate + run"),
        new("php", "PHP", [".php"], ["php"], "validate + run"),
        new("powershell", "PowerShell", [".ps1"], ["pwsh", "powershell"], "parse + run"),
        new("bash", "Shell", [".sh", ".bash"], ["C:\\msys64\\ucrt64\\bin\\bash.exe", "C:\\Program Files\\Git\\bin\\bash.exe", "bash"], "validate + run"),
        new("lua", "Lua", [".lua"], ["luac", "lua"], "byte-compile + run"),
        new("r", "R", [".r"], ["Rscript"], "parse + run"),
        new("elixir", "Elixir", [".ex", ".exs"], ["elixir", "elixirc"], "compile + run")
    ];

    public LanguageDefinition? DetectLanguage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return Languages.FirstOrDefault(language => language.Extensions.Contains(extension));
    }

    public Task<IReadOnlyList<ToolchainStatus>> ScanToolchainsAsync() => Task.Run<IReadOnlyList<ToolchainStatus>>(() =>
    {
        var statuses = Languages.Select(language =>
        {
            var resolved = ToolLocator.Resolve(language.ToolCandidates.ToArray());
            var instruction = ToolchainInstructions[language.Id];
            return new ToolchainStatus(
                language.DisplayName,
                resolved is null ? string.Join(" / ", language.ToolCandidates) : Path.GetFileName(resolved),
                resolved is not null,
                resolved is null ? "Not found on PATH" : language.Capability,
                resolved is null ? instruction.Install : GetUpdateCommand(language.Id, resolved, instruction.Update),
                resolved);
        }).ToList();

        var python = GetPythonRuntime();
        var pyInstallerVersion = python is null ? null : GetPyInstallerVersion(python);
        statuses.Add(new ToolchainStatus(
            "Python → EXE",
            pyInstallerVersion is null ? "PyInstaller" : $"PyInstaller {pyInstallerVersion}",
            pyInstallerVersion is not null,
            pyInstallerVersion is null ? "Python packager is not installed" : "standalone Windows .exe packaging",
            pyInstallerVersion is null ? "py -3 -m pip install pyinstaller" : "py -3 -m pip install --upgrade pyinstaller",
            python?.Tool));
        return statuses;
    });

    public string CreatePathReport(IEnumerable<ToolchainStatus> toolchains)
    {
        var report = new StringBuilder();
        report.AppendLine("MyCompiler PATH Doctor · read-only report");
        report.AppendLine(new string('─', 72));
        report.AppendLine("Nothing was installed, uninstalled, or changed.");
        report.AppendLine();

        var entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => Environment.ExpandEnvironmentVariables(entry.Trim().Trim('\"')))
            .Where(entry => entry.Length > 0)
            .ToList();
        var missingEntries = entries.Where(entry => !Directory.Exists(entry)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var duplicates = entries.GroupBy(entry => entry.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();

        report.AppendLine($"PATH folders: {entries.Count} · invalid: {missingEntries.Count} · duplicate: {duplicates.Count}");
        foreach (var entry in missingEntries)
            report.AppendLine($"  ! Missing folder: {entry}");
        foreach (var entry in duplicates)
            report.AppendLine($"  ! Duplicate folder: {entry}");
        if (missingEntries.Count == 0 && duplicates.Count == 0)
            report.AppendLine("  ✓ PATH folder list is structurally clean.");

        report.AppendLine();
        report.AppendLine("TOOLCHAIN RESOLUTION");
        foreach (var toolchain in toolchains)
        {
            report.AppendLine(toolchain.IsAvailable
                ? $"  ✓ {toolchain.Name,-23} {toolchain.ResolvedPath ?? toolchain.Tool}"
                : $"  ✕ {toolchain.Name,-23} not found");
            if (!toolchain.IsAvailable && !string.IsNullOrWhiteSpace(toolchain.SuggestedCommand))
                report.AppendLine($"      Setup: {toolchain.SuggestedCommand}");
        }

        report.AppendLine();
        report.AppendLine("Advice: restart MyCompiler after an installer changes PATH. Do not reinstall a tool solely because it is off PATH; repair the PATH entry first when the installation is otherwise healthy.");
        return report.ToString();
    }

    private static string GetUpdateCommand(string languageId, string resolved, string fallback)
    {
        if (languageId == "cpp" && resolved.Contains("msys64", StringComparison.OrdinalIgnoreCase))
            return "C:\\msys64\\usr\\bin\\bash.exe -lc \"pacman -Syu\"";
        if (languageId == "java" && resolved.Contains("Android Studio", StringComparison.OrdinalIgnoreCase))
            return "winget upgrade --id Google.AndroidStudio -e";
        if (languageId == "bash" && resolved.Contains("msys64", StringComparison.OrdinalIgnoreCase))
            return "C:\\msys64\\usr\\bin\\bash.exe -lc \"pacman -Syu\"";
        return fallback;
    }

    public BuildPlan CreatePlan(string sourcePath, string? workspaceRoot)
    {
        var project = FindProject(sourcePath, workspaceRoot) ?? FindDecompiledProject(sourcePath, workspaceRoot);
        if (project is not null)
        {
            var projectPlan = CreateProjectPlan(project, sourcePath);
            if (projectPlan is not null)
                return projectPlan;
        }

        return CreateFilePlan(sourcePath);
    }

    /// <summary>
    /// Decompiled sources (jadx Java, ILSpy C#) often sit beside — not inside — the rebuildable project
    /// (e.g. {output}\jadx\... vs {output}\apktool\apktool.yml). Search down from the decompile output root.
    /// </summary>
    private static string? FindDecompiledProject(string sourcePath, string? workspaceRoot)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
        for (var i = 0; current is not null && i < 12; i++, current = current.Parent)
        {
            if (DecompileFolderRegex().IsMatch(current.Name))
                return FindProjectBelow(current.FullName, maxDepth: 3);
        }
        return !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? FindProjectBelow(workspaceRoot, maxDepth: 3)
            : null;
    }

    [GeneratedRegex(@"_\d{8}-\d{6}$")]
    private static partial Regex DecompileFolderRegex();

    /// <summary>
    /// Finds the main project inside a workspace folder (searching a few levels down) and plans a build for it.
    /// Returns null when the folder has no recognisable project.
    /// </summary>
    public BuildPlan? CreateWorkspacePlan(string workspaceRoot)
    {
        var marker = FindProjectBelow(workspaceRoot, maxDepth: 3);
        return marker is null ? null : CreateProjectPlan(marker, marker);
    }

    private static string? FindProjectBelow(string root, int maxDepth)
    {
        string[] patterns = ["*.slnx", "*.sln", "*.csproj", "*.fsproj", "*.vbproj"];
        string[] exact = ["Cargo.toml", "go.mod", "package.json", "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Makefile"];
        var level = new List<string> { root };
        for (var depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            // Breadth-first so the shallowest project wins; apktool first, since decompile output also contains Java sources.
            foreach (var dir in level)
                if (File.Exists(Path.Combine(dir, AndroidToolchain.Marker))) return Path.Combine(dir, AndroidToolchain.Marker);
            foreach (var dir in level)
            {
                foreach (var pattern in patterns)
                    if (Directory.EnumerateFiles(dir, pattern).FirstOrDefault() is { } match) return match;
                foreach (var name in exact)
                    if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
            }
            level = level.SelectMany(dir =>
            {
                try
                {
                    return Directory.EnumerateDirectories(dir)
                        .Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "node_modules" or ".git" or "_bundle" or "sources" or "resources" or "smali" or "dist"));
                }
                catch { return []; }
            }).ToList();
        }
        return null;
    }

    private BuildPlan? CreateProjectPlan(string marker, string sourcePath)
    {
        var root = Directory.Exists(marker) ? marker : Path.GetDirectoryName(marker)!;
        var fileName = Path.GetFileName(marker);
        var extension = Path.GetExtension(marker).ToLowerInvariant();

        if (fileName.Equals(AndroidToolchain.Marker, StringComparison.OrdinalIgnoreCase))
            return AndroidToolchain.CreatePlan(marker);

        if (extension is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj")
        {
            RequireTool("dotnet", ".NET SDK");
            var dist = Path.Combine(root, "dist");
            var stem = Path.GetFileNameWithoutExtension(marker);
            var exe = Path.Combine(dist, stem + ".exe");
            return new BuildPlan(
                ".NET project",
                [Step("Publish .NET project", "dotnet", root, "publish", marker, "-c", "Release", "-o", dist, "--nologo", "--verbosity:minimal")],
                extension is ".sln" or ".slnx"
                    ? Step("Run .NET project", "dotnet", root, "run", "--project", marker)
                    : Step("Run", exe, dist),
                $"Project publish · {fileName}",
                extension is ".sln" or ".slnx" ? null : exe);
        }

        if (fileName.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            RequireTool("cargo", "Rust / Cargo");
            return new BuildPlan("Rust project",
                [Step("Cargo build", "cargo", root, "build", "--manifest-path", marker)],
                Step("Cargo run", "cargo", root, "run", "--manifest-path", marker),
                "Cargo workspace build");
        }

        if (fileName.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
        {
            RequireTool("go", "Go");
            return new BuildPlan("Go module",
                [Step("Build Go module", "go", root, "build", "./...")],
                Step("Run Go module", "go", root, "run", "."),
                "Go module build");
        }

        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            var (hasBuild, hasStart) = ReadPackageScripts(marker);
            if (!hasBuild && File.Exists(Path.Combine(root, "tsconfig.json")))
            {
                RequireTool("npx", "Node.js / TypeScript");
                return new BuildPlan("TypeScript project",
                    [Step("Type-check project", "npx", root, "tsc", "--noEmit")],
                    hasStart ? Step("Start package", "npm", root, "run", "start") : CreateFilePlan(sourcePath).RunStep,
                    "TypeScript project check");
            }

            if (hasBuild)
            {
                RequireTool("npm", "Node.js / npm");
                return new BuildPlan("Node package",
                    [Step("Run package build", "npm", root, "run", "build")],
                    hasStart ? Step("Start package", "npm", root, "run", "start") : CreateFilePlan(sourcePath).RunStep,
                    "Package script · build");
            }
        }

        if (fileName.Equals("pom.xml", StringComparison.OrdinalIgnoreCase))
        {
            var tool = File.Exists(Path.Combine(root, "mvnw.cmd")) ? Path.Combine(root, "mvnw.cmd") : "mvn";
            RequireTool(tool, "Maven");
            return new BuildPlan("Maven project",
                [Step("Package Maven project", tool, root, "-q", "package")], null, "Maven package");
        }

        if (fileName is "build.gradle" or "build.gradle.kts")
        {
            var tool = File.Exists(Path.Combine(root, "gradlew.bat")) ? Path.Combine(root, "gradlew.bat") : "gradle";
            RequireTool(tool, "Gradle");
            return new BuildPlan("Gradle project",
                [Step("Build Gradle project", tool, root, "build")], null, "Gradle build");
        }

        if (fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
        {
            RequireTool("cmake", "CMake");
            var output = Path.Combine(GetCacheDirectory(sourcePath), "cmake-build");
            return new BuildPlan("CMake project",
                [
                    Step("Configure CMake", "cmake", root, "-S", root, "-B", output),
                    Step("Build CMake", "cmake", root, "--build", output, "--config", "Release")
                ], null, "CMake configure + build");
        }

        if (fileName.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
        {
            var tool = ToolLocator.Resolve("mingw32-make", "make") ?? "make";
            RequireTool(tool, "Make");
            return new BuildPlan("Make project", [Step("Run Make", tool, root)], null, "Make build");
        }

        return null;
    }

    private BuildPlan CreateFilePlan(string source)
    {
        var language = DetectLanguage(source)
            ?? throw new NotSupportedException($"No built-in recipe for '{Path.GetExtension(source)}'. Use the custom command lane.");
        var working = Path.GetDirectoryName(source)!;
        var cache = GetCacheDirectory(source);
        Directory.CreateDirectory(cache);
        var stem = Path.GetFileNameWithoutExtension(source);
        var exe = Path.Combine(cache, stem + ".exe");

        return language.Id switch
        {
            "dotnet" => CreateDotNetFilePlan(source, language, cache, stem),
            "cpp" => CreateCppPlan(source, cache, exe),
            "rust" => Plan(language, Step("Compile Rust", RequireTool("rustc", "Rust"), working, source, "-o", exe), Step("Run", exe, working)),
            "go" => Plan(language, Step("Compile Go", RequireTool("go", "Go"), working, "build", "-o", exe, source), Step("Run", exe, working)),
            "java" => Plan(language, Step("Compile Java", RequireTool("javac", "Java JDK"), working, "-d", cache, source), Step("Run", RequireTool("java", "Java runtime"), working, "-cp", cache, stem)),
            "kotlin" => CreateKotlinPlan(source, language, cache, working, stem),
            "scala" => Plan(language, Step("Compile Scala", RequireTool("scalac", "Scala"), working, "-d", cache, source), Step("Run", RequireTool("scala", "Scala"), working, "-cp", cache, stem)),
            "swift" => Plan(language, Step("Compile Swift", RequireTool("swiftc", "Swift"), working, source, "-o", exe), Step("Run", exe, working)),
            "zig" => Plan(language, Step("Compile Zig", RequireTool("zig", "Zig"), working, "build-exe", source, $"-femit-bin={exe}"), Step("Run", exe, working)),
            "dart" => Plan(language, Step("Compile Dart", RequireTool("dart", "Dart"), working, "compile", "exe", source, "-o", exe), Step("Run", exe, working)),
            "pascal" => Plan(language, Step("Compile Pascal", RequireTool("fpc", "Free Pascal"), cache, source, $"-o{exe}"), Step("Run", exe, working)),
            "nim" => Plan(language, Step("Compile Nim", RequireTool("nim", "Nim"), working, "c", $"--out:{exe}", source), Step("Run", exe, working)),
            "d" => CreateDPlan(source, language, working, exe),
            "haskell" => Plan(language, Step("Compile Haskell", RequireTool("ghc", "GHC"), cache, source, "-o", exe), Step("Run", exe, working)),
            "ocaml" => Plan(language, Step("Compile OCaml", RequireTool("ocamlopt", "OCaml"), cache, "-o", exe, source), Step("Run", exe, working)),
            "typescript" => CreateTypeScriptPlan(source, language, cache, working, stem),
            "javascript" => Plan(language, Step("Validate JavaScript", RequireTool("node", "Node.js"), working, "--check", source), Step("Run", "node", working, source)),
            "python" => CreatePythonPlan(source, language, working),
            "ruby" => Plan(language, Step("Validate Ruby", RequireTool("ruby", "Ruby"), working, "-c", source), Step("Run", "ruby", working, source)),
            "php" => Plan(language, Step("Validate PHP", RequireTool("php", "PHP"), working, "-l", source), Step("Run", "php", working, source)),
            "powershell" => CreatePowerShellPlan(source, language, working),
            "bash" => Plan(language, Step("Validate shell script", RequireTool("bash", "Bash"), working, "-n", source), Step("Run", "bash", working, source)),
            "lua" => Plan(language, Step("Compile Lua bytecode", RequireTool("luac", "Lua compiler"), working, "-p", source), Step("Run", RequireTool("lua", "Lua"), working, source)),
            "r" => Plan(language, Step("Parse R source", RequireTool("Rscript", "R"), working, "-e", $"parse(file={JsonSerializer.Serialize(source)})"), Step("Run", "Rscript", working, source)),
            "elixir" => Plan(language, Step("Compile Elixir", RequireTool("elixirc", "Elixir"), cache, source), Step("Run", RequireTool("elixir", "Elixir"), working, source)),
            _ => throw new NotSupportedException($"No recipe for {language.DisplayName}.")
        };
    }

    private BuildPlan CreateDotNetFilePlan(string source, LanguageDefinition language, string cache, string stem)
    {
        RequireTool("dotnet", ".NET SDK");
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var languageName = extension switch { ".vb" => "VB", ".fs" => "F#", _ => "C#" };
        var projectExtension = extension switch { ".vb" => ".vbproj", ".fs" => ".fsproj", _ => ".csproj" };
        var project = Path.Combine(cache, "SingleFile" + projectExtension);
        var escapedSource = SecurityElement.Escape(Path.GetFullPath(source))!;
        var compileItem = $"<Compile Include=\"{escapedSource}\" Link=\"{Path.GetFileName(source)}\" />";
        var projectText = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>{SecurityElement.Escape(stem)}</AssemblyName>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>{compileItem}</ItemGroup>
            </Project>
            """;
        File.WriteAllText(project, projectText);
        var dist = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!, "dist");
        var exe = Path.Combine(dist, stem + ".exe");
        return new BuildPlan(languageName,
            [Step($"Compile {languageName}", "dotnet", cache, "publish", project, "-c", "Release", "-o", dist, "--nologo", "--verbosity:minimal")],
            Step("Run", exe, dist),
            $"Single-file {languageName} build · {stem}",
            exe);
    }

    private BuildPlan CreateCppPlan(string source, string cache, string output)
    {
        var isCpp = Path.GetExtension(source).ToLowerInvariant() is not ".c";
        var compiler = ToolLocator.Resolve(isCpp ? ["clang++", "g++", "cl"] : ["clang", "gcc", "cl"])
            ?? throw Missing("C / C++ compiler (Clang, GCC, or MSVC)");
        var name = Path.GetFileNameWithoutExtension(compiler).ToLowerInvariant();
        CommandStep build;
        if (name == "cl")
        {
            var arguments = new List<string> { "/nologo" };
            if (isCpp) arguments.Add("/EHsc");
            arguments.Add(source);
            arguments.Add($"/Fe:{output}");
            build = new CommandStep("Compile with MSVC", compiler, arguments, cache);
        }
        else
        {
            var arguments = new List<string> { source };
            if (isCpp) arguments.Add("-std=c++20");
            arguments.AddRange(["-o", output]);
            build = new CommandStep($"Compile with {name}", compiler, arguments, cache);
        }
        return new BuildPlan(isCpp ? "C++" : "C", [build], Step("Run", output, cache), "Native single-file build");
    }

    private BuildPlan CreateKotlinPlan(string source, LanguageDefinition language, string cache, string working, string stem)
    {
        var jar = Path.Combine(cache, stem + ".jar");
        return Plan(language,
            Step("Compile Kotlin", RequireTool("kotlinc", "Kotlin"), working, source, "-include-runtime", "-d", jar),
            Step("Run", RequireTool("java", "Java runtime"), working, "-jar", jar));
    }

    private BuildPlan CreateDPlan(string source, LanguageDefinition language, string working, string exe)
    {
        var compiler = ToolLocator.Resolve("dmd", "ldc2") ?? throw Missing("D compiler");
        return Plan(language, Step("Compile D", compiler, working, source, $"-of={exe}"), Step("Run", exe, working));
    }

    private BuildPlan CreateTypeScriptPlan(string source, LanguageDefinition language, string cache, string working, string stem)
    {
        var tsc = ToolLocator.Resolve("tsc");
        CommandStep compile = tsc is not null
            ? Step("Transpile TypeScript", tsc, working, source, "--target", "ES2022", "--module", "commonjs", "--outDir", cache)
            : Step("Transpile TypeScript", RequireTool("npx", "TypeScript / Node.js"), working, "tsc", source, "--target", "ES2022", "--module", "commonjs", "--outDir", cache);
        var output = Path.Combine(cache, stem + ".js");
        return Plan(language, compile, Step("Run", RequireTool("node", "Node.js"), working, output));
    }

    private BuildPlan CreatePythonPlan(string source, LanguageDefinition language, string working)
    {
        var python = GetPythonRuntime() ?? throw Missing("Python");
        if (GetPyInstallerVersion(python) is null)
        {
            throw new InvalidOperationException(
                "Python is installed, but Python → EXE packaging needs PyInstaller. " +
                "Install it once with: py -3 -m pip install pyinstaller — then refresh Toolchains.");
        }

        var stem = Path.GetFileNameWithoutExtension(source);
        var outputDirectory = Path.Combine(working, "dist");
        var output = Path.Combine(outputDirectory, stem + ".exe");
        var cache = GetCacheDirectory(source);
        var buildDirectory = Path.Combine(cache, "pyinstaller-build");
        var specDirectory = Path.Combine(cache, "pyinstaller-spec");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(buildDirectory);
        Directory.CreateDirectory(specDirectory);

        var arguments = new List<string>(python.PrefixArguments)
        {
            "-m", "PyInstaller",
            "--onefile",
            "--noconfirm",
            "--clean",
            "--noupx",
            "--name", stem,
            "--distpath", outputDirectory,
            "--workpath", buildDirectory,
            "--specpath", specDirectory
        };
        if (Path.GetExtension(source).Equals(".pyw", StringComparison.OrdinalIgnoreCase))
            arguments.Add("--windowed");
        arguments.Add(source);

        return new BuildPlan(
            "Python → EXE",
            [Step("Package Python → EXE", python.Tool, working, arguments)],
            Step("Run packaged EXE", output, working),
            $"Standalone Python executable · {Path.GetFileName(output)}",
            output);
    }

    private static PythonRuntime? GetPythonRuntime()
    {
        var tool = ToolLocator.Resolve("py", "python");
        if (tool is null) return null;
        var isLauncher = Path.GetFileNameWithoutExtension(tool).Equals("py", StringComparison.OrdinalIgnoreCase);
        return new PythonRuntime(tool, isLauncher ? ["-3"] : []);
    }

    private static string? GetPyInstallerVersion(PythonRuntime python)
    {
        var info = new ProcessStartInfo
        {
            FileName = python.Tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in python.PrefixArguments)
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("PyInstaller");
        info.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(info);
            if (process is null || !process.WaitForExit(5000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var version = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version.Split('\n')[0].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record PythonRuntime(string Tool, IReadOnlyList<string> PrefixArguments);
    private sealed record ToolchainInstruction(string Install, string Update);

    private BuildPlan CreatePowerShellPlan(string source, LanguageDefinition language, string working)
    {
        var tool = ToolLocator.Resolve("pwsh", "powershell") ?? throw Missing("PowerShell");
        const string parser = "$e=$null;[System.Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$null,[ref]$e)|Out-Null;if($e){$e|ForEach-Object{Write-Error $_};exit 1}";
        return Plan(language,
            Step("Parse PowerShell", tool, working, "-NoProfile", "-Command", parser, source),
            Step("Run", tool, working, "-NoProfile", "-File", source));
    }

    private static BuildPlan Plan(LanguageDefinition language, CommandStep build, CommandStep run) =>
        new(language.DisplayName, [build], run, $"{language.Capability} · {language.DisplayName}");

    private static CommandStep Step(string caption, string executable, string workingDirectory, params string[] arguments) =>
        new(caption, executable, arguments, workingDirectory);

    private static CommandStep Step(string caption, string executable, string workingDirectory, IEnumerable<string> arguments) =>
        new(caption, executable, arguments.ToArray(), workingDirectory);

    private static string RequireTool(string candidate, string displayName)
    {
        if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            return candidate;
        return ToolLocator.Resolve(candidate) ?? throw Missing(displayName);
    }

    private static InvalidOperationException Missing(string displayName) =>
        new($"{displayName} is not installed or is not available on PATH. Install that toolchain, then refresh Toolchains.");

    private static string GetCacheDirectory(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToLowerInvariant()));
        var key = Convert.ToHexString(bytes)[..12];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "BuildCache", key);
    }

    private static string? FindProject(string sourcePath, string? workspaceRoot)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
        var boundary = string.IsNullOrWhiteSpace(workspaceRoot) ? current.FullName : Path.GetFullPath(workspaceRoot);
        string[] exact = ["Cargo.toml", "go.mod", "package.json", "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Makefile"];
        string[] patterns = ["*.slnx", "*.sln", "*.csproj", "*.fsproj", "*.vbproj"];

        while (current is not null && current.FullName.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            if (AndroidToolchain.FindMarker(current.FullName) is { } apktool) return apktool;
            foreach (var pattern in patterns)
            {
                var match = current.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (match is not null) return match.FullName;
            }
            foreach (var name in exact)
            {
                var marker = Path.Combine(current.FullName, name);
                if (File.Exists(marker)) return marker;
            }
            if (current.FullName.Equals(boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
        return null;
    }

    private static (bool Build, bool Start) ReadPackageScripts(string packageJson)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)) return (false, false);
            return (scripts.TryGetProperty("build", out _), scripts.TryGetProperty("start", out _));
        }
        catch
        {
            return (false, false);
        }
    }
}
