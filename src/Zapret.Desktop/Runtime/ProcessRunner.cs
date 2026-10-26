using System.Diagnostics;

namespace Zapret.Desktop.Runtime;

public sealed record CommandResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken, string? input = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input != null
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            if (input != null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception error)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (Exception drain) when (drain is OperationCanceledException or IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
            // Only an expired deadline is a timeout; other failures (for example a closed input pipe) keep their own cause.
            if (error is OperationCanceledException) throw new TimeoutException($"Превышено время выполнения: {Path.GetFileName(executable)}.");
            throw;
        }
    }

    public static string SystemTool(string file) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), file);
}
