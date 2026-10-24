using System.Diagnostics;
using System.Text;

namespace Major.Core;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    internal static ProcessStartInfo CreateInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default, string? input = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var info = CreateInfo(executable, arguments);
        info.RedirectStandardInput = input is not null;
        using var process = new Process { StartInfo = info };
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, 64 * 1024);
        var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException("ADB non ha risposto entro il tempo previsto. Controlla il telefono e riprova.");
        }
        finally
        {
            Kill(process);
            await Task.WhenAll(stdout, stderr);
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            var remaining = limit - text.Length;
            if (remaining > 0) text.Append(buffer, 0, Math.Min(count, remaining));
        }
        return text.ToString();
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
