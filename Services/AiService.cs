using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyCompiler.Models;

namespace MyCompiler.Services;

public sealed partial class AiService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    [GeneratedRegex(@"```(?:[a-zA-Z0-9_\-\+#]*\n)?(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    public static string GetSettingsFilePath()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var portablePath = Path.Combine(appDir, "ai_settings.json");
        if (File.Exists(portablePath)) return portablePath;

        var parentPortable = Path.GetFullPath(Path.Combine(appDir, "..", "ai_settings.json"));
        if (File.Exists(parentPortable)) return parentPortable;

        // If on X: drive or USB drive root, check X:\MyCompiler\ai_settings.json
        if (appDir.Length >= 3 && (appDir[1] == ':' || appDir.StartsWith("X:", StringComparison.OrdinalIgnoreCase)))
        {
            var driveRoot = Path.GetPathRoot(appDir) ?? "X:\\";
            var rootMyCompiler = Path.Combine(driveRoot, "MyCompiler", "ai_settings.json");
            if (File.Exists(rootMyCompiler)) return rootMyCompiler;
            if (Directory.Exists(Path.Combine(driveRoot, "MyCompiler"))) return rootMyCompiler;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "ai_settings.json");
    }

    public static AiSettings LoadSettings()
    {
        try
        {
            var path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AiSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch { }

        return new AiSettings();
    }

    public static void SaveSettings(AiSettings settings)
    {
        try
        {
            var path = GetSettingsFilePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static async Task<(bool Success, string Message, TimeSpan Latency)> TestConnectionAsync(
        AiSettings settings,
        CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var url = (settings.ApiUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
                return (false, "API URL is required.", TimeSpan.Zero);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
            }

            var payload = new JsonObject
            {
                ["model"] = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o" : settings.Model.Trim(),
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "Respond with 'OK' and nothing else."
                    }
                },
                ["max_tokens"] = 100,
                ["temperature"] = 0.0
            };

            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var content = await response.Content.ReadAsStringAsync(token);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorMsg = TryExtractErrorMessage(content) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return (false, errorMsg, sw.Elapsed);
            }

            return (true, $"Connected successfully in {sw.ElapsedMilliseconds} ms (Model: {settings.Model})", sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection test cancelled.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}", sw.Elapsed);
        }
    }

    public static async Task<string> ExecuteChatStreamAsync(
        AiSettings settings,
        string userPrompt,
        AiPromptContext context,
        Action<string> onChunk,
        CancellationToken token)
    {
        var url = (settings.ApiUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("AI API URL is not configured. Please open AI Settings.");

        var fullUserMessage = BuildContextualPrompt(userPrompt, context);

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o" : settings.Model.Trim(),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = string.IsNullOrWhiteSpace(settings.SystemPrompt) ? AiSettings.DefaultSystemPrompt : settings.SystemPrompt
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = fullUserMessage
                }
            },
            ["temperature"] = settings.Temperature,
            ["stream"] = true
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        }

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(token);
            var parsedErr = TryExtractErrorMessage(errBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            throw new InvalidOperationException(parsedErr);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fullResponseBuilder = new StringBuilder();
        string? line;

        while ((line = await reader.ReadLineAsync(token)) != null)
        {
            if (token.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
            {
                var data = line[6..].Trim();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase)) break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var contentElem))
                        {
                            var textChunk = contentElem.GetString();
                            if (!string.IsNullOrEmpty(textChunk))
                            {
                                fullResponseBuilder.Append(textChunk);
                                onChunk(textChunk);
                            }
                        }
                    }
                }
                catch
                {
                    // Some endpoints may stream lines with extra metadata
                }
            }
            else if (line.TrimStart().StartsWith('{'))
            {
                // Non-streaming JSON response fallback
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentElem))
                        {
                            var text = contentElem.GetString() ?? "";
                            fullResponseBuilder.Append(text);
                            onChunk(text);
                        }
                    }
                }
                catch { }
            }
        }

        return fullResponseBuilder.ToString();
    }

    private static string BuildContextualPrompt(string userPrompt, AiPromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Task: {userPrompt}");
        sb.AppendLine();
        sb.AppendLine($"Target File: {context.FileName} ({context.Language})");

        if (!string.IsNullOrWhiteSpace(context.SelectedCode))
        {
            sb.AppendLine();
            sb.AppendLine("=== SELECTED CODE SNIPPET IN ACTIVE EDITOR ===");
            sb.AppendLine($"```{context.Language}");
            sb.AppendLine(context.SelectedCode);
            sb.AppendLine("```");
            sb.AppendLine("(Please focus modifications primarily on this selected snippet while keeping it compatible with the rest of the file.)");
            sb.AppendLine();
            sb.AppendLine("=== FULL FILE SURROUNDING CONTEXT ===");
            sb.AppendLine($"```{context.Language}");
            sb.AppendLine(context.SourceCode);
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("=== CURRENT SOURCE CODE IN ACTIVE EDITOR ===");
            sb.AppendLine($"```{context.Language}");
            sb.AppendLine(context.SourceCode);
            sb.AppendLine("```");
        }

        if (!string.IsNullOrWhiteSpace(context.BuildOutput))
        {
            sb.AppendLine();
            sb.AppendLine("=== RECENT BUILD / RUNTIME CONSOLE OUTPUT (FOR DEBUGGING) ===");
            sb.AppendLine("```");
            // Include last 40 lines of build output to prevent huge prompt bloat
            var lines = context.BuildOutput.Split('\n');
            var tail = lines.Length > 40 ? string.Join("\n", lines[^40..]) : context.BuildOutput;
            sb.AppendLine(tail);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    public static string? ExtractPrimaryCode(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var matches = CodeBlockRegex().Matches(markdown);
        if (matches.Count == 0)
        {
            // If no markdown fence was used, check if the response looks like raw code
            if (markdown.Contains("using ") || markdown.Contains("namespace ") ||
                markdown.Contains("<Window") || markdown.Contains("<Page") ||
                markdown.Contains("#include") || markdown.Contains("def ") ||
                markdown.Contains("function ") || markdown.Contains("import "))
            {
                return markdown.Trim();
            }
            return null;
        }

        // Find the largest code block in the response (usually the full refactored implementation)
        Match? best = null;
        var maxLen = -1;
        foreach (Match match in matches)
        {
            var code = match.Groups[1].Value.Trim();
            if (code.Length > maxLen)
            {
                maxLen = code.Length;
                best = match;
            }
        }

        return best?.Groups[1].Value.Trim();
    }

    private static string? TryExtractErrorMessage(string jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);

            // Case 1: Array of objects (Google Generative Language API format: [{ "error": { "message": "..." } }])
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                if (first.TryGetProperty("error", out var arrErr))
                {
                    if (arrErr.ValueKind == JsonValueKind.String)
                        return arrErr.GetString();
                    if (arrErr.TryGetProperty("message", out var msgElem))
                    {
                        var msg = msgElem.GetString();
                        if (!string.IsNullOrWhiteSpace(msg))
                            return FormatFriendlyError(msg);
                    }
                }
            }

            // Case 2: Standard OpenAI error object ({ "error": { "message": "..." } } or { "error": "..." })
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.ValueKind == JsonValueKind.String)
                    return errorObj.GetString();
                if (errorObj.TryGetProperty("message", out var msgElem))
                {
                    var msg = msgElem.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                        return FormatFriendlyError(msg);
                }
            }

            // Case 3: Root "message" property ({ "message": "..." })
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var rootMsg))
            {
                var msg = rootMsg.GetString();
                if (!string.IsNullOrWhiteSpace(msg))
                    return FormatFriendlyError(msg);
            }
        }
        catch { }

        return null;
    }

    private static string FormatFriendlyError(string message)
    {
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not supported for generateContent", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no longer available", StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}\n\n💡 Tip: For Google Gemini, please use active fast models 'gemini-3.5-flash', 'gemini-3.5-flash-lite', or 'gemini-flash-lite-latest'.";
        }

        return message;
    }
}
