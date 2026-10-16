using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MyCompiler.Models;
using MyCompiler.Services;

namespace MyCompiler;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "target", "dist", "build", ".venv", "venv", "packages"
    };

    private readonly CompilerEngine _compiler = new();
    private readonly DecompilerEngine _decompiler = new();
    private readonly GitHubService _github = new();
    private readonly ProcessRunner _runner = new();
    private readonly List<SourceFileItem> _allFiles = [];
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _trendingCts;
    private string? _currentFile;
    private string? _workspaceRoot;
    private string _trendingLanguage = "all";
    private string _trendingSince = "daily";
    private bool _isDirty;
    private bool _suppressEditorEvents;
    private bool _isLoadingFile;

    public ObservableCollection<SourceFileItem> Files { get; } = [];
    public ObservableCollection<ToolchainStatus> Toolchains { get; } = [];
    public ObservableCollection<GitHubRepoItem> TrendingRepos { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadScratchDocument();
        _ = Task.Run(CleanOldScratchFolders);
        SourceEditor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(SourceEditor_ScrollChanged));
        Loaded += async (_, _) =>
        {
            UpdateLineNumbers();
            await RefreshToolchainsAsync();
        };
    }

    private void LoadScratchDocument()
    {
        _suppressEditorEvents = true;
        SourceEditor.Text = """
            using System;

            Console.WriteLine("Hello from MyCompiler!");
            Console.WriteLine($"Built at {DateTime.Now:T}");
            """;
        _suppressEditorEvents = false;
        _currentFile = null;
        _isScratchFile = false;
        SetDirty(false);
        UpdateDocumentChrome();
        UpdateLineNumbers();
        SourceEditor.Focus();
        SourceEditor.CaretIndex = SourceEditor.Text.Length;
    }

    private async Task RefreshToolchainsAsync()
    {
        ToolchainSummaryText.Text = "Scanning this PC…";
        var statuses = (await _compiler.ScanToolchainsAsync()).ToList();
        statuses.AddRange(await Task.Run(_decompiler.ScanTools));
        Toolchains.Clear();
        foreach (var status in statuses)
            Toolchains.Add(status);
        var available = statuses.Count(status => status.IsAvailable);
        ToolchainSummaryText.Text = $"{available} of {statuses.Count} ready";
    }

    private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open source file",
            Filter = "Source files|*.cs;*.fs;*.vb;*.c;*.cpp;*.rs;*.go;*.java;*.kt;*.js;*.ts;*.py;*.rb;*.php;*.ps1;*.sh;*.lua;*.r;*.swift;*.zig;*.dart;*.pas;*.nim;*.d;*.hs;*.ml;*.scala;*.ex;*.exs|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true && await ResolveUnsavedChangesAsync())
            await LoadFileAsync(dialog.FileName);
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Open a code workspace", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            await OpenWorkspaceAsync(dialog.FolderName);
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        if (!Directory.Exists(path)) return;
        _workspaceRoot = Path.GetFullPath(path);
        WorkspaceNameText.Text = Path.GetFileName(_workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        AppStatusText.Text = "Indexing workspace…";
        var indexed = await Task.Run(() => IndexWorkspace(_workspaceRoot));
        _allFiles.Clear();
        _allFiles.AddRange(indexed);
        ApplyExplorerFilter();
        FileCountText.Text = $"{_allFiles.Count:N0} source files";
        AppStatusText.Text = "Workspace ready";
    }

    private List<SourceFileItem> IndexWorkspace(string root)
    {
        var sourceExtensions = _compiler.Languages.SelectMany(language => language.Extensions).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sourceExtensions.UnionWith([".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".json", ".toml", ".xml", ".gradle", ".txt", ".smali"]);
        var specialNames = new HashSet<string>(["Makefile", "CMakeLists.txt", "go.mod", "package.json", "Cargo.toml", "pom.xml"], StringComparer.OrdinalIgnoreCase);
        var result = new List<SourceFileItem>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && result.Count < 20000)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                        pending.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (sourceExtensions.Contains(Path.GetExtension(file)) || specialNames.Contains(Path.GetFileName(file)))
                    {
                        result.Add(new SourceFileItem
                        {
                            FullPath = file,
                            RelativePath = Path.GetRelativePath(root, file)
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip protected directories.
            }
            catch (IOException)
            {
                // Skip transient or unavailable directories.
            }
        }
        return result.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async void SourceExplorerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingFile || SourceExplorerList.SelectedItem is not SourceFileItem item ||
            string.Equals(item.FullPath, _currentFile, StringComparison.OrdinalIgnoreCase))
            return;

        if (!await ResolveUnsavedChangesAsync())
        {
            SourceExplorerList.SelectedItem = _allFiles.FirstOrDefault(file => file.FullPath == _currentFile);
            return;
        }
        await LoadFileAsync(item.FullPath);
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            _isLoadingFile = true;
            var text = await File.ReadAllTextAsync(path);
            _suppressEditorEvents = true;
            SourceEditor.Text = text;
            _suppressEditorEvents = false;
            _currentFile = Path.GetFullPath(path);
            _isScratchFile = false;
            if (_workspaceRoot is null || !_currentFile.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
                _workspaceRoot = Path.GetDirectoryName(_currentFile);
            SetDirty(false);
            UpdateDocumentChrome();
            UpdateLineNumbers();
            SourceEditor.CaretIndex = 0;
            SourceEditor.Focus();
            AppStatusText.Text = "File opened";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open file", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isLoadingFile = false;
        }
    }

    private async void NewFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ResolveUnsavedChangesAsync())
            LoadScratchDocument();
    }

    /// <summary>Start fresh: clear the console, close the workspace and reset the editor — no restart needed.</summary>
    private async void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts is not null || !await ResolveUnsavedChangesAsync()) return;
        _workspaceRoot = null;
        WorkspaceNameText.Text = "No folder open";
        _allFiles.Clear();
        ApplyExplorerFilter();
        FileCountText.Text = "0 source files";
        LoadScratchDocument();
        OutputBox.Clear();
        AppStatusText.Text = "Ready";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveAndRunAsync();

    /// <summary>Save, then immediately build + run (run-on-save).</summary>
    private async Task SaveAndRunAsync()
    {
        if (await SaveCurrentAsync()) await BuildAsync(runAfterBuild: true);
    }

    private static readonly TimeSpan ScratchRetention = TimeSpan.FromDays(3);

    private static void CleanOldScratchFolders()
    {
        try
        {
            if (!Directory.Exists(ScratchRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(ScratchRoot))
            {
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(dir) > ScratchRetention)
                        Directory.Delete(dir, recursive: true);
                }
                catch { /* in use or locked — try again next launch */ }
            }
        }
        catch { }
    }

    /// <summary>Best-effort guess of the language of an untitled buffer, so the right toolchain is picked.</summary>
    private static string ScratchFileName(string code)
    {
        var extension = GuessExtension(code);
        // javac requires the file name to match the public class.
        if (extension == ".java" && Regex.Match(code, @"\bpublic\s+(?:final\s+)?class\s+(\w+)") is { Success: true } match)
            return match.Groups[1].Value + extension;
        return "main" + extension;
    }

    private static string GuessExtension(string code)
    {
        bool Has(string pattern) => Regex.IsMatch(code, pattern, RegexOptions.Multiline);
        if (Has(@"^\s*#include\s*[<""]")) return ".cpp";
        if (Has(@"\bfn\s+main\s*\(")) return ".rs";
        if (Has(@"^\s*package\s+main\b") && Has(@"\bfunc\s+\w+\s*\(")) return ".go";
        if (Has(@"\bfun\s+main\s*\(")) return ".kt";
        if (Has(@"\bpublic\s+static\s+void\s+main\s*\(\s*String")) return ".java";
        if (Has(@"\bConsole\.|^\s*using\s+System|\bnamespace\s+\w")) return ".cs";
        if (Has(@"^\s*(def\s+\w+\s*\(.*\)\s*:|import\s+\w+\s*$|from\s+\w+(\.\w+)*\s+import\b|if\s+__name__\s*==)") || Has(@"^\s*print\(")) return ".py";
        if (Has(@":\s*(string|number|boolean)\b|^\s*interface\s+\w+\s*\{")) return ".ts";
        if (Has(@"\bconsole\.log\(|\bfunction\s+\w+\s*\(|\brequire\(|=>")) return ".js";
        return ".cs";
    }

    private static readonly string ScratchRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "Scratch");

    private bool _isScratchFile;

    /// <summary>
    /// Writes an untitled document to %LOCALAPPDATA%\MyCompiler\Scratch\{session}\main.cs without any dialog.
    /// The tab stays "unsaved" (Save / close still offer a real location), so nothing is silently lost.
    /// </summary>
    private async Task<bool> SaveScratchAsync()
    {
        try
        {
            var fileName = ScratchFileName(SourceEditor.Text);
            if (_currentFile is null)
            {
                var folder = Path.Combine(ScratchRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(folder);
                _currentFile = Path.Combine(folder, fileName);
                _isScratchFile = true;
            }
            else if (!Path.GetFileName(_currentFile).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                // The buffer changed language since the last run — rename so the right compiler is used.
                var renamed = Path.Combine(Path.GetDirectoryName(_currentFile)!, fileName);
                File.Delete(_currentFile);
                _currentFile = renamed;
            }
            await File.WriteAllTextAsync(_currentFile, SourceEditor.Text, new UTF8Encoding(false));
            UpdateDocumentChrome();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not prepare scratch file", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task<bool> SaveCurrentAsync()
    {
        if (_currentFile is null || _isScratchFile)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save source file",
                FileName = ScratchFileName(SourceEditor.Text),
                Filter = "C# source|*.cs|C++ source|*.cpp|Rust source|*.rs|Go source|*.go|Java source|*.java|Python source|*.py|JavaScript|*.js|TypeScript|*.ts|All files|*.*"
            };
            if (dialog.ShowDialog(this) != true) return false;
            _currentFile = dialog.FileName;
            _isScratchFile = false;
            _workspaceRoot ??= Path.GetDirectoryName(_currentFile);
        }

        try
        {
            await File.WriteAllTextAsync(_currentFile, SourceEditor.Text, new UTF8Encoding(false));
            SetDirty(false);
            UpdateDocumentChrome();
            AppStatusText.Text = "Saved";
            AddOrRefreshExplorerFile(_currentFile);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save file", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void AddOrRefreshExplorerFile(string path)
    {
        if (_allFiles.Any(item => item.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        var root = _workspaceRoot ?? Path.GetDirectoryName(path)!;
        _allFiles.Add(new SourceFileItem { FullPath = path, RelativePath = Path.GetRelativePath(root, path) });
        _allFiles.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        ApplyExplorerFilter();
        FileCountText.Text = $"{_allFiles.Count:N0} source files";
    }

    private async Task<bool> ResolveUnsavedChangesAsync()
    {
        if (!_isDirty) return true;
        var choice = MessageBox.Show(this, "Save your changes before continuing?", "Unsaved changes",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice switch
        {
            MessageBoxResult.Yes => await SaveCurrentAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e) => await BuildAsync(runAfterBuild: false);
    private async void RunButton_Click(object sender, RoutedEventArgs e) => await BuildAsync(runAfterBuild: true);

    private async Task BuildAsync(bool runAfterBuild)
    {
        if (_operationCts is not null) return;

        BuildPlan plan;
        try
        {
            // An open workspace (e.g. just decompiled) wins unless the active tab is a real file inside it.
            var fileInWorkspace = _currentFile is not null && !_isScratchFile && _workspaceRoot is not null
                && Path.GetFullPath(_currentFile).StartsWith(Path.GetFullPath(_workspaceRoot).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            var workspacePlan = _workspaceRoot is not null && !fileInWorkspace
                ? _compiler.CreateWorkspacePlan(_workspaceRoot)
                : null;

            if (workspacePlan is not null)
            {
                plan = workspacePlan;
            }
            else if (!fileInWorkspace && IsNativeDecompile(_workspaceRoot))
            {
                OutputBox.Clear();
                AppendOutput("MyCompiler: this is a decompiled native (C/C++) program.");
                AppendOutput("Ghidra's C output is for reading only — it can't be rebuilt into a working .exe.");
                AppendOutput("To rebuild a native app you need its original source code.");
                SetOutputState(false, "Not rebuildable");
                return;
            }
            else
            {
                if (_currentFile is null || _isScratchFile)
                {
                    // Unsaved tab: build from an auto-saved scratch copy instead of prompting for a location.
                    if (!await SaveScratchAsync()) return;
                }
                else if (_isDirty)
                {
                    if (!await SaveCurrentAsync()) return;
                }
                plan = _compiler.CreatePlan(_currentFile!, _workspaceRoot);
            }
        }
        catch (Exception ex)
        {
            SetOutputState(false, "Build could not start");
            AppendOutput($"MyCompiler: {ex.Message}");
            return;
        }

        OutputBox.Clear();
        AppendOutput($"MyCompiler 1.3  ·  {plan.Summary}");
        AppendOutput(new string('─', 70));
        LanguageBadgeText.Text = plan.Language;
        _operationCts = new CancellationTokenSource();
        SetBusy(true, runAfterBuild ? "Building and running…" : "Building…");
        var started = DateTimeOffset.UtcNow;

        try
        {
            foreach (var step in plan.BuildSteps)
            {
                var success = await ExecuteStepAsync(step, _operationCts.Token);
                if (!success)
                {
                    SetOutputState(false, "Build failed");
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(plan.ArtifactPath))
            {
                AppendOutput(string.Empty);
                AppendOutput($"★ {Path.GetExtension(plan.ArtifactPath).TrimStart('.').ToUpperInvariant()} ready: {plan.ArtifactPath}");
            }

            if (runAfterBuild && plan.RunStep is not null)
            {
                AppendOutput(string.Empty);
                var success = await ExecuteStepAsync(plan.RunStep, _operationCts.Token);
                if (!success)
                {
                    SetOutputState(false, "Program exited with an error");
                    return;
                }
            }
            else if (runAfterBuild && plan.RunStep is null)
            {
                AppendOutput("Build succeeded. This project recipe does not define a default executable.");
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            AppendOutput(string.Empty);
            AppendOutput($"✓ Completed successfully in {elapsed.TotalSeconds:0.00}s");
            SetOutputState(true, runAfterBuild ? "Run completed" : "Build succeeded");
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, AppStatusText.Text);
        }
    }

    private async Task<bool> ExecuteStepAsync(CommandStep step, CancellationToken token)
    {
        AppendOutput($"› {step.Caption}");
        AppendOutput($"  {FormatCommand(step)}");
        var result = await _runner.RunAsync(step, (line, isError) =>
            Dispatcher.BeginInvoke(() => AppendOutput(isError ? $"! {line}" : line)), token);
        if (result.WasCancelled)
        {
            AppendOutput("■ Stopped by user.");
            SetOutputState(false, "Stopped");
            return false;
        }
        if (result.ExitCode != 0)
        {
            AppendOutput($"✕ Exit code {result.ExitCode} · {result.Duration.TotalSeconds:0.00}s");
            return false;
        }
        AppendOutput($"✓ {step.Caption} · {result.Duration.TotalSeconds:0.00}s");
        return true;
    }

    private async void DecompileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Decompile an executable or package",
            Filter = "Binaries|*.exe;*.dll;*.apk;*.aab;*.xapk;*.dex;*.jar;*.class|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            await DecompileAsync(dialog.FileName);
    }

    private static bool IsNativeDecompile(string? root)
    {
        try
        {
            var info = root is null ? null : Path.Combine(root, "_DECOMPILED.txt");
            return info is not null && File.Exists(info)
                && File.ReadAllText(info).Contains(DecompilerEngine.Describe(BinaryKind.Native), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private async Task DecompileAsync(string path)
    {
        if (_operationCts is not null) return;
        BinaryKind kind;
        try { kind = DecompilerEngine.Detect(path); } catch { kind = BinaryKind.Unknown; }
        if (kind == BinaryKind.Native)
        {
            var answer = MessageBox.Show(this,
                $"{Path.GetFileName(path)} is a native Windows program (C, C++, Rust, Go…).\n\n" +
                "These can't be turned back into a rebuildable project. Ghidra can only produce approximate C " +
                "for reading (function names, logic) — Build won't work on it.\n\nDecompile anyway, for reading only?",
                "Native program — read-only", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        OutputBox.Clear();
        AppendOutput($"MyCompiler 1.3  ·  Decompile {Path.GetFileName(path)}");
        AppendOutput(new string('─', 70));
        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        SetBusy(true, "Decompiling…");
        DecompileButton.IsEnabled = false;
        var started = DateTimeOffset.UtcNow;
        try
        {
            var result = await _decompiler.RunAsync(path, step => ExecuteStepAsync(step, token), AppendOutput, token);
            AppendOutput(string.Empty);
            if (token.IsCancellationRequested)
            {
                SetOutputState(false, "Stopped");
                return;
            }
            if (!result.Success)
            {
                SetOutputState(false, "Decompile failed");
                return;
            }
            AppendOutput($"★ {result.Summary}");
            AppendOutput($"✓ Completed in {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0}s · opening output as workspace");
            SetOutputState(true, "Decompiled");
            await OpenWorkspaceAsync(result.OutputDirectory);
        }
        catch (Exception ex)
        {
            AppendOutput($"✕ {ex.Message}");
            SetOutputState(false, "Decompile failed");
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            DecompileButton.IsEnabled = true;
            SetBusy(false, AppStatusText.Text);
        }
    }

    private async void RunCustomCommandButton_Click(object sender, RoutedEventArgs e) => await RunCustomCommandAsync();

    private async Task RunCustomCommandAsync()
    {
        if (_operationCts is not null || string.IsNullOrWhiteSpace(CustomCommandBox.Text)) return;
        var tokens = SplitCommandLine(CustomCommandBox.Text);
        if (tokens.Count == 0) return;
        var working = _currentFile is not null ? Path.GetDirectoryName(_currentFile)! : _workspaceRoot ?? Environment.CurrentDirectory;
        var step = new CommandStep("Custom command", tokens[0], tokens.Skip(1).ToArray(), working);
        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Running custom command…");
        AppendOutput(string.Empty);
        try
        {
            var ok = await ExecuteStepAsync(step, _operationCts.Token);
            SetOutputState(ok, ok ? "Command completed" : "Command failed");
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, AppStatusText.Text);
        }
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            if (character == '\\' && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
            {
                current.Append('"');
                index++;
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();
    private async void RefreshToolchainsButton_Click(object sender, RoutedEventArgs e) => await RefreshToolchainsAsync();

    private async void PathDoctorButton_Click(object sender, RoutedEventArgs e)
    {
        if (Toolchains.Count == 0)
            await RefreshToolchainsAsync();
        OutputBox.Text = _compiler.CreatePathReport(Toolchains);
        OutputBox.ScrollToHome();
        var missing = Toolchains.Count(toolchain => !toolchain.IsAvailable);
        SetOutputState(missing == 0, missing == 0 ? "All toolchains ready" : $"PATH report · {missing} toolchains missing");
    }

    private void CopyToolchainCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string command } || string.IsNullOrWhiteSpace(command))
            return;
        Clipboard.SetText(command);
        OutputSummaryText.Text = command.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "Setup guide copied"
            : "PowerShell command copied";
        AppStatusText.Text = OutputSummaryText.Text;
    }

    private void SourceEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLineNumbers();
        if (!_suppressEditorEvents) SetDirty(true);
    }

    private void UpdateLineNumbers()
    {
        var count = 1 + (SourceEditor?.Text.Count(character => character == '\n') ?? 0);
        LineNumbersText.Text = string.Join(Environment.NewLine, Enumerable.Range(1, count));
    }

    private void SourceEditor_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer)
            LineNumberScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void SourceEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var line = Math.Max(0, SourceEditor.GetLineIndexFromCharacterIndex(SourceEditor.CaretIndex));
        var lineStart = SourceEditor.GetCharacterIndexFromLineIndex(line);
        CursorStatusText.Text = $"Ln {line + 1}, Col {SourceEditor.CaretIndex - lineStart + 1}";
    }

    private void ExplorerSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyExplorerFilter();

    private void ApplyExplorerFilter()
    {
        if (Files is null || ExplorerSearchBox is null) return;
        var query = ExplorerSearchBox.Text.Trim();
        Files.Clear();
        foreach (var item in _allFiles.Where(item => query.Length == 0 || item.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Files.Add(item);
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SourceEditor.Focus();
            e.Handled = true;
        }
    }

    private void FindNext()
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        var start = Math.Min(SourceEditor.Text.Length, SourceEditor.SelectionStart + SourceEditor.SelectionLength);
        var index = SourceEditor.Text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = SourceEditor.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            SourceEditor.Select(index, query.Length);
            SourceEditor.Focus();
        }
    }

    private void CustomCommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = RunCustomCommandAsync();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            _ = SaveAndRunAsync();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            NewFileButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            OpenFileButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = BuildAsync(true);
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            _ = BuildAsync(false);
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && GitHubOverlay.Visibility == Visibility.Visible)
        {
            GitHubOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _operationCts is not null)
        {
            _operationCts.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private async void GitHubHubButton_Click(object sender, RoutedEventArgs e)
    {
        GitHubOverlay.Visibility = Visibility.Visible;
        GitHubUrlBox.Focus();
        if (TrendingRepos.Count == 0)
            await RefreshTrendingAsync();
    }

    private void CloseGitHubOverlay_Click(object sender, RoutedEventArgs e) =>
        GitHubOverlay.Visibility = Visibility.Collapsed;

    private void GitHubOverlay_BackdropClick(object sender, MouseButtonEventArgs e) =>
        GitHubOverlay.Visibility = Visibility.Collapsed;

    private void GitHubModalCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private enum GitHubFeedMode { Trending, WindowsApps, DecompRecomp, AntigravityMcp }
    private GitHubFeedMode _feedMode = GitHubFeedMode.Trending;

    private async void TrendingSinceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string since }) return;
        _feedMode = GitHubFeedMode.Trending;
        _trendingSince = since;
        await RefreshCurrentGitHubFeedAsync();
    }

    private async void WindowsAppsFeedButton_Click(object sender, RoutedEventArgs e)
    {
        _feedMode = GitHubFeedMode.WindowsApps;
        await RefreshCurrentGitHubFeedAsync();
    }

    private async void DecompRecompFeedButton_Click(object sender, RoutedEventArgs e)
    {
        _feedMode = GitHubFeedMode.DecompRecomp;
        await RefreshCurrentGitHubFeedAsync();
    }

    private async void AntigravityMcpFeedButton_Click(object sender, RoutedEventArgs e)
    {
        _feedMode = GitHubFeedMode.AntigravityMcp;
        await RefreshCurrentGitHubFeedAsync();
    }

    private async void TrendingLangButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string lang }) return;
        _trendingLanguage = lang;
        await RefreshCurrentGitHubFeedAsync();
    }

    private Task RefreshCurrentGitHubFeedAsync() => _feedMode switch
    {
        GitHubFeedMode.WindowsApps => RefreshWindowsAppsFeedAsync(),
        GitHubFeedMode.DecompRecomp => RefreshDecompRecompFeedAsync(),
        GitHubFeedMode.AntigravityMcp => RefreshAntigravityMcpFeedAsync(),
        _ => RefreshTrendingAsync()
    };

    private async void GitHubSearchButton_Click(object sender, RoutedEventArgs e) =>
        await SearchGitHubReposAsync();

    private async void GitHubSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SearchGitHubReposAsync();
            e.Handled = true;
        }
    }

    private async void GitHubUrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await HandleTopGitHubInputAsync(autoBuildAndRun: true);
            e.Handled = true;
        }
    }

    private async void GitHubFetchOnlyButton_Click(object sender, RoutedEventArgs e) =>
        await HandleTopGitHubInputAsync(autoBuildAndRun: false);

    private async void GitHubInstallAndRunUrlButton_Click(object sender, RoutedEventArgs e) =>
        await HandleTopGitHubInputAsync(autoBuildAndRun: true);

    /// <summary>
    /// If the user typed a search keyword (e.g. "Antigravity" or "N64Recomp") into the top bar instead of a URL or owner/repo,
    /// run a live GitHub search inside the modal rather than closing the modal and failing as a source download.
    /// </summary>
    private async Task HandleTopGitHubInputAsync(bool autoBuildAndRun)
    {
        var input = GitHubUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        if (!GitHubService.IsGitHubUrlOrShorthand(input))
        {
            GitHubSearchBox.Text = input;
            await SearchGitHubReposAsync(input);
            return;
        }

        await ProcessGitHubUrlAsync(input, autoBuildAndRun);
    }

    private async void TrendingCardOpenOnly_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            await ProcessGitHubUrlAsync(url, autoBuildAndRun: false);
    }

    private async void TrendingCardInstallRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            await ProcessGitHubUrlAsync(url, autoBuildAndRun: true);
    }

    private async Task RefreshWindowsAppsFeedAsync()
    {
        _trendingCts?.Cancel();
        _trendingCts = new CancellationTokenSource();
        var token = _trendingCts.Token;

        GitHubModalStatusText.Text = $"Loading runnable Windows desktop apps & tools ({_trendingLanguage})…";
        try
        {
            var items = await _github.GetWindowsAppsFeedAsync(_trendingLanguage, token);
            if (token.IsCancellationRequested) return;
            TrendingRepos.Clear();
            foreach (var item in items)
                TrendingRepos.Add(item);
            GitHubModalStatusText.Text = $"Showing {TrendingRepos.Count} runnable Windows apps & tools ({_trendingLanguage}). Click '⚡ Install & Run' to launch!";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GitHubModalStatusText.Text = $"Could not load Windows Apps feed: {ex.Message}";
        }
    }

    private async Task RefreshDecompRecompFeedAsync()
    {
        _trendingCts?.Cancel();
        _trendingCts = new CancellationTokenSource();
        var token = _trendingCts.Token;

        GitHubModalStatusText.Text = $"Loading Decomp & Recomp game ports ({_trendingLanguage})…";
        try
        {
            var items = await _github.GetDecompRecompFeedAsync(_trendingLanguage, token);
            if (token.IsCancellationRequested) return;
            TrendingRepos.Clear();
            foreach (var item in items)
                TrendingRepos.Add(item);
            GitHubModalStatusText.Text = $"Showing {TrendingRepos.Count} Decompilation & Static Recompilation game ports ({_trendingLanguage}).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GitHubModalStatusText.Text = $"Could not load Decomp/Recomp feed: {ex.Message}";
        }
    }

    private async Task RefreshAntigravityMcpFeedAsync()
    {
        _trendingCts?.Cancel();
        _trendingCts = new CancellationTokenSource();
        var token = _trendingCts.Token;

        GitHubModalStatusText.Text = $"Loading Antigravity skills, plugins & MCP servers ({_trendingLanguage})…";
        try
        {
            var items = await _github.GetAntigravityMcpFeedAsync(_trendingLanguage, token);
            if (token.IsCancellationRequested) return;
            TrendingRepos.Clear();
            foreach (var item in items)
                TrendingRepos.Add(item);
            GitHubModalStatusText.Text = $"Showing {TrendingRepos.Count} Antigravity skills & MCP repositories ({_trendingLanguage}).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GitHubModalStatusText.Text = $"Could not load Antigravity/MCP feed: {ex.Message}";
        }
    }

    private async Task RefreshTrendingAsync()
    {
        _trendingCts?.Cancel();
        _trendingCts = new CancellationTokenSource();
        var token = _trendingCts.Token;

        GitHubModalStatusText.Text = $"Loading GitHub Trending ({_trendingLanguage}, {_trendingSince})…";
        try
        {
            var items = await _github.GetTrendingAsync(_trendingLanguage, _trendingSince, token);
            if (token.IsCancellationRequested) return;
            TrendingRepos.Clear();
            foreach (var item in items)
                TrendingRepos.Add(item);
            GitHubModalStatusText.Text = $"Showing {TrendingRepos.Count} trending repositories ({_trendingLanguage} · {_trendingSince}). Try '★ Windows Apps', '🎮 Decomp / Recomp', or '🤖 Antigravity / MCP'!";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GitHubModalStatusText.Text = $"Could not load Trending: {ex.Message}";
        }
    }

    private async Task SearchGitHubReposAsync(string? overrideQuery = null)
    {
        var query = (overrideQuery ?? GitHubSearchBox.Text).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            var topInput = GitHubUrlBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(topInput) && !GitHubService.IsGitHubUrlOrShorthand(topInput))
            {
                query = topInput;
                GitHubSearchBox.Text = query;
            }
            else
            {
                await RefreshCurrentGitHubFeedAsync();
                return;
            }
        }

        _trendingCts?.Cancel();
        _trendingCts = new CancellationTokenSource();
        var token = _trendingCts.Token;

        GitHubModalStatusText.Text = $"Searching GitHub for '{query}'…";
        try
        {
            var items = await _github.SearchRepositoriesAsync(query, _trendingLanguage, token);
            if (token.IsCancellationRequested) return;
            TrendingRepos.Clear();
            foreach (var item in items)
                TrendingRepos.Add(item);
            GitHubModalStatusText.Text = $"Found {TrendingRepos.Count} repositories matching '{query}'.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GitHubModalStatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async Task ProcessGitHubUrlAsync(string? rawUrl, bool autoBuildAndRun)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || _operationCts is not null)
            return;

        if (!await ResolveUnsavedChangesAsync())
            return;

        GitHubOverlay.Visibility = Visibility.Collapsed;
        OutputBox.Clear();
        AppendOutput($"MyCompiler 1.4  ·  GitHub Automated Pipeline");
        AppendOutput($"Source: {rawUrl.Trim()}");
        AppendOutput(new string('─', 70));

        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        SetBusy(true, "Fetching from GitHub…");
        var started = DateTimeOffset.UtcNow;

        try
        {
            var (downloadedPath, isSingleFile, repoFullName) = await _github.DownloadAsync(rawUrl, AppendOutput, token);
            if (token.IsCancellationRequested)
            {
                SetOutputState(false, "Cancelled");
                return;
            }

            if (isSingleFile)
            {
                AppendOutput($"✓ Downloaded single file: {Path.GetFileName(downloadedPath)}");
                await LoadFileAsync(downloadedPath);

                var ext = Path.GetExtension(downloadedPath).ToLowerInvariant();
                if (_compiler.DetectLanguage(downloadedPath) is null)
                {
                    AppendOutput($"ℹ '{Path.GetFileName(downloadedPath)}' ({ext}) is a non-executable document or asset. Opened in editor for reading.");
                    LanguageBadgeText.Text = "Document / Asset";
                    SetOutputState(true, "Opened non-buildable file");
                    return;
                }

                if (autoBuildAndRun)
                {
                    AppendOutput(string.Empty);
                    AppendOutput("› Auto-building & running single source file…");
                    _operationCts.Dispose();
                    _operationCts = null;
                    await BuildAsync(runAfterBuild: true);
                }
                else
                {
                    SetOutputState(true, "GitHub file loaded");
                }
                return;
            }

            // Full repository or multi-file Gist downloaded
            await OpenWorkspaceAsync(downloadedPath);

            var classification = await Task.Run(() => GitHubService.ClassifyWorkspace(downloadedPath, _compiler), token);
            AppendOutput(string.Empty);
            AppendOutput($"★ Classification: [{classification.BadgeText}]");
            AppendOutput($"  {classification.Summary}");
            LanguageBadgeText.Text = classification.BadgeText;

            if (classification.EntryPath is not null && File.Exists(classification.EntryPath))
                await LoadFileAsync(classification.EntryPath);

            if (!autoBuildAndRun)
            {
                SetOutputState(true, $"Loaded · {classification.BadgeText}");
                return;
            }

            // Automated handling based on RepoKind
            if (classification.Kind == RepoKind.DocsOrData)
            {
                AppendOutput(string.Empty);
                AppendOutput("✓ Non-executable repository detected (Docs / Curated List / Skills).");
                AppendOutput("  No build step is needed — opened README in editor so you can browse right away.");
                SetOutputState(true, "Opened Docs / Non-App Repo");
                return;
            }

            if (classification.Kind == RepoKind.WebFrontend && classification.EntryPath is not null)
            {
                AppendOutput(string.Empty);
                AppendOutput($"› Launching web frontend in default browser: {classification.EntryPath}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = classification.EntryPath,
                    UseShellExecute = true
                });
                SetOutputState(true, "Launched Web App");
                return;
            }

            var executedSuccessfully = false;

            // FAST-PATH FOR WINDOWS APPS: Check if the repository publishes an official Windows Release binary (.exe / .zip) FIRST!
            // This guarantees complex Windows apps launch immediately without waiting on missing C++/Rust/SDK toolchains.
            if (classification.Kind == RepoKind.ExecutableApp && !string.IsNullOrWhiteSpace(repoFullName))
            {
                AppendOutput(string.Empty);
                AppendOutput("› Checking GitHub Releases for an official prebuilt Windows binary…");
                var releaseExe = await _github.TryDownloadWindowsReleaseBinaryAsync(repoFullName, downloadedPath, AppendOutput, token);
                if (releaseExe is not null && File.Exists(releaseExe))
                {
                    AppendOutput($"★ Launching Windows release executable: {Path.GetFileName(releaseExe)}");
                    var runReleaseStep = new CommandStep($"Run {Path.GetFileName(releaseExe)}", releaseExe, [], Path.GetDirectoryName(releaseExe)!);
                    executedSuccessfully = await ExecuteStepAsync(runReleaseStep, token);
                }
                else
                {
                    AppendOutput("  No prebuilt Windows binary in latest release — building from downloaded source code…");
                }
            }

            // Build & run from source if no prebuilt binary was run
            if (!executedSuccessfully && classification.Plan is not null)
            {
                AppendOutput(string.Empty);
                AppendOutput($"› Starting automated {classification.Plan.Summary}…");
                executedSuccessfully = true;

                foreach (var step in classification.Plan.BuildSteps)
                {
                    if (!await ExecuteStepAsync(step, token))
                    {
                        executedSuccessfully = false;
                        break;
                    }
                }

                if (executedSuccessfully && classification.Plan.RunStep is not null)
                {
                    AppendOutput(string.Empty);
                    executedSuccessfully = await ExecuteStepAsync(classification.Plan.RunStep, token);
                }
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            AppendOutput(string.Empty);
            if (executedSuccessfully)
            {
                AppendOutput($"✓ GitHub automated pipeline completed in {elapsed.TotalSeconds:0.00}s");
                SetOutputState(true, classification.Kind == RepoKind.LibraryOrSdk ? "Library / Tool verified" : "GitHub App ran");
            }
            else if (classification.Kind == RepoKind.LibraryOrSdk && classification.Plan is null)
            {
                AppendOutput("✓ Library workspace indexed and opened for inspection.");
                SetOutputState(true, "Library opened");
            }
            else
            {
                SetOutputState(false, "GitHub build failed — workspace open in editor");
            }
        }
        catch (Exception ex)
        {
            AppendOutput($"✕ {ex.Message}");
            SetOutputState(false, "GitHub fetch/build error");
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, AppStatusText.Text);
        }
    }

    private void SetDirty(bool dirty)
    {
        _isDirty = dirty;
        DirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        UpdateDocumentChrome();
    }

    private void UpdateDocumentChrome()
    {
        var name = _currentFile is null ? "welcome.cs" : Path.GetFileName(_currentFile);
        EditorTabText.Text = name + (_isDirty ? "  •" : string.Empty);
        TitlePathText.Text = _currentFile ?? "Untitled · save to build";
        LanguageBadgeText.Text = _compiler.DetectLanguage(_currentFile ?? "welcome.cs")?.DisplayName ?? "Custom";
    }

    private void SetBusy(bool busy, string status)
    {
        BuildButton.IsEnabled = !busy;
        RunButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        AppStatusText.Text = status;
        AppStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(busy ? "#FFB85C" : "#4AE28D"));
    }

    private void SetOutputState(bool success, string summary)
    {
        OutputSummaryText.Text = summary;
        OutputStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#45E18D" : "#FF6B7A"));
        AppStatusText.Text = summary;
    }

    private void AppendOutput(string text)
    {
        OutputBox.AppendText(text + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    private static string FormatCommand(CommandStep command) =>
        $"{Path.GetFileName(command.Executable)} {string.Join(" ", command.Arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument))}";

    private void CopyOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(OutputBox.Text)) Clipboard.SetText(OutputBox.Text);
    }

    private void ClearOutputButton_Click(object sender, RoutedEventArgs e)
    {
        OutputBox.Clear();
        OutputSummaryText.Text = "Ready";
        OutputStatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5B6980"));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var hasFile = e.Data.GetDataPresent(DataFormats.FileDrop);
        var hasText = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effects = (hasFile || hasText) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            var path = paths[0];
            if (Directory.Exists(path))
                await OpenWorkspaceAsync(path);
            else if (File.Exists(path) && DecompilerEngine.BinaryExtensions.Contains(Path.GetExtension(path)))
                await DecompileAsync(path);
            else if (File.Exists(path) && await ResolveUnsavedChangesAsync())
                await LoadFileAsync(path);
            return;
        }

        var droppedText = (e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData(DataFormats.Text) as string)?.Trim();
        if (!string.IsNullOrWhiteSpace(droppedText) &&
            (droppedText.Contains("github.com", StringComparison.OrdinalIgnoreCase) ||
             droppedText.Contains("githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            await ProcessGitHubUrlAsync(droppedText, autoBuildAndRun: true);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private bool _isFullScreen;
    private WindowState _preFullScreenState = WindowState.Normal;

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _preFullScreenState = WindowState;
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            OuterChromeBorder.CornerRadius = new CornerRadius(0);
            OuterChromeBorder.BorderThickness = new Thickness(0);
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullScreen = true;
            FullScreenButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7DEBFF"));
            AppStatusText.Text = "Full Screen (press F11 or Esc to exit)";
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            OuterChromeBorder.CornerRadius = new CornerRadius(10);
            OuterChromeBorder.BorderThickness = new Thickness(1);
            WindowState = _preFullScreenState == WindowState.Minimized ? WindowState.Normal : _preFullScreenState;
            _isFullScreen = false;
            FullScreenButton.ClearValue(Control.ForegroundProperty);
            AppStatusText.Text = "Ready";
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize()
    {
        if (_isFullScreen)
        {
            ToggleFullScreen();
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isDirty)
        {
            var choice = MessageBox.Show(this, "Save your changes before closing?", "MyCompiler",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (choice == MessageBoxResult.Yes)
            {
                var saved = SaveCurrentOnClose();
                if (!saved) e.Cancel = true;
            }
        }
        if (!e.Cancel) _operationCts?.Cancel();
    }

    private bool SaveCurrentOnClose()
    {
        if (_currentFile is null || _isScratchFile)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save source file",
                FileName = "main.cs",
                Filter = "C# source|*.cs|All files|*.*"
            };
            if (dialog.ShowDialog(this) != true) return false;
            _currentFile = dialog.FileName;
            _isScratchFile = false;
        }

        try
        {
            File.WriteAllText(_currentFile, SourceEditor.Text, new UTF8Encoding(false));
            SetDirty(false);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save file", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}
