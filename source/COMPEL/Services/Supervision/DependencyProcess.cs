namespace COMPEL.Services.Supervision;

internal sealed record DependencyProcessResult(int ExitCode, string Output, string Error);

/// <summary>
///     Runs dependency tools directly, without shell expansion. Package-manager commands inherit the console; diagnostic commands capture their output.
/// </summary>
internal static class DependencyProcess
{
    internal static ProcessStartInfo Create(string executable, IEnumerable<string> arguments, bool captureOutput = true)
    {
        ProcessStartInfo startInfo = new (executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["LC_ALL"] = "C";

        return startInfo;
    }

    internal static async Task<DependencyProcessResult> Run(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using Process process = new () { StartInfo = startInfo };

        cancellationToken.ThrowIfCancellationRequested();

        if (process.Start() is false)
            throw new InvalidOperationException($"Could Not Start {startInfo.FileName}");

        Task<string> output = startInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        Task<string> error = startInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);

        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }

        catch (OperationCanceledException)
        {
            // Only Read-Only Probes Receive A Cancellation Token. Never Kill APT Halfway Through A Package Transaction.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);

            throw;
        }

        return new DependencyProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
