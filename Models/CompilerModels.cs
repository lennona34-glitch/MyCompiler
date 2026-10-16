namespace MyCompiler.Models;

public sealed record LanguageDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ToolCandidates,
    string Capability);

public sealed record ToolchainStatus(
    string Name,
    string Tool,
    bool IsAvailable,
    string Detail,
    string? SuggestedCommand = null,
    string? ResolvedPath = null)
{
    public string ActionLabel => IsAvailable ? "Copy update" :
        SuggestedCommand?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true ? "Copy guide" : "Copy install";
}

public sealed record CommandStep(
    string Caption,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record BuildPlan(
    string Language,
    IReadOnlyList<CommandStep> BuildSteps,
    CommandStep? RunStep,
    string Summary,
    string? ArtifactPath = null);

public sealed record ProcessResult(int ExitCode, TimeSpan Duration, bool WasCancelled);

public sealed class SourceFileItem
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public string FileName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();
}

public enum RepoKind
{
    ExecutableApp,
    WebFrontend,
    LibraryOrSdk,
    DocsOrData
}

public sealed record RepoClassification(
    RepoKind Kind,
    string BadgeText,
    string BadgeColor,
    string Summary,
    string? EntryPath = null,
    BuildPlan? Plan = null);

public sealed class GitHubRepoItem
{
    public required string FullName { get; init; }
    public required string Name { get; init; }
    public required string Owner { get; init; }
    public required string Description { get; init; }
    public required string Language { get; init; }
    public required string StarsText { get; init; }
    public required string PeriodStarsText { get; init; }
    public required string HtmlUrl { get; init; }
    public string CategoryHint { get; init; } = "Auto-detect on install";
    public string CategoryColor { get; init; } = "#263F7DFF";
}

