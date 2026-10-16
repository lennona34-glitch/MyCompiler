using System.Text.RegularExpressions;
using MyCompiler.Models;

namespace MyCompiler.Services;

/// <summary>
/// Locates apktool + Android SDK tools and builds the decode / rebuild → align → sign → install pipeline.
/// apktool works on smali (not the jadx Java), which is what makes the round trip reliable.
/// </summary>
public static partial class AndroidToolchain
{
    public const string ProjectFolder = "apktool";   // decoded project lives in {output}\apktool
    public const string Marker = "apktool.yml";

    private const string DebugAlias = "androiddebugkey";
    private const string DebugPassword = "android";

    public static string? ApktoolJar
    {
        get
        {
            var jar = Path.Combine(DecompilerEngine.ToolsRoot, "apktool", "apktool.jar");
            return File.Exists(jar) ? jar : null;
        }
    }

    public static string? Java => ToolLocator.Resolve("java");

    public static string? SdkRoot =>
        new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
        }.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    /// <summary>Newest build-tools folder that has both zipalign and apksigner.</summary>
    public static string? BuildTools
    {
        get
        {
            var dir = SdkRoot is { } sdk ? Path.Combine(sdk, "build-tools") : null;
            if (dir is null || !Directory.Exists(dir)) return null;
            return Directory.GetDirectories(dir)
                .Where(d => File.Exists(Path.Combine(d, "zipalign.exe")) && File.Exists(Path.Combine(d, "apksigner.bat")))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version(0, 0))
                .FirstOrDefault();
        }
    }

    public static string? Adb =>
        SdkRoot is { } sdk && File.Exists(Path.Combine(sdk, "platform-tools", "adb.exe"))
            ? Path.Combine(sdk, "platform-tools", "adb.exe")
            : ToolLocator.Resolve("adb");

    public static string? Keytool
    {
        get
        {
            if (Java is { } java && Path.Combine(Path.GetDirectoryName(java)!, "keytool.exe") is var sibling && File.Exists(sibling))
                return sibling;
            return ToolLocator.Resolve("keytool");
        }
    }

    public static string DebugKeystore =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "debug.keystore");

    public static CommandStep DecodeStep(string apk, string output, string java, string jar) =>
        new("Decode with apktool (rebuildable smali project)", java,
            ["-jar", jar, "d", "-f", "-o", Path.Combine(output, ProjectFolder), apk], output);

    /// <summary>Finds an apktool project at or one level below a directory ({dir}\apktool.yml or {dir}\apktool\apktool.yml).</summary>
    public static string? FindMarker(string directory)
    {
        var direct = Path.Combine(directory, Marker);
        if (File.Exists(direct)) return direct;
        var nested = Path.Combine(directory, ProjectFolder, Marker);
        return File.Exists(nested) ? nested : null;
    }

    public static BuildPlan CreatePlan(string marker)
    {
        var root = Path.GetDirectoryName(marker)!;
        var java = Java ?? throw new InvalidOperationException("Java is not on PATH; apktool needs it.");
        var jar = ApktoolJar ?? throw new InvalidOperationException(
            $"apktool is not installed. Run: powershell -ExecutionPolicy Bypass -File \"{Path.Combine(DecompilerEngine.ToolsRoot, "Install-Decompilers.ps1")}\" -Tool apktool");
        var buildTools = BuildTools ?? throw new InvalidOperationException(
            "Android SDK build-tools (zipalign + apksigner) not found. Install them from Android Studio → SDK Manager → SDK Tools.");

        var name = new DirectoryInfo(root).Parent?.Name is { Length: > 0 } parent && Path.GetFileName(root) == ProjectFolder
            ? Regex.Replace(parent, @"_\d{8}-\d{6}$", "")
            : Path.GetFileName(root);
        var dist = Path.Combine(root, "dist");
        var unsigned = Path.Combine(dist, $"{name}-unsigned.apk");
        var aligned = Path.Combine(dist, $"{name}-aligned.apk");
        var signed = Path.Combine(dist, $"{name}-signed.apk");

        var steps = new List<CommandStep>
        {
            new("Rebuild APK with apktool", java, ["-jar", jar, "b", "-f", "-o", unsigned, root], root),
            new("Align (zipalign)", Path.Combine(buildTools, "zipalign.exe"), ["-p", "-f", "4", unsigned, aligned], root)
        };

        if (!File.Exists(DebugKeystore))
        {
            var keytool = Keytool ?? throw new InvalidOperationException("keytool not found (it ships with the JDK) — needed to create a debug signing key.");
            Directory.CreateDirectory(Path.GetDirectoryName(DebugKeystore)!);
            steps.Add(new("Create debug signing key", keytool,
                ["-genkeypair", "-v", "-keystore", DebugKeystore, "-alias", DebugAlias, "-keyalg", "RSA", "-keysize", "2048",
                 "-validity", "10000", "-storepass", DebugPassword, "-keypass", DebugPassword, "-dname", "CN=Android Debug,O=Android,C=US"], root));
        }

        steps.Add(new("Sign (apksigner, debug key)", Path.Combine(buildTools, "apksigner.bat"),
            ["sign", "--ks", DebugKeystore, "--ks-key-alias", DebugAlias, "--ks-pass", $"pass:{DebugPassword}",
             "--key-pass", $"pass:{DebugPassword}", "--out", signed, aligned], root));

        CommandStep? run = null;
        if (Adb is { } adb)
        {
            var package = ReadPackageName(Path.Combine(root, "AndroidManifest.xml"));
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var script = $"& {Quote(adb)} install -r {Quote(signed)}; if ($LASTEXITCODE) {{ Write-Error 'adb install failed - is a device or emulator connected (adb devices)? If the app is already installed with a different signature, uninstall it first.'; exit $LASTEXITCODE }}";
            if (package is not null)
                script += $"; & {Quote(adb)} shell monkey -p {Quote(package)} -c android.intent.category.LAUNCHER 1 | Out-Null; Write-Output 'Launched {package}'";
            var pwsh = ToolLocator.Resolve("pwsh", "powershell") ?? "powershell";
            run = new("Install + launch on device (adb)", pwsh, ["-NoProfile", "-Command", script], root);
        }

        return new BuildPlan("Android (apktool)", steps, run,
            $"apktool rebuild → zipalign → apksigner{(run is null ? "" : " → adb install")} · {name}", signed);
    }

    private static string? ReadPackageName(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        var match = PackageRegex().Match(File.ReadAllText(manifest));
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<manifest\b[^>]*\bpackage=""([^""]+)""")]
    private static partial Regex PackageRegex();
}
