using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyCompiler.Models;

namespace MyCompiler.Services;

public static class ProjectService
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ProjectsRoot
    {
        get
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var driveRoot = Path.GetPathRoot(appDir);
            
            // If running from X: or similar USB drive, put Projects directly under X:\MyCompiler\Projects
            if (!string.IsNullOrWhiteSpace(driveRoot) && driveRoot.StartsWith("X", StringComparison.OrdinalIgnoreCase))
            {
                var xProjects = @"X:\MyCompiler\Projects";
                try
                {
                    Directory.CreateDirectory(xProjects);
                    return xProjects;
                }
                catch { }
            }

            // Local folder next to app
            var localProjects = Path.Combine(appDir, "Projects");
            if (Directory.Exists(localProjects)) return localProjects;

            // Portable fallback to parent/Projects if running inside bin/Publish
            var parentProjects = Path.GetFullPath(Path.Combine(appDir, "..", "Projects"));
            if (Directory.Exists(parentProjects)) return parentProjects;

            // Standard LocalAppData fallback
            var appDataProjects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "Projects");
            try { Directory.CreateDirectory(appDataProjects); } catch { }
            return appDataProjects;
        }
    }

    private static string GetCatalogPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "projects.json");

    private static string GetSecondaryCatalogPath() =>
        Path.Combine(ProjectsRoot, "projects_index.json");

    public static List<SavedProjectItem> LoadProjects()
    {
        lock (FileLock)
        {
            var primary = GetCatalogPath();
            var secondary = GetSecondaryCatalogPath();

            List<SavedProjectItem>? items = null;

            if (File.Exists(primary))
            {
                try
                {
                    var json = File.ReadAllText(primary);
                    items = JsonSerializer.Deserialize<List<SavedProjectItem>>(json, JsonOpts);
                }
                catch { }
            }

            if ((items == null || items.Count == 0) && File.Exists(secondary))
            {
                try
                {
                    var json = File.ReadAllText(secondary);
                    items = JsonSerializer.Deserialize<List<SavedProjectItem>>(json, JsonOpts);
                }
                catch { }
            }

            items ??= new List<SavedProjectItem>();

            // Auto-discover any folders already sitting inside ProjectsRoot that aren't yet cataloged
            try
            {
                if (Directory.Exists(ProjectsRoot))
                {
                    foreach (var dir in Directory.GetDirectories(ProjectsRoot))
                    {
                        var dirName = Path.GetFileName(dir);
                        if (dirName.StartsWith('.') || dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) || dirName.Equals("obj", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!items.Any(p => p.FullPath.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                        {
                            var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                                .Where(f => !f.Contains("\\.git\\") && !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                                .ToList();

                            items.Add(new SavedProjectItem
                            {
                                Name = dirName,
                                FullPath = dir,
                                Description = "Saved project",
                                Language = DetectPrimaryLanguage(dir),
                                FileCount = files.Count,
                                CreatedAt = Directory.GetCreationTimeUtc(dir),
                                LastOpened = Directory.GetLastWriteTimeUtc(dir)
                            });
                        }
                    }
                }
            }
            catch { }

            return items
                .OrderByDescending(p => p.IsPinned)
                .ThenByDescending(p => p.LastOpened)
                .ToList();
        }
    }

    public static void SaveProjects(IEnumerable<SavedProjectItem> items)
    {
        lock (FileLock)
        {
            var list = items.ToList();
            var json = JsonSerializer.Serialize(list, JsonOpts);

            try
            {
                var p1 = GetCatalogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p1)!);
                File.WriteAllText(p1, json);
            }
            catch { }

            try
            {
                var p2 = GetSecondaryCatalogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p2)!);
                File.WriteAllText(p2, json);
            }
            catch { }
        }
    }

    public static SavedProjectItem RecordWorkspace(string path, string? language = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var isDir = Directory.Exists(fullPath);
        var name = isDir ? Path.GetFileName(fullPath) : Path.GetFileNameWithoutExtension(fullPath);

        var list = LoadProjects();
        var existing = list.FirstOrDefault(p => p.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

        int fileCount = 1;
        if (isDir)
        {
            try
            {
                fileCount = Directory.EnumerateFiles(fullPath, "*.*", SearchOption.AllDirectories)
                    .Count(f => !f.Contains("\\.git\\") && !f.Contains("\\bin\\") && !f.Contains("\\obj\\"));
            }
            catch { }
        }

        if (existing != null)
        {
            existing.LastOpened = DateTimeOffset.UtcNow;
            existing.FileCount = fileCount;
            if (!string.IsNullOrWhiteSpace(language)) existing.Language = language;
            if (!string.IsNullOrWhiteSpace(description)) existing.Description = description;
            SaveProjects(list);
            return existing;
        }

        var item = new SavedProjectItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name,
            FullPath = fullPath,
            Description = description ?? (isDir ? "Workspace project" : "Source file"),
            Language = language ?? (isDir ? DetectPrimaryLanguage(fullPath) : DetectLanguageFromExtension(fullPath)),
            FileCount = fileCount,
            CreatedAt = DateTimeOffset.UtcNow,
            LastOpened = DateTimeOffset.UtcNow
        };

        list.Insert(0, item);
        SaveProjects(list);
        return item;
    }

    public static async Task<SavedProjectItem> SaveAsProjectAsync(
        string projectName,
        string? sourceDir,
        string? activeCode,
        string? activeFileName,
        string? description,
        Action<string> log)
    {
        var safeName = SanitizeFileName(projectName.Trim());
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "MyProject";

        var targetDir = Path.Combine(ProjectsRoot, safeName);
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            var counter = 2;
            while (Directory.Exists($"{targetDir}_{counter}") && Directory.EnumerateFileSystemEntries($"{targetDir}_{counter}").Any())
                counter++;
            targetDir = $"{targetDir}_{counter}";
        }

        Directory.CreateDirectory(targetDir);
        log($"› Creating permanent project at: {targetDir}");

        // If source directory exists and has files, copy all contents
        if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir))
        {
            CopyDirectory(sourceDir, targetDir);
            log($"› Copied workspace files into project.");
        }

        // If active code is provided, ensure the active file in targetDir is updated with latest code
        if (!string.IsNullOrWhiteSpace(activeCode))
        {
            var fileName = !string.IsNullOrWhiteSpace(activeFileName) ? Path.GetFileName(activeFileName) : "main.cs";
            var targetFilePath = Path.Combine(targetDir, fileName);
            await File.WriteAllTextAsync(targetFilePath, activeCode);
            log($"› Saved active editor file: {fileName}");
        }

        // Initialize git in the new project so it is immediately revision-tracked
        try
        {
            RunGitCmd(targetDir, "init");
            RunGitCmd(targetDir, "config user.name \"MyCompiler User\"");
            RunGitCmd(targetDir, "config user.email \"developer@mycompiler.local\"");
            RunGitCmd(targetDir, "add -A");
            RunGitCmd(targetDir, $"commit -m \"Initial commit for project: {safeName}\"");
            log($"› Initialized Git repository.");
        }
        catch (Exception ex)
        {
            log($"› Git init note: {ex.Message}");
        }

        var item = RecordWorkspace(targetDir, description: description ?? "User enhanced project");
        return item;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destinationDir, name), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(subDir);
            if (name is ".git" or "bin" or "obj" or ".vs" or "node_modules" or "__pycache__" or ".idea")
                continue;

            CopyDirectory(subDir, Path.Combine(destinationDir, name));
        }
    }

    private static void RunGitCmd(string workingDir, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(4000);
    }

    public static string DetectPrimaryLanguage(string folder)
    {
        try
        {
            if (Directory.GetFiles(folder, "*.csproj", SearchOption.AllDirectories).Any() ||
                Directory.GetFiles(folder, "*.sln", SearchOption.AllDirectories).Any()) return "C#";
            if (Directory.GetFiles(folder, "Cargo.toml", SearchOption.AllDirectories).Any()) return "Rust";
            if (Directory.GetFiles(folder, "go.mod", SearchOption.AllDirectories).Any()) return "Go";
            if (Directory.GetFiles(folder, "package.json", SearchOption.AllDirectories).Any()) return "TypeScript";
            if (Directory.GetFiles(folder, "CMakeLists.txt", SearchOption.AllDirectories).Any()) return "C++";
            if (Directory.GetFiles(folder, "requirements.txt", SearchOption.AllDirectories).Any() ||
                Directory.GetFiles(folder, "*.py", SearchOption.AllDirectories).Any()) return "Python";
            if (Directory.GetFiles(folder, "*.java", SearchOption.AllDirectories).Any()) return "Java";
        }
        catch { }
        return "C#";
    }

    public static string DetectLanguageFromExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "C#",
            ".py" or ".pyw" => "Python",
            ".rs" => "Rust",
            ".go" => "Go",
            ".cpp" or ".cxx" or ".cc" => "C++",
            ".c" => "C",
            ".ts" or ".tsx" => "TypeScript",
            ".js" or ".jsx" => "JavaScript",
            ".java" => "Java",
            ".kt" => "Kotlin",
            ".swift" => "Swift",
            ".zig" => "Zig",
            _ => "Code"
        };
    }

    private static string SanitizeFileName(string name) =>
        Regex.Replace(name, @"[^\w\-\.]+", "_").Trim('_');
}
