using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MyCompiler.Models;

public sealed class SavedProjectItem : INotifyPropertyChanged
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string FullPath { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "C#";

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("lastOpened")]
    public DateTimeOffset LastOpened { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; } = 0;

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; } = false;

    [JsonPropertyName("gitRemoteUrl")]
    public string? GitRemoteUrl { get; set; }

    [JsonIgnore]
    public string LastOpenedText => LastOpened.ToLocalTime().ToString("MMM dd, yyyy h:mm tt");

    [JsonIgnore]
    public string FileCountText => $"{FileCount} file{(FileCount == 1 ? "" : "s")}";

    [JsonIgnore]
    public bool Exists => Directory.Exists(FullPath) || File.Exists(FullPath);

    [JsonIgnore]
    public string StatusBadgeText => Exists ? "Ready" : "Missing / Moved";

    [JsonIgnore]
    public string StatusBadgeColor => Exists ? "#304AE28D" : "#30E05555";

    [JsonIgnore]
    public string StatusBadgeTextColor => Exists ? "#6AF5A5" : "#FFA5A5";

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyChange(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
