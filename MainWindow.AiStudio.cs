using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCompiler.Models;
using MyCompiler.Services;

namespace MyCompiler;

public partial class MainWindow
{
    private AiSettings _aiSettings = new();
    private CancellationTokenSource? _aiCts;
    private string? _aiExtractedCode;
    private string? _aiUndoSnapshot;
    private bool _aiUndoWasSelection;

    private void InitAiStudio()
    {
        try
        {
            _aiSettings = AiService.LoadSettings();

            AiProviderCombo.ItemsSource = AiSettings.Presets.Select(p => p.Name).ToList();
            var presetIndex = AiSettings.Presets.ToList().FindIndex(p => p.Name.Equals(_aiSettings.Provider, StringComparison.OrdinalIgnoreCase));
            AiProviderCombo.SelectedIndex = presetIndex >= 0 ? presetIndex : 0;

            AiApiUrlBox.Text = _aiSettings.ApiUrl;
            AiApiKeyBox.Password = _aiSettings.ApiKey;
            AiApiKeyPlainBox.Text = _aiSettings.ApiKey;
            AiModelBox.Text = _aiSettings.Model;

            var currentPreset = presetIndex >= 0 ? AiSettings.Presets[presetIndex] : AiSettings.Presets[0];
            UpdatePresetKeyUi(currentPreset);

            UpdateAiModelBadge();
        }
        catch { }
    }

    private void UpdateAiModelBadge()
    {
        var model = string.IsNullOrWhiteSpace(_aiSettings.Model) ? "gpt-4o" : _aiSettings.Model;
        var provider = string.IsNullOrWhiteSpace(_aiSettings.Provider) ? "AI" : _aiSettings.Provider;
        AiModelInfoText.Text = $"{provider} · {model}";
    }

    private void AiStudioButton_Click(object sender, RoutedEventArgs e) =>
        ToggleAiStudioOverlay();

    private void ToggleAiStudioOverlay()
    {
        if (AiStudioOverlay.Visibility == Visibility.Visible)
        {
            CloseAiStudioOverlay();
        }
        else
        {
            OpenAiStudioOverlay();
        }
    }

    private void OpenAiStudioOverlay()
    {
        var fileName = _currentFile is not null ? Path.GetFileName(_currentFile) : "welcome.cs";
        var lang = _compiler.DetectLanguage(_currentFile ?? "welcome.cs")?.DisplayName ?? "C#";
        AiTargetFileText.Text = $"📄 {fileName} ({lang})";

        string scopeDesc;
        var hasSelection = SourceEditor.SelectionLength > 0;
        if (hasSelection)
        {
            var lines = SourceEditor.SelectedText.Split('\n').Length;
            AiContextScopeText.Text = "Selected code";
            scopeDesc = $"Selected snippet ({lines} lines, {SourceEditor.SelectedText.Length:N0} chars)";
            AiSelectionStatusText.Text = scopeDesc;
        }
        else
        {
            var lines = SourceEditor.Text.Split('\n').Length;
            AiContextScopeText.Text = "Full file";
            scopeDesc = $"Entire file ({lines} lines, {SourceEditor.Text.Length:N0} chars)";
            AiSelectionStatusText.Text = scopeDesc;
        }

        var outText = OutputBox.Text;
        if (!string.IsNullOrWhiteSpace(outText) && (outText.Contains("error ", StringComparison.OrdinalIgnoreCase) || outText.Contains("Exception", StringComparison.OrdinalIgnoreCase)))
        {
            AiErrorStatusText.Text = "⚠️ Compiler error detected in Output console (auto-injected for repair)";
            AiErrorStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA5A5"));
        }
        else
        {
            AiErrorStatusText.Text = "Build output: Clean (no errors detected)";
            AiErrorStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#52627A"));
        }

        // Display welcoming context card if Response Box is empty or showing previous welcome guide
        if (string.IsNullOrWhiteSpace(AiResponseBox.Text) || AiResponseBox.Text.StartsWith("✨ MyCompiler AI Studio"))
        {
            AiResponseBox.Text = GetAiStudioWelcomeText(fileName, lang, scopeDesc);
            _showingContextPreview = false;
            if (AiPreviewContextButton is not null)
                AiPreviewContextButton.Content = "👁 View Input Code";
        }

        AiStudioOverlay.Visibility = Visibility.Visible;
        AiPromptBox.Focus();

        _ = RefreshGitStatusAsync();
    }

    private string GetAiStudioWelcomeText(string fileName, string lang, string scopeDesc)
    {
        var sampleCode = SourceEditor.SelectionLength > 0
            ? SourceEditor.SelectedText
            : SourceEditor.Text;

        var previewLines = (sampleCode ?? "").Split('\n').Take(12).ToArray();
        var previewSnippet = string.Join("\n", previewLines);
        if ((sampleCode ?? "").Split('\n').Length > 12) previewSnippet += "\n... (more lines in editor)";

        return $"""
            ✨ MyCompiler AI Studio is ready!
            ═══════════════════════════════════════════════════════════════════════════
            🎯 ACTIVE TARGET CONTEXT:
               File:  {fileName} ({lang})
               Scope: {scopeDesc}
               AI Studio automatically reads your active editor code into context.

            📝 ACTIVE CODE PREVIEW:
            ---------------------------------------------------------------------------
            {previewSnippet}
            ---------------------------------------------------------------------------

            🚀 HOW TO SEND INSTRUCTIONS TO LORD GEMINI:
               1. Click any quick prompt chip above:
                  ⚡ Refactor Code   💙 Make Neon Blue   🌙 Give Dark Mode
                  🔤 Modern Font     🐛 Fix Build Error  📖 Explain Code

               2. Or type your custom instruction in the prompt box on the left, e.g.:
                  • "Refactor this open source code following clean, modern idioms"
                  • "Make everything orange in the app neon blue"
                  • "Add async background processing to improve UI responsiveness"
                  • "Change the color palette to cyberpunk glassmorphism"

               3. Click [▶ Generate & Refactor] (or press Ctrl+Enter).

               4. When Gemini finishes, click [⚡ Apply to Code] to inject it!
            ═══════════════════════════════════════════════════════════════════════════
            """;
    }

    private bool _showingContextPreview = false;

    private void AiPreviewContextButton_Click(object sender, RoutedEventArgs e)
    {
        _showingContextPreview = !_showingContextPreview;
        if (_showingContextPreview)
        {
            var fileName = _currentFile is not null ? Path.GetFileName(_currentFile) : "welcome.cs";
            var lang = _compiler.DetectLanguage(_currentFile ?? "welcome.cs")?.DisplayName ?? "C#";
            var code = SourceEditor.SelectionLength > 0 ? SourceEditor.SelectedText : SourceEditor.Text;
            AiResponseBox.Text = $"// === ACTIVE CODE IN EDITOR: {fileName} ({lang}) ===\n\n{code}";
            if (AiPreviewContextButton is not null)
                AiPreviewContextButton.Content = "📄 View AI Response";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_aiExtractedCode))
                AiResponseBox.Text = _aiExtractedCode;
            else
            {
                var fileName = _currentFile is not null ? Path.GetFileName(_currentFile) : "welcome.cs";
                var lang = _compiler.DetectLanguage(_currentFile ?? "welcome.cs")?.DisplayName ?? "C#";
                var scopeDesc = SourceEditor.SelectionLength > 0 ? "Selected code" : "Full file";
                AiResponseBox.Text = GetAiStudioWelcomeText(fileName, lang, scopeDesc);
            }
            if (AiPreviewContextButton is not null)
                AiPreviewContextButton.Content = "👁 View Input Code";
        }
    }

    private void CloseAiStudioOverlay()
    {
        AiStudioOverlay.Visibility = Visibility.Collapsed;
    }

    private void CloseAiStudioOverlay_Click(object sender, RoutedEventArgs e) =>
        CloseAiStudioOverlay();

    private void AiStudioOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == AiStudioOverlay)
            CloseAiStudioOverlay();
    }

    private void AiStudioModalCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        e.Handled = true;

    private void AiSettingsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        AiSettingsPanel.Visibility = AiSettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AiProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiProviderCombo.SelectedIndex < 0 || AiProviderCombo.SelectedIndex >= AiSettings.Presets.Count)
            return;

        var preset = AiSettings.Presets[AiProviderCombo.SelectedIndex];
        if (preset.Name != "Custom")
        {
            AiApiUrlBox.Text = preset.DefaultUrl;
            AiModelBox.Text = preset.DefaultModel;
            AiPingStatusText.Text = preset.Description;
        }

        UpdatePresetKeyUi(preset);
    }

    private void UpdatePresetKeyUi(AiProviderPreset preset)
    {
        if (AiGetKeyLinkButton is not null)
        {
            if (!string.IsNullOrWhiteSpace(preset.ApiKeyUrl))
            {
                AiGetKeyLinkButton.Visibility = Visibility.Visible;
                AiGetKeyLinkButton.Content = preset.Name.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
                    ? "Get Free Gemini Key ↗"
                    : $"Get {preset.Name} Key ↗";
                AiGetKeyLinkButton.Tag = preset.ApiKeyUrl;
            }
            else
            {
                AiGetKeyLinkButton.Visibility = Visibility.Collapsed;
            }
        }

        if (AiEnvDetectedText is not null)
        {
            if (!string.IsNullOrWhiteSpace(preset.EnvVarName))
            {
                var envVal = Environment.GetEnvironmentVariable(preset.EnvVarName);
                if (!string.IsNullOrWhiteSpace(envVal))
                {
                    AiEnvDetectedText.Text = $"💡 Found %{preset.EnvVarName}% in your environment (click to use)";
                    AiEnvDetectedText.Tag = envVal;
                    AiEnvDetectedText.Visibility = Visibility.Visible;
                }
                else
                {
                    AiEnvDetectedText.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                AiEnvDetectedText.Visibility = Visibility.Collapsed;
            }
        }

        if (AiModelSuggestionsPanel is not null)
        {
            AiModelSuggestionsPanel.Children.Clear();
            if (preset.SuggestedModels is { Count: > 0 })
            {
                foreach (var modelName in preset.SuggestedModels)
                {
                    var btn = new Button
                    {
                        Content = modelName,
                        Style = (Style)FindResource("GlassButton"),
                        Padding = new Thickness(6, 2, 6, 2),
                        FontSize = 9,
                        Margin = new Thickness(0, 0, 4, 3),
                        Tag = modelName,
                        ToolTip = $"Click to set model to {modelName}"
                    };
                    btn.Click += (s, _) =>
                    {
                        if (s is Button b && b.Tag is string m)
                        {
                            AiModelBox.Text = m;
                            _aiSettings.Model = m;
                            UpdateAiModelBadge();
                        }
                    };
                    AiModelSuggestionsPanel.Children.Add(btn);
                }
            }
        }
    }

    private bool _isAiStudioMaximized = false;

    private void AiStudioMaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        _isAiStudioMaximized = !_isAiStudioMaximized;
        if (_isAiStudioMaximized)
        {
            AiStudioModalCard.Margin = new Thickness(0);
            AiStudioModalCard.CornerRadius = new CornerRadius(0);
            AiStudioModalCard.MaxWidth = double.PositiveInfinity;
            AiStudioModalCard.MaxHeight = double.PositiveInfinity;
            if (AiStudioMaximizeButton is not null)
            {
                AiStudioMaximizeButton.Content = "🗗";
                AiStudioMaximizeButton.ToolTip = "Restore AI Studio size";
            }
        }
        else
        {
            AiStudioModalCard.Margin = new Thickness(20);
            AiStudioModalCard.CornerRadius = new CornerRadius(14);
            AiStudioModalCard.MaxWidth = 1680;
            AiStudioModalCard.MaxHeight = 980;
            if (AiStudioMaximizeButton is not null)
            {
                AiStudioMaximizeButton.Content = "⛶";
                AiStudioMaximizeButton.ToolTip = "Maximize AI Studio to full window";
            }
        }
    }

    private void AiGetKeyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as Button)?.Tag as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "https://aistudio.google.com/apikey";
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            AiPingStatusText.Text = "Opened API key creation page in your browser.";
            AiPingStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66D9FF"));
        }
        catch (Exception ex)
        {
            AiPingStatusText.Text = $"Could not open browser: {ex.Message}";
        }
    }

    private void AiEnvDetectedText_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string key && !string.IsNullOrWhiteSpace(key))
        {
            AiApiKeyBox.Password = key;
            AiApiKeyPlainBox.Text = key;
            AiEnvDetectedText.Visibility = Visibility.Collapsed;
            AiPingStatusText.Text = "✓ Applied API key from environment variable!";
            AiPingStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
        }
    }

    private void AiToggleKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (AiApiKeyBox.Visibility == Visibility.Visible)
        {
            AiApiKeyPlainBox.Text = AiApiKeyBox.Password;
            AiApiKeyBox.Visibility = Visibility.Collapsed;
            AiApiKeyPlainBox.Visibility = Visibility.Visible;
            AiToggleKeyVisButton.Content = "🔒";
        }
        else
        {
            AiApiKeyBox.Password = AiApiKeyPlainBox.Text;
            AiApiKeyPlainBox.Visibility = Visibility.Collapsed;
            AiApiKeyBox.Visibility = Visibility.Visible;
            AiToggleKeyVisButton.Content = "👁";
        }
    }

    private async void AiTestPing_Click(object sender, RoutedEventArgs e)
    {
        AiPingStatusText.Text = "Pinging AI provider...";
        AiPingStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7CE8FF"));

        var key = AiApiKeyBox.Visibility == Visibility.Visible ? AiApiKeyBox.Password : AiApiKeyPlainBox.Text;
        var tempSettings = new AiSettings
        {
            ApiUrl = AiApiUrlBox.Text.Trim(),
            ApiKey = key.Trim(),
            Model = AiModelBox.Text.Trim(),
            Provider = (AiProviderCombo.SelectedItem as string) ?? "Custom"
        };

        using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (success, msg, latency) = await AiService.TestConnectionAsync(tempSettings, pingCts.Token);

        AiPingStatusText.Text = success ? $"✓ {msg}" : $"✕ {msg}";
        AiPingStatusText.Foreground = success
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7070"));
    }

    private void AiSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var key = AiApiKeyBox.Visibility == Visibility.Visible ? AiApiKeyBox.Password : AiApiKeyPlainBox.Text;
        _aiSettings.ApiUrl = AiApiUrlBox.Text.Trim();
        _aiSettings.ApiKey = key.Trim();
        _aiSettings.Model = AiModelBox.Text.Trim();
        _aiSettings.Provider = (AiProviderCombo.SelectedItem as string) ?? "Custom";

        AiService.SaveSettings(_aiSettings);
        UpdateAiModelBadge();

        AiPingStatusText.Text = "✓ Settings saved to portable storage.";
        AiPingStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
        AiSettingsPanel.Visibility = Visibility.Collapsed;
        AiStatusText.Text = $"Ready with {_aiSettings.Provider} ({_aiSettings.Model}).";
    }

    // ==========================================
    // Quick Action Prompts
    // ==========================================

    private async void QuickAction_Refactor_Click(object sender, RoutedEventArgs e)
    {
        AiPromptBox.Text = "Refactor this open source code following clean, modern best practices, idiomatic patterns, strong type safety, and optimal performance.";
        await StartAiGenerationAsync(AiPromptBox.Text);
    }

    private async void QuickAction_NeonBlue_Click(object sender, RoutedEventArgs e)
    {
        AiPromptBox.Text = "Make everything orange in the app neon blue: find all orange, amber, brown, or warm accent colors, borders, brushes, and highlights and transform them into glowing electric neon blue and cyan (#00E5FF, #00B0FF, #38EAFF, cyan/electric blue). Keep UI hierarchy crisp, modern, and high contrast.";
        await StartAiGenerationAsync(AiPromptBox.Text);
    }

    private async void QuickAction_DarkMode_Click(object sender, RoutedEventArgs e)
    {
        AiPromptBox.Text = "Give it dark mode: convert this UI to a sleek, modern cyberpunk/glassmorphism dark mode with deep navy/black backgrounds (#0A0E17, #121A29), subtle glassy border strokes (#25FFFFFF), high-contrast readable text (#F0F4FC, #9AAEC8), and vibrant colored accents.";
        await StartAiGenerationAsync(AiPromptBox.Text);
    }

    private async void QuickAction_Font_Click(object sender, RoutedEventArgs e)
    {
        AiPromptBox.Text = "Change the font: update all fonts in this UI to modern typographic hierarchy using Cascadia Code / Segoe UI Variable / JetBrains Mono with clean weights and line heights.";
        await StartAiGenerationAsync(AiPromptBox.Text);
    }

    private async void QuickAction_FixError_Click(object sender, RoutedEventArgs e)
    {
        var outText = OutputBox.Text;
        if (string.IsNullOrWhiteSpace(outText) || (!outText.Contains("error ", StringComparison.OrdinalIgnoreCase) && !outText.Contains("Exception", StringComparison.OrdinalIgnoreCase)))
        {
            AiStatusText.Text = "No compiler errors detected in output console. Run or build the project first (F5/F6) to capture errors.";
            return;
        }

        AiPromptBox.Text = "Fix the compiler / runtime error shown in the console output. Return the complete corrected code and briefly explain what was broken.";
        await StartAiGenerationAsync(AiPromptBox.Text);
    }

    private async void QuickAction_Explain_Click(object sender, RoutedEventArgs e)
    {
        AiPromptBox.Text = "Explain how this code works: summarize its architecture, key functions, data structures, and how to use or extend it.";
        await StartAiGenerationAsync(AiPromptBox.Text);
    }

    private void QuickAction_GitFork_Click(object sender, RoutedEventArgs e)
    {
        AiGitForkPanel.Visibility = AiGitForkPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        _ = RefreshGitStatusAsync();
    }

    private async void AiPromptBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            await StartAiGenerationAsync(AiPromptBox.Text);
            e.Handled = true;
        }
    }

    private async void AiGenerateButton_Click(object sender, RoutedEventArgs e) =>
        await StartAiGenerationAsync(AiPromptBox.Text);

    private void AiStopButton_Click(object sender, RoutedEventArgs e) =>
        _aiCts?.Cancel();

    private async Task StartAiGenerationAsync(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
        {
            AiStatusText.Text = "Please enter an instruction or click one of the quick prompts above.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_aiSettings.ApiUrl))
        {
            AiSettingsPanel.Visibility = Visibility.Visible;
            AiStatusText.Text = "Please configure your AI Provider API URL and key in Settings.";
            return;
        }

        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var token = _aiCts.Token;

        AiGenerateButton.IsEnabled = false;
        AiStopButton.IsEnabled = true;
        AiApplyCodeButton.IsEnabled = false;
        AiCopyCodeButton.IsEnabled = false;
        AiSaveNewFileButton.IsEnabled = false;

        AiResponseBox.Text = "";
        AiStatusText.Text = $"Streaming response from {_aiSettings.Model}...";
        AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66D9FF"));

        var context = new AiPromptContext(
            FileName: _currentFile is not null ? Path.GetFileName(_currentFile) : "welcome.cs",
            Language: _compiler.DetectLanguage(_currentFile ?? "welcome.cs")?.DisplayName ?? "C#",
            SourceCode: SourceEditor.Text,
            SelectedCode: SourceEditor.SelectionLength > 0 ? SourceEditor.SelectedText : null,
            BuildOutput: OutputBox.Text,
            WorkspaceRoot: _workspaceRoot);

        try
        {
            var fullResponse = await AiService.ExecuteChatStreamAsync(
                _aiSettings,
                promptText,
                context,
                chunk =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        AiResponseBox.AppendText(chunk);
                        AiResponseBox.ScrollToEnd();
                    });
                },
                token);

            _aiExtractedCode = AiService.ExtractPrimaryCode(fullResponse);
            if (!string.IsNullOrWhiteSpace(_aiExtractedCode))
            {
                AiApplyCodeButton.IsEnabled = true;
                AiCopyCodeButton.IsEnabled = true;
                AiSaveNewFileButton.IsEnabled = true;
                AiStatusText.Text = "✓ Ready! Code block detected. Click '⚡ Apply to Code' to insert into editor.";
                AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
            }
            else
            {
                AiCopyCodeButton.IsEnabled = true;
                AiStatusText.Text = "✓ Generation complete.";
                AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
            }
        }
        catch (OperationCanceledException)
        {
            AiStatusText.Text = "AI generation cancelled.";
            AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8592A8"));
        }
        catch (Exception ex)
        {
            AiResponseBox.AppendText($"\n\n[AI Error: {ex.Message}]");
            AiStatusText.Text = $"✕ Error: {ex.Message}";
            AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7070"));

            if (ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("API Key", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                AiSettingsPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            AiGenerateButton.IsEnabled = true;
            AiStopButton.IsEnabled = false;
        }
    }

    private void AiApplyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_aiExtractedCode)) return;

        _aiUndoSnapshot = SourceEditor.Text;
        _aiUndoWasSelection = SourceEditor.SelectionLength > 0;

        _suppressEditorEvents = true;
        if (_aiUndoWasSelection)
        {
            SourceEditor.SelectedText = _aiExtractedCode;
        }
        else
        {
            SourceEditor.Text = _aiExtractedCode;
        }
        _suppressEditorEvents = false;

        SetDirty(true);
        UpdateLineNumbers();

        AiUndoCodeButton.Visibility = Visibility.Visible;
        AiStatusText.Text = "✓ Code applied to active editor! Press F5 to build & run, or click '↩ Undo Edit'.";
        AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
    }

    private void AiUndoCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiUndoSnapshot is null) return;

        _suppressEditorEvents = true;
        SourceEditor.Text = _aiUndoSnapshot;
        _suppressEditorEvents = false;

        SetDirty(true);
        UpdateLineNumbers();

        AiUndoCodeButton.Visibility = Visibility.Collapsed;
        AiStatusText.Text = "↩ Original code restored.";
        AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8592A8"));
    }

    private void AiCopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        var textToCopy = !string.IsNullOrWhiteSpace(_aiExtractedCode) ? _aiExtractedCode : AiResponseBox.Text;
        if (!string.IsNullOrWhiteSpace(textToCopy))
        {
            Clipboard.SetText(textToCopy);
            AiStatusText.Text = "✓ Code copied to clipboard.";
            AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
        }
    }

    private async void AiSaveNewFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_aiExtractedCode)) return;

        var baseFolder = _workspaceRoot ?? (_currentFile is not null ? Path.GetDirectoryName(_currentFile) : GitHubService.DownloadsRoot);
        if (string.IsNullOrWhiteSpace(baseFolder))
            baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        var ext = _currentFile is not null ? Path.GetExtension(_currentFile) : ".cs";
        var stem = _currentFile is not null ? Path.GetFileNameWithoutExtension(_currentFile) : "code";
        var newPath = Path.Combine(baseFolder, $"{stem}_ai_modified{ext}");

        var counter = 2;
        while (File.Exists(newPath))
        {
            newPath = Path.Combine(baseFolder, $"{stem}_ai_modified_{counter}{ext}");
            counter++;
        }

        try
        {
            await File.WriteAllTextAsync(newPath, _aiExtractedCode, new UTF8Encoding(false));
            AddOrRefreshExplorerFile(newPath);
            AiStatusText.Text = $"✓ Saved to new file: {Path.GetFileName(newPath)}";
            AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));
        }
        catch (Exception ex)
        {
            AiStatusText.Text = $"✕ Failed to save file: {ex.Message}";
        }
    }

    // ==========================================
    // Git Fork & Commit Helper
    // ==========================================

    private async Task RefreshGitStatusAsync()
    {
        var dir = _workspaceRoot ?? (_currentFile is not null ? Path.GetDirectoryName(_currentFile) : null);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, ".git")))
        {
            // Also check parent folder if active file is in a subdirectory
            if (dir is not null && Directory.Exists(Path.Combine(Path.GetFullPath(Path.Combine(dir, "..")), ".git")))
            {
                dir = Path.GetFullPath(Path.Combine(dir, ".."));
            }
            else
            {
                AiGitBranchText.Text = "Not a git repository";
                AiGitStatusText.Text = "Open a git repository or download a project from GitHub Hub to enable fork & commit features.";
                return;
            }
        }

        try
        {
            var branchStep = new CommandStep("Get branch", "git", ["branch", "--show-current"], dir);
            var branchName = "main";
            await _runner.RunAsync(branchStep, (line, isErr) =>
            {
                if (!isErr && !string.IsNullOrWhiteSpace(line)) branchName = line.Trim();
            }, CancellationToken.None);

            var statusStep = new CommandStep("Git status", "git", ["status", "-s"], dir);
            var modifiedList = new List<string>();
            await _runner.RunAsync(statusStep, (line, isErr) =>
            {
                if (!isErr && !string.IsNullOrWhiteSpace(line)) modifiedList.Add(line.Trim());
            }, CancellationToken.None);

            AiGitBranchText.Text = $"Branch: {branchName}  ({Path.GetFileName(dir)})";
            AiGitStatusText.Text = modifiedList.Count > 0
                ? $"{modifiedList.Count} modified file(s):\n" + string.Join("\n", modifiedList.Take(5)) + (modifiedList.Count > 5 ? $"\n... and {modifiedList.Count - 5} more" : "")
                : "Working tree clean · All changes committed.";
        }
        catch (Exception ex)
        {
            AiGitStatusText.Text = $"Git query: {ex.Message}";
        }
    }

    private void AiSuggestCommitMsg_Click(object sender, RoutedEventArgs e)
    {
        var prompt = AiPromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = "modern code refactoring and styling improvements";

        if (prompt.Length > 60) prompt = prompt[..60];
        AiGitCommitMsgBox.Text = $"refactor: {prompt.ToLowerInvariant()}";
    }

    private async void AiGitCommitPush_Click(object sender, RoutedEventArgs e)
    {
        var dir = _workspaceRoot ?? (_currentFile is not null ? Path.GetDirectoryName(_currentFile) : null);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, ".git")))
        {
            AiStatusText.Text = "Active workspace is not a git repository.";
            return;
        }

        var msg = AiGitCommitMsgBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(msg)) msg = "refactor: code modernization and cleanup";

        AiStatusText.Text = "Running git commit & push...";
        AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66D9FF"));

        try
        {
            var commitStep = new CommandStep("Git commit", "git", ["commit", "-am", msg], dir);
            var commitOut = new StringBuilder();
            await _runner.RunAsync(commitStep, (l, _) => commitOut.AppendLine(l), CancellationToken.None);

            var pushStep = new CommandStep("Git push", "git", ["push"], dir);
            var pushOut = new StringBuilder();
            await _runner.RunAsync(pushStep, (l, _) => pushOut.AppendLine(l), CancellationToken.None);

            AiStatusText.Text = "✓ Changes committed and pushed to git remote!";
            AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4AE28D"));

            await RefreshGitStatusAsync();
        }
        catch (Exception ex)
        {
            AiStatusText.Text = $"✕ Git error: {ex.Message}";
            AiStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7070"));
        }
    }

    private async void AiGitOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var dir = _workspaceRoot ?? (_currentFile is not null ? Path.GetDirectoryName(_currentFile) : null);
        if (string.IsNullOrWhiteSpace(dir)) return;

        string? remoteUrl = null;
        try
        {
            var step = new CommandStep("Git remote", "git", ["remote", "get-url", "origin"], dir);
            await _runner.RunAsync(step, (l, isErr) =>
            {
                if (!isErr && !string.IsNullOrWhiteSpace(l)) remoteUrl = l.Trim();
            }, CancellationToken.None);
        }
        catch { }

        if (string.IsNullOrWhiteSpace(remoteUrl)) return;

        if (remoteUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            remoteUrl = remoteUrl.Replace(":", "/").Replace("git@", "https://");
        }
        if (remoteUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            remoteUrl = remoteUrl[..^4];
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = remoteUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
