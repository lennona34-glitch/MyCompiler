using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyCompiler.Models;

namespace MyCompiler.Services;

public sealed partial class GitHubService
{
    private static string? _customDownloadsRoot;
    public static string DownloadsRoot
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customDownloadsRoot))
                return _customDownloadsRoot;

            var settingsPath = GetDownloadsSettingFilePath();
            if (File.Exists(settingsPath))
            {
                try
                {
                    var saved = File.ReadAllText(settingsPath).Trim();
                    if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                        return saved;
                }
                catch { }
            }

            var desktopDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MyCompiler", "Downloads");
            return desktopDownloads;
        }
        set
        {
            _customDownloadsRoot = value;
            try
            {
                var settingsPath = GetDownloadsSettingFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                File.WriteAllText(settingsPath, value);
            }
            catch { }
        }
    }

    private static string GetDownloadsSettingFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyCompiler", "downloads_location.txt");

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expiry, IReadOnlyList<GitHubRepoItem> Items)> SearchCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyCompiler-Workbench/1.5 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, text/html, */*");
        var ghToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(ghToken))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghToken.Trim());
        return client;
    }

    public static bool IsGitHubUrlOrShorthand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim();
        if (trimmed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://gist.github.com/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://gist.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            return true;

        return ShorthandRepoRegex().IsMatch(trimmed);
    }

    public async Task<IReadOnlyList<GitHubRepoItem>> GetTrendingAsync(string? language, string since, CancellationToken token)
    {
        try
        {
            var scraped = await ScrapeTrendingPageAsync(language, since, token);
            if (scraped.Count > 0)
                return scraped;
        }
        catch
        {
            // Fallback to GitHub Search REST API if HTML layout changes or is blocked.
        }

        return await SearchTrendingViaApiAsync(language, since, token);
    }

    /// <summary>
    /// Fetches GitHub repositories that specifically target Windows desktop/apps/games/tools so every card is a real runnable Windows app.
    /// Note: GitHub Search REST API rejects >5 boolean operators and dislikes parentheses around terms.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepoItem>> GetWindowsAppsFeedAsync(string? language, CancellationToken token)
    {
        var langFilter = !string.IsNullOrWhiteSpace(language) && !language.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? $" language:{language}"
            : "";
        var q = $"windows wpf OR winforms OR winui desktop stars:>35{langFilter}";
        return await SearchRepositoriesCoreAsync(q, token, "★ Windows Ready", perPage: 30);
    }

    /// <summary>
    /// Fetches Decompilation & Static Recompilation game/engine ports (N64Recomp, Zelda64Recomp, Shipwright, decomp/recomp projects).
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepoItem>> GetDecompRecompFeedAsync(string? language, CancellationToken token)
    {
        var langFilter = !string.IsNullOrWhiteSpace(language) && !language.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? $" language:{language}"
            : "";
        var q = $"n64recomp OR zelda64recomp OR decomp OR recomp in:name,description,topics stars:>15{langFilter}";
        var raw = await SearchRepositoriesCoreAsync(q, token, "🎮 Decomp / Recomp", perPage: 35);
        return raw
            .Where(item =>
                !item.Name.Equals("recompose", StringComparison.OrdinalIgnoreCase) &&
                !item.Name.Equals("decompose", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Fetches Antigravity skills, plugins, and compatible MCP servers.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepoItem>> GetAntigravityMcpFeedAsync(string? language, CancellationToken token)
    {
        var langFilter = !string.IsNullOrWhiteSpace(language) && !language.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? $" language:{language}"
            : "";
        var q = $"antigravity stars:>1{langFilter}";
        return await SearchRepositoriesCoreAsync(q, token, "🤖 Antigravity / MCP", perPage: 30);
    }

    public async Task<IReadOnlyList<GitHubRepoItem>> SearchRepositoriesAsync(
        string query,
        string? language,
        CancellationToken token,
        string periodLabel = "GitHub Search")
    {
        var sanitized = SanitizeUserSearchQuery(query);
        var q = string.IsNullOrWhiteSpace(sanitized) ? "stars:>100" : sanitized;
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("all", StringComparison.OrdinalIgnoreCase))
            q += $" language:{language}";

        return await SearchRepositoriesCoreAsync(q, token, periodLabel, perPage: 25);
    }

    private static string SanitizeUserSearchQuery(string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
            return string.Empty;

        // Strip parentheses and unbalanced quotes that cause GitHub's search parser to return 0 results or 422
        var cleaned = rawQuery.Replace('(', ' ').Replace(')', ' ').Replace('"', ' ').Trim();
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // GitHub Search API returns HTTP 422 if more than 5 AND / OR / NOT operators are present
        var operatorCount = 0;
        var safeTokens = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (t.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                operatorCount++;
                if (operatorCount > 4)
                    continue;
                safeTokens.Add(t.ToUpperInvariant());
            }
            else
            {
                safeTokens.Add(t);
            }
        }

        // Remove leading/trailing boolean operators
        while (safeTokens.Count > 0 && (safeTokens[0] is "OR" or "AND" or "NOT"))
            safeTokens.RemoveAt(0);
        while (safeTokens.Count > 0 && (safeTokens[^1] is "OR" or "AND" or "NOT"))
            safeTokens.RemoveAt(safeTokens.Count - 1);

        return string.Join(' ', safeTokens);
    }

    private async Task<IReadOnlyList<GitHubRepoItem>> SearchRepositoriesCoreAsync(
        string q,
        CancellationToken token,
        string periodLabel,
        int perPage = 25)
    {
        var cacheKey = $"{q}|{periodLabel}|{perPage}";
        if (SearchCache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
            return cached.Items;

        var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page={perPage}";
        using var response = await Http.GetAsync(url, token);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // Fallback: strip any special qualifiers/characters and retry as plain keywords
            var plain = Regex.Replace(q, @"[^\w\s\-\.#\+]", " ").Trim();
            if (string.IsNullOrWhiteSpace(plain)) plain = "stars:>100";
            var retryUrl = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(plain)}&sort=stars&order=desc&per_page={perPage}";
            using var retryResponse = await Http.GetAsync(retryUrl, token);
            retryResponse.EnsureSuccessStatusCode();
            var retryItems = await ParseSearchResponseAsync(retryResponse, periodLabel, token);
            SearchCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(3), retryItems);
            return retryItems;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("GitHub Search API rate limit reached (10 searches/min unauthenticated). Please wait ~15 seconds and try again.");
        }

        response.EnsureSuccessStatusCode();
        var items = await ParseSearchResponseAsync(response, periodLabel, token);
        SearchCache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(3), items);
        return items;
    }

    private static async Task<IReadOnlyList<GitHubRepoItem>> ParseSearchResponseAsync(
        HttpResponseMessage response,
        string periodLabel,
        CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

        var items = new List<GitHubRepoItem>();
        if (!doc.RootElement.TryGetProperty("items", out var array))
            return items;

        foreach (var item in array.EnumerateArray())
        {
            var fullName = item.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(fullName)) continue;
            var parts = fullName.Split('/');
            var desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "No description provided.";
            var lang = item.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "Multi" : "Multi";
            var stars = item.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0;
            var htmlUrl = item.TryGetProperty("html_url", out var u) ? u.GetString() ?? $"https://github.com/{fullName}" : $"https://github.com/{fullName}";
            var (hint, color) = GuessRepoHint(fullName, desc, lang);

            items.Add(new GitHubRepoItem
            {
                FullName = fullName,
                Owner = parts[0],
                Name = parts.Length > 1 ? parts[1] : fullName,
                Description = desc,
                Language = lang,
                StarsText = $"★ {stars:N0}",
                PeriodStarsText = periodLabel,
                HtmlUrl = htmlUrl,
                CategoryHint = hint,
                CategoryColor = color
            });
        }
        return items;
    }

    private async Task<IReadOnlyList<GitHubRepoItem>> ScrapeTrendingPageAsync(string? language, string since, CancellationToken token)
    {
        var langSlug = string.IsNullOrWhiteSpace(language) || language.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ""
            : "/" + Uri.EscapeDataString(language.ToLowerInvariant());
        var url = $"https://github.com/trending{langSlug}?since={Uri.EscapeDataString(since)}";
        var html = await Http.GetStringAsync(url, token);

        var results = new List<GitHubRepoItem>();
        foreach (Match article in ArticleRegex().Matches(html))
        {
            var block = article.Value;
            var repoMatch = RepoLinkRegex().Match(block);
            if (!repoMatch.Success) continue;

            var fullName = $"{repoMatch.Groups[1].Value.Trim()}/{repoMatch.Groups[2].Value.Trim()}";
            var parts = fullName.Split('/');
            var descMatch = DescriptionRegex().Match(block);
            var description = descMatch.Success ? CleanHtml(descMatch.Groups[1].Value) : "No description provided.";

            var langMatch = LanguageRegex().Match(block);
            var lang = langMatch.Success ? CleanHtml(langMatch.Groups[1].Value) : "Polyglot";

            var starsMatch = StarsRegex().Match(block);
            var stars = starsMatch.Success ? CleanHtml(starsMatch.Groups[1].Value) : "";

            var periodMatch = PeriodStarsRegex().Match(block);
            var period = periodMatch.Success ? CleanHtml(periodMatch.Groups[1].Value) : "Trending";

            var (hint, color) = GuessRepoHint(fullName, description, lang);

            results.Add(new GitHubRepoItem
            {
                FullName = fullName,
                Owner = parts[0],
                Name = parts[1],
                Description = description,
                Language = lang,
                StarsText = string.IsNullOrWhiteSpace(stars) ? "★ Trending" : $"★ {stars}",
                PeriodStarsText = period,
                HtmlUrl = $"https://github.com/{fullName}",
                CategoryHint = hint,
                CategoryColor = color
            });
        }
        return results;
    }

    private async Task<IReadOnlyList<GitHubRepoItem>> SearchTrendingViaApiAsync(string? language, string since, CancellationToken token)
    {
        var days = since switch
        {
            "weekly" => 7,
            "monthly" => 30,
            _ => 3
        };
        var date = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        var q = $"created:>{date}";
        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("all", StringComparison.OrdinalIgnoreCase))
            q += $" language:{language}";

        return await SearchRepositoriesAsync(q, null, token);
    }

    private static (string Hint, string Color) GuessRepoHint(string fullName, string description, string language)
    {
        var text = $"{fullName} {description}".ToLowerInvariant();
        if (IsDecompOrRecompText(text))
            return ("🎮 Decomp / Recomp Game", "#C85A17");
        if (text.Contains("antigravity"))
            return ("🤖 Antigravity Skill / MCP", "#0E8A7A");
        if (text.Contains("mcp") || text.Contains("claude-plugin") || text.Contains("claude code") || text.Contains("agent-skill") || text.Contains("agent skill") || text.Contains("cursor-plugin"))
            return ("AI Plugin / MCP Server", "#6E4599");
        if (text.Contains("awesome-") || text.Contains("curated list") || text.Contains("roadmap") || text.Contains("interview") || text.Contains("tutorial") || text.Contains("book") || language.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            return ("Docs / Curated List", "#3D526B");
        if (text.Contains("sdk") || text.Contains("library") || text.Contains("framework") || text.Contains("binding") || text.Contains("wrapper") || text.Contains("api client"))
            return ("Library / SDK", "#5C4799");
        if (text.Contains("wpf") || text.Contains("winforms") || text.Contains("winui") || text.Contains("windows app") || text.Contains("desktop app"))
            return ("Windows GUI App", "#157F54");
        if (text.Contains("cli") || text.Contains("terminal") || text.Contains("desktop") || text.Contains("tool") || text.Contains("app") || text.Contains("game") || text.Contains("emulator") || text.Contains("editor") || text.Contains("player"))
            return ("Runnable App / Tool", "#1B6B4A");
        if (language is "HTML" or "CSS" or "Vue" or "Svelte")
            return ("Web / UI Project", "#7A5221");
        return ("Source / Auto-Detect", "#263F7DFF");
    }

    private static bool IsDecompOrRecompText(string lowerText)
    {
        return lowerText.Contains("recomp") ||
               lowerText.Contains("decomp") ||
               lowerText.Contains("n64recomp") ||
               lowerText.Contains("zelda64") ||
               lowerText.Contains("shipwright") ||
               lowerText.Contains("harbourmasters") ||
               lowerText.Contains("sm64") ||
               lowerText.Contains("static recompilation") ||
               lowerText.Contains("matching decompilation");
    }

    private static string CleanHtml(string raw)
    {
        var stripped = HtmlTagRegex().Replace(raw, "");
        return WebUtility.HtmlDecode(stripped).Trim();
    }

    public async Task<(string Path, bool IsSingleFile, string? RepoOwnerAndName)> DownloadAsync(
        string inputUrl,
        Action<string> log,
        CancellationToken token)
    {
        var trimmed = inputUrl.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (ShorthandRepoRegex().IsMatch(trimmed))
                trimmed = $"https://github.com/{trimmed}";
            else
                throw new ArgumentException("Please enter a valid GitHub URL or owner/repo shorthand (e.g. 'sharkdp/fd').");
        }

        var uri = new Uri(trimmed);
        Directory.CreateDirectory(DownloadsRoot);

        // 1. Direct raw file
        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadSingleFileAsync(uri.ToString(), Path.GetFileName(uri.LocalPath), log, token);
        }

        // 2. GitHub blob URL -> convert to raw URL and download single source file
        var blobMatch = GitHubBlobRegex().Match(trimmed);
        if (blobMatch.Success)
        {
            var owner = blobMatch.Groups[1].Value;
            var repo = blobMatch.Groups[2].Value;
            var branch = blobMatch.Groups[3].Value;
            var filePath = blobMatch.Groups[4].Value;
            var rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{filePath}";
            var fileName = Path.GetFileName(filePath);
            log($"› Detected single GitHub source file: {owner}/{repo}/{filePath}");
            var result = await DownloadSingleFileAsync(rawUrl, fileName, log, token);
            return (result.Path, true, $"{owner}/{repo}");
        }

        // 3. GitHub Gist URL
        if (uri.Host.Equals("gist.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var gistId = segments.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(gistId))
                return await DownloadGistAsync(gistId, log, token);
        }

        // 4. GitHub Repository URL
        var repoMatch = GitHubRepoUrlRegex().Match(trimmed);
        if (!repoMatch.Success)
            throw new InvalidOperationException("URL does not match a recognized GitHub repository, file, or Gist.");

        var repoOwner = repoMatch.Groups[1].Value;
        var repoName = repoMatch.Groups[2].Value.Replace(".git", "", StringComparison.OrdinalIgnoreCase);
        var treeBranch = repoMatch.Groups[3].Success ? repoMatch.Groups[3].Value : null;
        var subFolder = repoMatch.Groups[4].Success ? repoMatch.Groups[4].Value.Trim('/') : null;

        var fullName = $"{repoOwner}/{repoName}";
        log($"› Downloading repository {fullName}…");

        // Clean, readable destination folder (e.g. "MyCompiler" or "owner-MyCompiler" on conflict)
        var targetFolder = Path.Combine(DownloadsRoot, repoName);
        if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
        {
            targetFolder = Path.Combine(DownloadsRoot, $"{repoOwner}-{repoName}");
            if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
            {
                var counter = 2;
                while (Directory.Exists($"{targetFolder} ({counter})") && Directory.EnumerateFileSystemEntries($"{targetFolder} ({counter})").Any())
                    counter++;
                targetFolder = $"{targetFolder} ({counter})";
            }
        }
        Directory.CreateDirectory(targetFolder);

        var zipPath = Path.Combine(targetFolder, "repo.zip");
        var downloaded = false;

        var urlsToTry = new List<string>();
        if (!string.IsNullOrWhiteSpace(treeBranch))
            urlsToTry.Add($"https://codeload.github.com/{repoOwner}/{repoName}/zip/refs/heads/{treeBranch}");
        urlsToTry.Add($"https://api.github.com/repos/{repoOwner}/{repoName}/zipball");
        urlsToTry.Add($"https://codeload.github.com/{repoOwner}/{repoName}/zip/refs/heads/main");
        urlsToTry.Add($"https://codeload.github.com/{repoOwner}/{repoName}/zip/refs/heads/master");

        foreach (var zipUrl in urlsToTry.Distinct())
        {
            try
            {
                using var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) continue;

                await using var fs = File.Create(zipPath);
                await response.Content.CopyToAsync(fs, token);
                downloaded = true;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try next URL */ }
        }

        if (!downloaded)
            throw new InvalidOperationException($"Could not download archive for {fullName}. Check that the repository is public.");

        log("› Extracting repository archive…");
        var tempExtract = Path.Combine(targetFolder, "_extract_temp");
        Directory.CreateDirectory(tempExtract);
        ZipFile.ExtractToDirectory(zipPath, tempExtract, overwriteFiles: true);
        File.Delete(zipPath);

        // Flatten outer GitHub archive wrapper directory so repository files sit cleanly at root
        var topDirs = Directory.GetDirectories(tempExtract);
        var topFiles = Directory.GetFiles(tempExtract);
        var sourceDir = topDirs.Length == 1 && topFiles.Length == 0 ? topDirs[0] : tempExtract;

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dest = Path.Combine(targetFolder, Path.GetFileName(dir));
            if (dest.Equals(tempExtract, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.Move(dir, dest);
        }
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetFolder, Path.GetFileName(file));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(file, dest);
        }
        if (Directory.Exists(tempExtract))
            Directory.Delete(tempExtract, recursive: true);

        var extractedRoot = targetFolder;

        if (!string.IsNullOrWhiteSpace(subFolder))
        {
            var candidateSub = Path.Combine(extractedRoot, subFolder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidateSub))
            {
                log($"› Focusing subfolder: {subFolder}");
                extractedRoot = candidateSub;
            }
            else if (File.Exists(candidateSub))
            {
                return (candidateSub, true, fullName);
            }
        }

        return (extractedRoot, false, fullName);
    }

    private async Task<(string Path, bool IsSingleFile, string? RepoOwnerAndName)> DownloadSingleFileAsync(
        string rawUrl,
        string fileName,
        Action<string> log,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "main.cs";

        var folder = Path.Combine(DownloadsRoot, "Files");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, fileName);
        if (File.Exists(destination))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var counter = 2;
            while (File.Exists(Path.Combine(folder, $"{stem} ({counter}){ext}")))
                counter++;
            destination = Path.Combine(folder, $"{stem} ({counter}){ext}");
        }

        log($"› Downloading {Path.GetFileName(destination)}…");
        var content = await Http.GetByteArrayAsync(rawUrl, token);
        await File.WriteAllBytesAsync(destination, content, token);
        return (destination, true, null);
    }

    private async Task<(string Path, bool IsSingleFile, string? RepoOwnerAndName)> DownloadGistAsync(
        string gistId,
        Action<string> log,
        CancellationToken token)
    {
        log($"› Fetching Gist {gistId}…");
        var url = $"https://api.github.com/gists/{Uri.EscapeDataString(gistId)}";
        using var response = await Http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

        var folder = Path.Combine(DownloadsRoot, "Gists", $"Gist_{gistId}");
        Directory.CreateDirectory(folder);

        if (!doc.RootElement.TryGetProperty("files", out var filesObj))
            throw new InvalidOperationException("Gist contains no files.");

        var savedFiles = new List<string>();
        foreach (var prop in filesObj.EnumerateObject())
        {
            var fileName = prop.Name;
            var fileEl = prop.Value;
            var filePath = Path.Combine(folder, fileName);
            if (fileEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            {
                await File.WriteAllTextAsync(filePath, contentEl.GetString() ?? "", token);
                savedFiles.Add(filePath);
            }
            else if (fileEl.TryGetProperty("raw_url", out var rawEl) && rawEl.ValueKind == JsonValueKind.String)
            {
                var bytes = await Http.GetByteArrayAsync(rawEl.GetString()!, token);
                await File.WriteAllBytesAsync(filePath, bytes, token);
                savedFiles.Add(filePath);
            }
        }

        if (savedFiles.Count == 1)
            return (savedFiles[0], true, null);
        return (folder, false, null);
    }

    /// <summary>
    /// Queries GitHub Releases for a prebuilt Windows executable or portable archive and downloads/extracts it if found.
    /// Checking this FIRST for repositories with releases gives instant Windows execution without fighting local SDK versions.
    /// </summary>
    public async Task<string?> TryDownloadWindowsReleaseBinaryAsync(
        string repoFullName,
        string destinationRoot,
        Action<string> log,
        CancellationToken token)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repoFullName}/releases/latest";
            using var response = await Http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;

            string? bestUrl = null;
            string? bestName = null;
            var bestScore = -1;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var dlUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dlUrl)) continue;

                var lower = name.ToLowerInvariant();
                if (lower.Contains("linux") || lower.Contains("darwin") || lower.Contains("macos") || lower.Contains("osx") ||
                    lower.Contains("android") || lower.Contains("ios") || lower.Contains("arm64") || lower.Contains("aarch64") ||
                    lower.EndsWith(".deb") || lower.EndsWith(".rpm") || lower.EndsWith(".dmg") || lower.EndsWith(".apk") ||
                    lower.EndsWith(".appimage") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.xz") ||
                    lower.EndsWith(".sha256") || lower.EndsWith(".sig") || lower.EndsWith(".pdb"))
                    continue;

                var score = 0;
                if (lower.EndsWith(".exe") && !lower.Contains("setup") && !lower.Contains("installer") && !lower.Contains("unins"))
                    score += 60;
                else if (lower.EndsWith(".zip") && (lower.Contains("win") || lower.Contains("x86_64") || lower.Contains("x64") || lower.Contains("amd64") || lower.Contains("msvc") || lower.Contains("portable")))
                    score += 55;
                else if (lower.EndsWith(".zip") && !lower.Contains("src") && !lower.Contains("source"))
                    score += 35;
                else if (lower.EndsWith(".exe"))
                    score += 30;
                else
                    continue;

                if (lower.Contains("x64") || lower.Contains("x86_64") || lower.Contains("amd64") || lower.Contains("win64")) score += 15;
                if (lower.Contains("portable") || lower.Contains("standalone")) score += 10;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = dlUrl;
                    bestName = name;
                }
            }

            if (bestUrl is null || bestName is null) return null;

            var releaseDir = Path.Combine(destinationRoot, "_release_bin");
            Directory.CreateDirectory(releaseDir);
            var targetFile = Path.Combine(releaseDir, bestName);

            log($"★ Found official Windows release package: {bestName}");
            log($"  Downloading prebuilt Windows release from GitHub…");
            using var dlResponse = await Http.GetAsync(bestUrl, HttpCompletionOption.ResponseHeadersRead, token);
            dlResponse.EnsureSuccessStatusCode();
            await using (var fs = File.Create(targetFile))
            {
                await dlResponse.Content.CopyToAsync(fs, token);
            }

            if (bestName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDir = Path.Combine(releaseDir, "extracted");
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(targetFile, extractDir, overwriteFiles: true);
                var exes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("createdump", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToList();
                return exes.FirstOrDefault();
            }

            return targetFile;
        }
        catch
        {
            return null;
        }
    }

    public static RepoClassification ClassifyWorkspace(string root, CompilerEngine compiler)
    {
        var allFiles = SafeEnumerateFiles(root, maxDepth: 3);
        var readme = allFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase));
        var readmeText = readme is not null ? SafeReadText(readme).ToLowerInvariant() : "";
        var rootNameLower = Path.GetFileName(root).ToLowerInvariant();

        // 0A. Check if this is a Decomp / Recomp Game or Engine Port
        var hasRecompToml = allFiles.Any(f => f.EndsWith("recomp.toml", StringComparison.OrdinalIgnoreCase) ||
                                              Path.GetFileName(f).Equals("n64recomp.toml", StringComparison.OrdinalIgnoreCase) ||
                                              Path.GetFileName(f).Equals("us.splat.yaml", StringComparison.OrdinalIgnoreCase) ||
                                              Path.GetFileName(f).Equals("splat.yaml", StringComparison.OrdinalIgnoreCase));
        var isDedicatedDecompRepo = hasRecompToml ||
                                    IsDecompOrRecompText(rootNameLower) ||
                                    readmeText.Contains("static recompilation") ||
                                    readmeText.Contains("matching decompilation") ||
                                    readmeText.Contains("n64recomp");
        if (isDedicatedDecompRepo)
        {
            var csproj = allFiles.FirstOrDefault(f => Path.GetExtension(f).Equals(".csproj", StringComparison.OrdinalIgnoreCase));
            var cmake = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase));
            var makefile = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("Makefile", StringComparison.OrdinalIgnoreCase));
            BuildPlan? decompPlan = null;
            try
            {
                if (csproj is not null) decompPlan = compiler.CreatePlan(csproj, root);
                else if (cmake is not null) decompPlan = compiler.CreatePlan(cmake, root);
                else if (makefile is not null) decompPlan = compiler.CreatePlan(makefile, root);
            }
            catch { }

            var entrySrc = FindRepresentativeSource(root, [".cpp", ".c", ".h", ".cs", ".rs"]) ?? readme ?? allFiles.FirstOrDefault();
            return new RepoClassification(
                RepoKind.ExecutableApp,
                "🎮 DECOMP / RECOMP GAME",
                "#C85A17",
                "Detected Decompilation / Static Recompilation game port. Checking GitHub Releases for a prebuilt Windows executable first (note: some ports require your own legally obtained retail ROM/assets to launch).",
                entrySrc,
                decompPlan);
        }

        // 0B. Check if this is an Antigravity Skill / Plugin or AI Agent Plugin / MCP Server
        var hasSkillMd = allFiles.Any(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (hasSkillMd || readmeText.Contains("antigravity"))
        {
            var skillFile = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) ?? readme;
            if (!allFiles.Any(f => Path.GetExtension(f) is ".csproj" or ".sln") &&
                (hasSkillMd || File.Exists(Path.Combine(root, ".claude-plugin")) || Directory.Exists(Path.Combine(root, ".claude-plugin"))))
            {
                return new RepoClassification(
                    RepoKind.LibraryOrSdk,
                    "🤖 ANTIGRAVITY SKILL / MCP",
                    "#0E8A7A",
                    "Detected Antigravity-compatible AI Skill / MCP repository. Opened SKILL.md / README in the editor so you can inspect or copy it to your Antigravity skills folder.",
                    skillFile ?? allFiles.FirstOrDefault(),
                    null);
            }
        }

        if (File.Exists(Path.Combine(root, ".claude-plugin")) ||
            Directory.Exists(Path.Combine(root, ".claude-plugin")) ||
            File.Exists(Path.Combine(root, "openclaw.plugin.json")) ||
            File.Exists(Path.Combine(root, ".mcp.json.example")))
        {
            var bundleCli = Path.Combine(root, "cli.bundle.mjs");
            if (File.Exists(bundleCli))
            {
                var plan = new BuildPlan("AI Plugin / CLI Bundle",
                    [],
                    new CommandStep("Run CLI bundle (--help)", "node", [bundleCli, "--help"], root),
                    "Pre-bundled CLI / MCP Server");
                return new RepoClassification(RepoKind.LibraryOrSdk, "AI PLUGIN / MCP SERVER", "#6E4599",
                    "This repository is an AI Agent Plugin / MCP Server (meant to be plugged into an AI agent rather than run as a standalone GUI window). Running its bundled CLI `--help` output.",
                    bundleCli, plan);
            }
        }

        // 1. Check .NET (.csproj / .fsproj / .vbproj)
        var csprojs = allFiles.Where(f => Path.GetExtension(f).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                                       || Path.GetExtension(f).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                                       || Path.GetExtension(f).Equals(".vbproj", StringComparison.OrdinalIgnoreCase)).ToList();
        if (csprojs.Count > 0)
        {
            // Prioritize WinExe (WPF/WinForms GUI apps) first, then Exe (console apps)
            string? guiProj = null;
            string? consoleProj = null;
            string? testProj = null;

            foreach (var p in csprojs)
            {
                var txt = SafeReadText(p);
                var name = Path.GetFileName(p);
                if (name.Contains("Test", StringComparison.OrdinalIgnoreCase) || txt.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                {
                    testProj ??= p;
                    continue;
                }
                if (txt.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase) ||
                    txt.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase) ||
                    txt.Contains("<UseWindowsForms>true</UseWindowsForms>", StringComparison.OrdinalIgnoreCase) ||
                    txt.Contains("<UseWinUI>true</UseWinUI>", StringComparison.OrdinalIgnoreCase))
                {
                    guiProj ??= p;
                }
                else if (txt.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
                {
                    consoleProj ??= p;
                }
            }

            var targetProj = guiProj ?? consoleProj;
            if (targetProj is not null)
            {
                var projDir = Path.GetDirectoryName(targetProj)!;
                var dist = Path.Combine(projDir, "dist");
                var stem = InspectAssemblyName(targetProj) ?? Path.GetFileNameWithoutExtension(targetProj);
                var exe = Path.Combine(dist, stem + ".exe");

                // Pass --roll-forward Major so net6.0 / net7.0 / net8.0 GitHub apps run seamlessly on .NET 9!
                var buildSteps = new List<CommandStep>
                {
                    new("Restore & build .NET app", "dotnet",
                        ["publish", targetProj, "-c", "Release", "-o", dist, "--nologo", "--verbosity:minimal", "-p:RollForward=Major"], projDir)
                };
                var runStep = new CommandStep($"Launch {stem}.exe", exe, [], dist);
                var plan = new BuildPlan(guiProj is not null ? "Windows GUI App (.NET)" : ".NET Console App",
                    buildSteps, runStep, $"Automated .NET publish & launch · {Path.GetFileName(targetProj)}", exe);

                var entryCs = FindRepresentativeSource(projDir, [".cs", ".fs", ".vb"]) ?? targetProj;
                return new RepoClassification(RepoKind.ExecutableApp,
                    guiProj is not null ? "WINDOWS GUI APP (.NET)" : "RUNNABLE .NET APP",
                    "#157F54",
                    $"Detected runnable Windows .NET application ({Path.GetFileName(targetProj)}).",
                    entryCs, plan);
            }

            var firstProj = csprojs[0];
            if (testProj is not null)
            {
                var testPlan = new BuildPlan(".NET Library (Test Suite)",
                    [new CommandStep("Build .NET library", "dotnet", ["build", firstProj, "--nologo"], root)],
                    new CommandStep("Run library test suite", "dotnet", ["test", testProj, "--nologo", "--roll-forward", "Major"], Path.GetDirectoryName(testProj)!),
                    $"Library build + test suite · {Path.GetFileName(firstProj)}");
                return new RepoClassification(RepoKind.LibraryOrSdk, ".NET LIBRARY (WITH TESTS)", "#5C4799",
                    $"This repository is a reusable .NET library ({Path.GetFileName(firstProj)}), not a standalone desktop app. MyCompiler will build the library and run its test suite.",
                    FindRepresentativeSource(root, [".cs", ".fs"]) ?? firstProj, testPlan);
            }

            var libBuildPlan = new BuildPlan(".NET Library",
                [new CommandStep("Build .NET library", "dotnet", ["build", firstProj, "-c", "Release", "--nologo"], root)],
                null,
                $"Library build · {Path.GetFileName(firstProj)}");
            return new RepoClassification(RepoKind.LibraryOrSdk, ".NET LIBRARY / SDK", "#5C4799",
                $"This repository is a .NET class library ({Path.GetFileName(firstProj)}) meant to be imported by other code rather than run as an app.",
                FindRepresentativeSource(root, [".cs", ".fs"]) ?? firstProj, libBuildPlan);
        }

        // 2. Check Rust (Cargo.toml)
        var cargo = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase));
        if (cargo is not null)
        {
            var cargoDir = Path.GetDirectoryName(cargo)!;
            var cargoText = SafeReadText(cargo);
            var hasMainRs = File.Exists(Path.Combine(cargoDir, "src", "main.rs"))
                         || cargoText.Contains("[[bin]]", StringComparison.OrdinalIgnoreCase)
                         || allFiles.Any(f => Path.GetFileName(f).Equals("main.rs", StringComparison.OrdinalIgnoreCase));

            if (hasMainRs)
            {
                var plan = compiler.CreatePlan(cargo, root);
                var mainRs = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("main.rs", StringComparison.OrdinalIgnoreCase)) ?? cargo;
                return new RepoClassification(RepoKind.ExecutableApp, "RUNNABLE RUST APP", "#1B6B4A",
                    "Detected runnable Rust binary crate.", mainRs, plan);
            }

            var libRs = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("lib.rs", StringComparison.OrdinalIgnoreCase)) ?? cargo;
            var rustLibPlan = new BuildPlan("Rust Library Crate",
                [new CommandStep("Cargo build library", "cargo", ["build", "--manifest-path", cargo], cargoDir)],
                new CommandStep("Cargo run tests", "cargo", ["test", "--manifest-path", cargo], cargoDir),
                "Rust library build + cargo test");
            return new RepoClassification(RepoKind.LibraryOrSdk, "RUST LIBRARY CRATE", "#5C4799",
                "This repository is a Rust library crate (lib.rs) with no binary entry point. MyCompiler will compile the crate and run its test suite.",
                libRs, rustLibPlan);
        }

        // 3. Check Go (go.mod)
        var goMod = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("go.mod", StringComparison.OrdinalIgnoreCase));
        if (goMod is not null)
        {
            var goDir = Path.GetDirectoryName(goMod)!;
            var goFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".go", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)).ToList();
            var mainGo = goFiles.FirstOrDefault(f =>
            {
                var txt = SafeReadText(f);
                return txt.Contains("package main", StringComparison.Ordinal) && txt.Contains("func main(", StringComparison.Ordinal);
            });

            if (mainGo is not null)
            {
                var mainDir = Path.GetDirectoryName(mainGo)!;
                var exe = Path.Combine(goDir, "dist", Path.GetFileNameWithoutExtension(goDir) + ".exe");
                Directory.CreateDirectory(Path.Combine(goDir, "dist"));
                var plan = new BuildPlan("Go App",
                    [new CommandStep("Build Go executable", "go", ["build", "-o", exe, "."], mainDir)],
                    new CommandStep("Run Go executable", exe, [], mainDir),
                    $"Go application · {Path.GetFileName(mainDir)}", exe);
                return new RepoClassification(RepoKind.ExecutableApp, "RUNNABLE GO APP", "#1B6B4A",
                    $"Detected Go executable entry point ({Path.GetRelativePath(root, mainGo)}).", mainGo, plan);
            }

            var goLibPlan = new BuildPlan("Go Package / Library",
                [new CommandStep("Build Go packages", "go", ["build", "./..."], goDir)],
                new CommandStep("Run Go tests", "go", ["test", "./..."], goDir),
                "Go library build + test");
            return new RepoClassification(RepoKind.LibraryOrSdk, "GO PACKAGE / LIBRARY", "#5C4799",
                "This repository is a Go package/library without a `func main()` binary entry point. MyCompiler will verify compilation and run `go test ./...`.",
                goFiles.FirstOrDefault() ?? goMod, goLibPlan);
        }

        // 4. Check Python projects / scripts
        var pyFiles = allFiles.Where(f => Path.GetExtension(f).Equals(".py", StringComparison.OrdinalIgnoreCase)
                                       || Path.GetExtension(f).Equals(".pyw", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pyFiles.Count > 0)
        {
            var entryPy = ChoosePythonEntryFile(root, pyFiles);
            if (entryPy is not null)
            {
                var plan = CreateAutomatedPythonPlan(root, entryPy);
                return new RepoClassification(RepoKind.ExecutableApp, "RUNNABLE PYTHON APP", "#1B6B4A",
                    $"Detected runnable Python entry point ({Path.GetRelativePath(root, entryPy)}).", entryPy, plan);
            }

            return new RepoClassification(RepoKind.LibraryOrSdk, "PYTHON LIBRARY / MODULE", "#5C4799",
                "This repository is a Python package/library without a standalone script entry point. Opening in workspace for inspection.",
                pyFiles[0], null);
        }

        // 5. Check Node.js / TypeScript (package.json)
        var pkgJson = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase));
        if (pkgJson is not null)
        {
            var pkgDir = Path.GetDirectoryName(pkgJson)!;
            var info = InspectPackageJson(pkgJson);

            if (info.IsMcpServer)
            {
                var steps = new List<CommandStep>
                {
                    new("Install npm dependencies (--ignore-scripts)", "npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund"], pkgDir)
                };
                if (info.HasCompile)
                    steps.Add(new CommandStep("Compile TypeScript", "npm", ["run", "compile"], pkgDir));

                var runStep = info.MainScript is not null && File.Exists(Path.Combine(pkgDir, info.MainScript))
                    ? new CommandStep($"Run {info.MainScript} --help", "node", [Path.Combine(pkgDir, info.MainScript), "--help"], pkgDir)
                    : null;

                var plan = new BuildPlan("MCP Server / Agent CLI", steps, runStep, $"MCP Server / CLI setup · {Path.GetFileName(pkgDir)}");
                return new RepoClassification(RepoKind.LibraryOrSdk, "MCP SERVER / AGENT TOOL", "#6E4599",
                    "This repository is an AI Model Context Protocol (MCP) Server or CLI tool designed to run over stdio inside an AI agent. MyCompiler will install its packages safely and run `--help`.",
                    FindRepresentativeSource(pkgDir, [".ts", ".mjs", ".js"]) ?? pkgJson, plan);
            }

            if (info.HasStart || info.HasDev || info.MainScript is not null)
            {
                var steps = new List<CommandStep>
                {
                    new("Install npm dependencies", "npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund"], pkgDir)
                };
                if (info.HasCompile)
                    steps.Add(new CommandStep("Compile project", "npm", ["run", "compile"], pkgDir));
                else if (info.HasBuild)
                    steps.Add(new CommandStep("Build project", "npm", ["run", "build"], pkgDir));

                CommandStep runStep = info.HasStart
                    ? new CommandStep("Run npm start", "npm", ["run", "start"], pkgDir)
                    : info.MainScript is not null && File.Exists(Path.Combine(pkgDir, info.MainScript))
                        ? new CommandStep($"Run {info.MainScript}", "node", [Path.Combine(pkgDir, info.MainScript)], pkgDir)
                        : new CommandStep("Run npm dev", "npm", ["run", "dev"], pkgDir);

                var plan = new BuildPlan("Node.js App", steps, runStep, $"Node.js automated install & run · {Path.GetFileName(pkgDir)}");
                var entryJs = info.MainScript is not null && File.Exists(Path.Combine(pkgDir, info.MainScript))
                    ? Path.Combine(pkgDir, info.MainScript)
                    : FindRepresentativeSource(pkgDir, [".ts", ".tsx", ".js", ".mjs"]) ?? pkgJson;

                return new RepoClassification(RepoKind.ExecutableApp, "RUNNABLE NODE APP", "#1B6B4A",
                    "Detected runnable Node.js / TypeScript application.", entryJs, plan);
            }

            return new RepoClassification(RepoKind.LibraryOrSdk, "JS/TS PACKAGE / LIBRARY", "#5C4799",
                "This repository is an npm library/package without a standalone app runner. Opening source in workspace.",
                FindRepresentativeSource(pkgDir, [".ts", ".js"]) ?? pkgJson, null);
        }

        // 6. Check static HTML web app
        var indexHtml = allFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("index.html", StringComparison.OrdinalIgnoreCase));
        if (indexHtml is not null)
        {
            return new RepoClassification(RepoKind.WebFrontend, "STATIC WEB APP", "#7A5221",
                $"Detected static web frontend ({Path.GetRelativePath(root, indexHtml)}). Launching in your default browser and opening source.",
                indexHtml, null);
        }

        // 7. Standalone single-file C/C++/Java/Rust/Go/PowerShell
        var standaloneSource = FindStandaloneRunnableFile(allFiles, compiler);
        if (standaloneSource is not null)
        {
            try
            {
                var plan = compiler.CreatePlan(standaloneSource, root);
                return new RepoClassification(RepoKind.ExecutableApp, $"RUNNABLE {plan.Language.ToUpperInvariant()}", "#1B6B4A",
                    $"Detected runnable source file ({Path.GetRelativePath(root, standaloneSource)}).", standaloneSource, plan);
            }
            catch
            {
                return new RepoClassification(RepoKind.ExecutableApp, "SOURCE APP", "#1B6B4A",
                    $"Detected source entry point ({Path.GetRelativePath(root, standaloneSource)}).", standaloneSource, null);
            }
        }

        // 8. Docs / Curated List / Non-App
        return new RepoClassification(RepoKind.DocsOrData, "DOCS / NON-APP REPO", "#3D526B",
            "This repository contains documentation, curated lists, or non-executable skills rather than a standalone Windows application. Opening README in the editor.",
            readme ?? allFiles.FirstOrDefault(), null);
    }

    private static string? InspectAssemblyName(string csprojPath)
    {
        var txt = SafeReadText(csprojPath);
        var match = AssemblyNameRegex().Match(txt);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ChoosePythonEntryFile(string root, List<string> pyFiles)
    {
        string[] preferredNames = ["main.py", "app.py", "run.py", "gui.py", "cli.py", "__main__.py", "start.py", "demo.py", "server.py", "bot.py"];
        foreach (var preferred in preferredNames)
        {
            var match = pyFiles.FirstOrDefault(f => Path.GetFileName(f).Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        foreach (var file in pyFiles)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("setup.py", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("conftest.py", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = SafeReadText(file);
            if (text.Contains("__name__", StringComparison.Ordinal) && text.Contains("__main__", StringComparison.Ordinal))
                return file;
        }

        var topPy = pyFiles.Where(f => Path.GetDirectoryName(f)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                                    && !Path.GetFileName(f).Equals("setup.py", StringComparison.OrdinalIgnoreCase)).ToList();
        return topPy.Count == 1 ? topPy[0] : null;
    }

    private static BuildPlan CreateAutomatedPythonPlan(string root, string entryPy)
    {
        var tool = ToolLocator.Resolve("py", "python") ?? "python";
        var isPyLauncher = Path.GetFileNameWithoutExtension(tool).Equals("py", StringComparison.OrdinalIgnoreCase);
        var prefix = isPyLauncher ? new[] { "-3" } : Array.Empty<string>();

        var steps = new List<CommandStep>();
        var reqFile = Path.Combine(root, "requirements.txt");
        if (File.Exists(reqFile))
        {
            steps.Add(new CommandStep("Install Python requirements.txt", tool,
                [.. prefix, "-m", "pip", "install", "-r", reqFile, "--quiet", "--disable-pip-version-check"], root));
        }

        steps.Add(new CommandStep("Validate Python syntax", tool,
            [.. prefix, "-m", "py_compile", entryPy], Path.GetDirectoryName(entryPy)!));

        var runStep = new CommandStep($"Run {Path.GetFileName(entryPy)}", tool,
            [.. prefix, entryPy], Path.GetDirectoryName(entryPy)!);

        return new BuildPlan("Python App", steps, runStep, $"Automated Python setup & run · {Path.GetFileName(entryPy)}");
    }

    private sealed record PackageJsonInfo(bool HasStart, bool HasDev, bool HasBuild, bool HasCompile, string? MainScript, bool IsMcpServer);

    private static PackageJsonInfo InspectPackageJson(string pkgJsonPath)
    {
        try
        {
            var raw = File.ReadAllText(pkgJsonPath);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            bool hasStart = false, hasDev = false, hasBuild = false, hasCompile = false;
            string? startCmd = null;
            if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
            {
                if (scripts.TryGetProperty("start", out var st))
                {
                    hasStart = true;
                    startCmd = st.GetString();
                }
                hasDev = scripts.TryGetProperty("dev", out _);
                hasBuild = scripts.TryGetProperty("build", out _);
                hasCompile = scripts.TryGetProperty("compile", out _);
            }
            string? main = null;
            if (root.TryGetProperty("bin", out var bin))
            {
                if (bin.ValueKind == JsonValueKind.String) main = bin.GetString();
                else if (bin.ValueKind == JsonValueKind.Object)
                    main = bin.EnumerateObject().FirstOrDefault().Value.GetString();
            }
            var isMcp = root.TryGetProperty("mcpName", out _) ||
                        raw.Contains("@modelcontextprotocol", StringComparison.OrdinalIgnoreCase) ||
                        (startCmd is not null && startCmd.Contains("mcp", StringComparison.OrdinalIgnoreCase));

            return new PackageJsonInfo(hasStart, hasDev, hasBuild, hasCompile, main, isMcp);
        }
        catch
        {
            return new PackageJsonInfo(false, false, false, false, null, false);
        }
    }

    private static string? FindStandaloneRunnableFile(List<string> files, CompilerEngine compiler)
    {
        string[] entryNames = ["main.cs", "Program.cs", "main.cpp", "main.c", "main.rs", "main.go", "Main.java", "main.zig", "main.swift", "main.ps1"];
        foreach (var name in entryNames)
        {
            var match = files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return files.FirstOrDefault(f => compiler.DetectLanguage(f) is not null);
    }

    private static string? FindRepresentativeSource(string root, string[] extensions)
    {
        var files = SafeEnumerateFiles(root, maxDepth: 3);
        return files.FirstOrDefault(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    private static List<string> SafeEnumerateFiles(string root, int maxDepth)
    {
        var result = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && result.Count < 1500)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                result.AddRange(Directory.EnumerateFiles(dir));
                if (depth < maxDepth)
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (name is ".git" or "node_modules" or "bin" or "obj" or "target" or "dist" or ".vs" or ".venv" or "venv")
                            continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch { }
        }
        return result;
    }

    private static string SafeReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    [GeneratedRegex(@"<AssemblyName>([^<]+)</AssemblyName>", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyNameRegex();

    [GeneratedRegex(@"<article\s+class=""Box-row""[\s\S]*?</article>", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex(@"<h2[^>]*>\s*<a[^>]*href=""/([^/""]+)/([^/""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex RepoLinkRegex();

    [GeneratedRegex(@"<p\s+class=""col-9[^""]*""[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"itemprop=""programmingLanguage""[^>]*>([\s\S]*?)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageRegex();

    [GeneratedRegex(@"href=""/[^""]+/stargazers""[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex StarsRegex();

    [GeneratedRegex(@"([\d,]+\s+stars\s+(?:today|this week|this month))", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodStarsRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex ShorthandRepoRegex();

    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/]+)/blob/([^/]+)/(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubBlobRegex();

    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/#?]+)(?:/tree/([^/]+)(?:/(.+))?)?", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRepoUrlRegex();
}
