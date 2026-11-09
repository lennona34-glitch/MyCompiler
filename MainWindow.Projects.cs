using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCompiler.Models;
using MyCompiler.Services;

namespace MyCompiler;

public partial class MainWindow
{
    public ObservableCollection<SavedProjectItem> SavedProjects { get; } = new();
    private List<SavedProjectItem> _allSavedProjects = new();

    private void InitializeProjectsModule()
    {
        // Initial silent load of recent projects
        _ = RefreshSavedProjectsAsync(false);
    }

    private void SavedProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenProjectsOverlay();
    }

    private void OpenProjectsOverlay()
    {
        ProjectsOverlay.Visibility = Visibility.Visible;
        if (ProjectsRootLocationText is not null)
            ProjectsRootLocationText.Text = $"Projects Root: {ProjectService.ProjectsRoot}";

        _ = RefreshSavedProjectsAsync(true);
    }

    private void CloseProjectsOverlay()
    {
        ProjectsOverlay.Visibility = Visibility.Collapsed;
    }

    private void CloseProjectsOverlay_Click(object sender, RoutedEventArgs e) =>
        CloseProjectsOverlay();

    private void ProjectsOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == ProjectsOverlay)
            CloseProjectsOverlay();
    }

    private void ProjectsModalCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private async Task RefreshSavedProjectsAsync(bool updateUi)
    {
        var items = await Task.Run(() => ProjectService.LoadProjects());
        _allSavedProjects = items;

        if (updateUi)
        {
            ApplyProjectsFilter();
        }
    }

    private void ApplyProjectsFilter()
    {
        SavedProjects.Clear();
        var q = ProjectsSearchBox?.Text?.Trim().ToLowerInvariant() ?? "";

        var filtered = string.IsNullOrWhiteSpace(q)
            ? _allSavedProjects
            : _allSavedProjects.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Language.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.FullPath.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var item in filtered)
            SavedProjects.Add(item);

        if (ProjectsCountText is not null)
            ProjectsCountText.Text = $"{SavedProjects.Count} project{(SavedProjects.Count == 1 ? "" : "s")}";

        if (ProjectsEmptyState is not null)
            ProjectsEmptyState.Visibility = SavedProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyProjectsFilter();
    }

    private async void ProjectCardOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedProjectItem item }) return;

        if (!Directory.Exists(item.FullPath) && !File.Exists(item.FullPath))
        {
            MessageBox.Show(this, $"The project path does not exist on disk:\n{item.FullPath}", "Project Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseProjectsOverlay();

        if (Directory.Exists(item.FullPath))
        {
            await OpenWorkspaceAsync(item.FullPath);
        }
        else if (File.Exists(item.FullPath))
        {
            await LoadFileAsync(item.FullPath);
        }

        ProjectService.RecordWorkspace(item.FullPath);
        AppendOutput($"Opened saved project: {item.Name} ({item.FullPath})");
    }

    private async void ProjectCardRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedProjectItem item }) return;

        if (!Directory.Exists(item.FullPath) && !File.Exists(item.FullPath))
        {
            MessageBox.Show(this, $"The project path does not exist on disk:\n{item.FullPath}", "Project Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CloseProjectsOverlay();

        if (Directory.Exists(item.FullPath))
        {
            await OpenWorkspaceAsync(item.FullPath);
        }
        else if (File.Exists(item.FullPath))
        {
            await LoadFileAsync(item.FullPath);
        }

        ProjectService.RecordWorkspace(item.FullPath);
        await BuildAsync(runAfterBuild: true);
    }

    private void ProjectCardShowExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedProjectItem item }) return;

        try
        {
            var target = Directory.Exists(item.FullPath) ? item.FullPath : Path.GetDirectoryName(item.FullPath);
            if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{target}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open folder: {ex.Message}", "Explorer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ProjectCardRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedProjectItem item }) return;

        var result = MessageBox.Show(this,
            $"Remove '{item.Name}' from the saved projects list?\n(Files on disk will NOT be deleted)",
            "Remove Project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _allSavedProjects.RemoveAll(p => p.Id == item.Id || p.FullPath.Equals(item.FullPath, StringComparison.OrdinalIgnoreCase));
            ProjectService.SaveProjects(_allSavedProjects);
            ApplyProjectsFilter();
        }
    }

    private void OpenProjectsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = ProjectService.ProjectsRoot;
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{root}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open folder: {ex.Message}", "Explorer Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==========================================
    // Save Project As Dialog
    // ==========================================

    private void SaveCurrentAsProject_Click(object sender, RoutedEventArgs e)
    {
        OpenSaveProjectDialog();
    }

    private void OpenSaveProjectDialog()
    {
        var defaultName = "MyProject";
        if (!string.IsNullOrWhiteSpace(_workspaceRoot) && Directory.Exists(_workspaceRoot))
        {
            defaultName = Path.GetFileName(_workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
            if (defaultName.EndsWith("_Enhanced", StringComparison.OrdinalIgnoreCase))
                defaultName += "_v2";
            else
                defaultName += "_Enhanced";
        }
        else if (!string.IsNullOrWhiteSpace(_currentFile))
        {
            defaultName = Path.GetFileNameWithoutExtension(_currentFile) + "_Project";
        }

        SaveProjectNameBox.Text = defaultName;
        SaveProjectDescBox.Text = "Custom enhanced codebase";
        UpdateSaveProjectDestinationPreview();

        SaveProjectStatusText.Text = "";
        SaveProjectDialogOverlay.Visibility = Visibility.Visible;
        SaveProjectNameBox.Focus();
        SaveProjectNameBox.SelectAll();
    }

    private void CloseSaveProjectDialog()
    {
        SaveProjectDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private void SaveProjectDialog_BackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SaveProjectDialogOverlay)
            CloseSaveProjectDialog();
    }

    private void CancelSaveProject_Click(object sender, RoutedEventArgs e) =>
        CloseSaveProjectDialog();

    private void SaveProjectNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSaveProjectDestinationPreview();
    }

    private void UpdateSaveProjectDestinationPreview()
    {
        if (SaveProjectDestinationPreview is null) return;
        var name = SaveProjectNameBox?.Text?.Trim() ?? "MyProject";
        SaveProjectDestinationPreview.Text = Path.Combine(ProjectService.ProjectsRoot, name);
    }

    private async void ConfirmSaveProject_Click(object sender, RoutedEventArgs e)
    {
        var name = SaveProjectNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SaveProjectStatusText.Text = "Please enter a project name.";
            SaveProjectStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7070"));
            return;
        }

        SaveProjectStatusText.Text = "Saving and copying project files…";
        SaveProjectStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38BDF8"));

        try
        {
            var savedItem = await ProjectService.SaveAsProjectAsync(
                name,
                _workspaceRoot,
                SourceEditor.Text,
                _currentFile,
                SaveProjectDescBox.Text.Trim(),
                AppendOutput);

            CloseSaveProjectDialog();

            // Switch to this new workspace!
            await OpenWorkspaceAsync(savedItem.FullPath);

            AppendOutput($"★ Project '{savedItem.Name}' saved permanently to: {savedItem.FullPath}");
            AppendOutput("★ Initialized Git repository for revision tracking.");

            // Refresh recent list
            _ = RefreshSavedProjectsAsync(false);

            MessageBox.Show(this,
                $"Project successfully saved!\n\nFolder: {savedItem.FullPath}\n\nYour active workspace has been switched to this saved project.",
                "Project Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SaveProjectStatusText.Text = $"Save error: {ex.Message}";
            SaveProjectStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7070"));
        }
    }
}
