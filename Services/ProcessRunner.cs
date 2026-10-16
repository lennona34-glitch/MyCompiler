using System.Diagnostics;
using System.IO;
using MyCompiler.Models;

namespace MyCompiler.Services;

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        CommandStep command,
        Action<string, bool> onOutput,
        CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        var executable = ToolLocator.Resolve(command.Executable) ?? command.Executable;
        var isBatch = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                      || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isBatch ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isBatch)
        {
            // cmd.exe /s strips exactly one outer pair of quotes; ArgumentList would escape inner quotes as \" which cmd can't parse.
            startInfo.Arguments = "/d /s /c \"" + string.Join(" ", new[] { executable }.Concat(command.Arguments).Select(QuoteForCommandPrompt)) + "\"";
        }
        else
        {
            foreach (var argument in command.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start {command.Executable}.");
        }
        catch (Exception ex)
        {
            onOutput($"Unable to start '{command.Executable}': {ex.Message}", true);
            return new ProcessResult(-1, DateTimeOffset.UtcNow - start, false);
        }

        var stdout = PumpAsync(process.StandardOutput, line => onOutput(line, false), cancellationToken);
        var stderr = PumpAsync(process.StandardError, line => onOutput(line, true), cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            return new ProcessResult(process.ExitCode, DateTimeOffset.UtcNow - start, false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the checks.
            }

            return new ProcessResult(-1, DateTimeOffset.UtcNow - start, true);
        }
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> write, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null)
                break;
            write(line);
        }
    }

    private static string QuoteForCommandPrompt(string value)
    {
        var escaped = value.Replace("%", "%%").Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}

public static class ToolLocator
{
    public static string? Resolve(params string[] candidates)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
                return candidate;

            foreach (var directory in pathEntries)
            {
                var cleanDirectory = directory.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(cleanDirectory))
                    continue;

                var direct = Path.Combine(cleanDirectory, candidate);
                if (Path.HasExtension(candidate))
                {
                    if (File.Exists(direct))
                        return direct;
                    continue;
                }

                foreach (var extension in extensions)
                {
                    var expanded = direct + extension.ToLowerInvariant();
                    if (File.Exists(expanded))
                        return expanded;
                    expanded = direct + extension.ToUpperInvariant();
                    if (File.Exists(expanded))
                        return expanded;
                }
                if (File.Exists(direct))
                    return direct;
            }
        }

        return null;
    }
}
