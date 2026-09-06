namespace COMPEL.Services.Supervision;

internal sealed record DebianCompatibilityPackage(string Name, string FileName, string SHA256)
{
    internal Uri DownloadURI => new ($"https://deb.debian.org/debian/pool/main/n/ncurses/{FileName}");
}

/// <summary>
///     Provides an explicitly requested Debian 13 dependency installation and a read-only, platform-scoped preflight for the deployed LAS binaries.
/// </summary>
internal sealed class Debian13Dependencies
{
    internal const string InstallArgument = "--install-dependencies";

    // These Are ABI-5 Compatibility Packages, Not Replacements Or Symlinks For Debian 13's ABI-6 Libraries. Hashes Come From Debian's Package Download Pages.
    // https://packages.debian.org/bookworm/amd64/libtinfo5/download
    // https://packages.debian.org/bookworm/amd64/libncurses5/download
    internal static readonly IReadOnlyList<DebianCompatibilityPackage> CompatibilityPackages = Array.AsReadOnly<DebianCompatibilityPackage>
    ([
        new ("libtinfo5", "libtinfo5_6.4-4_amd64.deb", "dd347f794e651039e7b4c391f86c674fed7f415b3dca6b0937beb0d470f09c1a"),
        new ("libncurses5", "libncurses5_6.4-4_amd64.deb", "02f4f7f52c4ce2fc4021793a931bfd85f7870554b8e4d56576d73a4ed0bdb390")
    ]);

    private readonly bool supportedHost;
    private readonly bool privileged;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<DependencyProcessResult>> runProcess;
    private readonly Func<DebianCompatibilityPackage, string, Task> downloadPackage;

    internal Debian13Dependencies() : this(Debian13Platform.IsSupportedHost(), Environment.IsPrivilegedProcess, DependencyProcess.Run, DownloadPackage) { }

    internal Debian13Dependencies(bool supportedHost, bool privileged,
        Func<ProcessStartInfo, CancellationToken, Task<DependencyProcessResult>> runProcess,
        Func<DebianCompatibilityPackage, string, Task> downloadPackage)
    {
        this.supportedHost = supportedHost;
        this.privileged = privileged;
        this.runProcess = runProcess;
        this.downloadPackage = downloadPackage;
    }

    internal async Task Install(string installationDirectory, TextWriter output)
    {
        // Reject Unsupported Hosts Before Creating Files, Invoking Package Tools, Or Making Network Requests.
        if (supportedHost is false)
            throw new PlatformNotSupportedException("Dependency Installation Is Supported Only On Debian 13 x64 (amd64). No Changes Were Made.");

        if (privileged is false)
            throw new InvalidOperationException("Run COMPEL --install-dependencies As Root. No Changes Were Made.");

        if (SingleInstanceGuard.TryAcquire(Path.Combine(installationDirectory, "COMPEL.lock"), out SingleInstanceGuard guard) is false)
            throw new InvalidOperationException("Stop COMPEL For This Installation Before Running --install-dependencies.");

        using (guard)
        {
            DependencyProcessResult architecture = await runProcess(DependencyProcess.Create("/usr/bin/dpkg", ["--print-architecture"]), CancellationToken.None).ConfigureAwait(false);

            if (architecture.ExitCode is not 0 || architecture.Output.Trim() != "amd64")
                throw new PlatformNotSupportedException("The Debian Package Architecture Must Be amd64. No Packages Were Changed.");

            List<string> repositoryPackages = [];
            List<DebianCompatibilityPackage> missingCompatibilityPackages = [];

            foreach (string package in new[] { "libfontconfig1", "libfreetype6" })
                if (await IsInstalled(package).ConfigureAwait(false) is false)
                    repositoryPackages.Add(package + ":amd64");

            foreach (DebianCompatibilityPackage package in CompatibilityPackages)
                if (await IsInstalled(package.Name).ConfigureAwait(false) is false)
                    missingCompatibilityPackages.Add(package);

            if (repositoryPackages.Count is 0 && missingCompatibilityPackages.Count is 0)
            {
                await output.WriteLineAsync("All Debian 13 Dependency Packages Are Already Installed; No Package Changes Were Needed.").ConfigureAwait(false);
                return;
            }

            DirectoryInfo temporaryDirectory = Directory.CreateTempSubdirectory("COMPEL-dependencies-");

            try
            {
                // APT's Unprivileged Download User Must Be Able To Traverse This Directory And Read The Verified Local Packages.
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(temporaryDirectory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

                List<string> packageArguments = [.. repositoryPackages];

                foreach (DebianCompatibilityPackage package in missingCompatibilityPackages)
                {
                    string destination = Path.Combine(temporaryDirectory.FullName, package.FileName);
                    await output.WriteLineAsync($"Downloading And Verifying {package.FileName} From {package.DownloadURI}").ConfigureAwait(false);
                    await downloadPackage(package, destination).ConfigureAwait(false);
                    packageArguments.Add(destination);
                }

                // Download And Verify Every Legacy Package Before APT Can Modify The Host. Do Not Add An Older Debian Repository Or Permit Package Removals/Downgrades.
                await RunAPT(["-o", "APT::Update::Error-Mode=any", "update"]).ConfigureAwait(false);
                await RunAPT(["-o", "DPkg::Lock::Timeout=60", "install", "--yes", "--no-remove", "--no-upgrade", "--no-install-recommends", .. packageArguments]).ConfigureAwait(false);

                foreach (string package in new[] { "libfontconfig1", "libfreetype6", "libtinfo5", "libncurses5" })
                    if (await IsInstalled(package).ConfigureAwait(false) is false)
                        throw new InvalidOperationException($"APT Finished But {package}:amd64 Is Not Installed. Inspect The APT Output Before Retrying.");

                await output.WriteLineAsync("Debian 13 Dependency Installation Complete.").ConfigureAwait(false);
            }

            finally
            {
                try { temporaryDirectory.Delete(recursive: true); }
                catch (IOException) { await output.WriteLineAsync($"Could Not Remove Temporary Downloads At {temporaryDirectory.FullName}").ConfigureAwait(false); }
                catch (UnauthorizedAccessException) { await output.WriteLineAsync($"Could Not Remove Temporary Downloads At {temporaryDirectory.FullName}").ConfigureAwait(false); }
            }
        }
    }

    private async Task<bool> IsInstalled(string package)
    {
        // The Desired Selection Can Be "hold" Even When A Package Is Fully Installed. Inspect The Actual Installation State So Held Packages Are Not Reinstalled.
        DependencyProcessResult result = await runProcess(DependencyProcess.Create("/usr/bin/dpkg-query", ["--show", "--showformat=${db:Status-Status}\t${Architecture}", package + ":amd64"]), CancellationToken.None).ConfigureAwait(false);

        if (result.ExitCode is not 0 and not 1)
            throw new InvalidOperationException($"Could Not Query Installed Packages: {result.Error.Trim()}");

        return result.ExitCode is 0 && result.Output.Trim() == "installed\tamd64";
    }

    private async Task RunAPT(string[] arguments)
    {
        DependencyProcessResult result = await runProcess(DependencyProcess.Create("/usr/bin/apt-get", arguments, captureOutput: false), CancellationToken.None).ConfigureAwait(false);

        if (result.ExitCode is not 0)
            throw new InvalidOperationException($"APT Exited With Code {result.ExitCode}. Inspect Its Output; COMPEL Has Not Started Any Match Servers.");
    }

    private static async Task DownloadPackage(DebianCompatibilityPackage package, string destination)
    {
        using HttpClient client = new () { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 1024 * 1024 };

        await DownloadPackage(client, package, destination).ConfigureAwait(false);
    }

    internal static async Task DownloadPackage(HttpClient client, DebianCompatibilityPackage package, string destination)
    {
        byte[] contents = await client.GetByteArrayAsync(package.DownloadURI).ConfigureAwait(false);
        string actualHash = Convert.ToHexString(SHA256.HashData(contents));

        if (string.Equals(actualHash, package.SHA256, StringComparison.OrdinalIgnoreCase) is false)
            throw new InvalidDataException($"SHA-256 Verification Failed For {package.FileName}; No Packages Were Installed.");

        await File.WriteAllBytesAsync(destination, contents).ConfigureAwait(false);

        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    internal async Task<string?> Check(ProcessStartInfo managerStartInfo, CancellationToken cancellationToken)
    {
        if (supportedHost is false)
            return null;

        // Check Only The x64 Server Components, Not Optional Client/32-Bit Tools Which May Intentionally Lack Dependencies On A Headless Host.
        string[] targets =
        [
            managerStartInfo.FileName,
            Path.Combine(managerStartInfo.WorkingDirectory, "libk2-x86_64-server.so"),
            Path.Combine(managerStartInfo.WorkingDirectory, "game", "game-x86_64-server.so"),
            Path.Combine(managerStartInfo.WorkingDirectory, "game", "libgame_shared-x86_64-server.so"),
            Path.Combine(managerStartInfo.WorkingDirectory, "linux_server", "hcon")
        ];

        try
        {
            foreach (string target in targets)
            {
                if (target != managerStartInfo.FileName && File.Exists(target) is false)
                    continue;

                // The Loader's "--list" Mode Resolves Dependencies Without Starting HoN Or Running Its Constructors. Preserve The Launch Environment And $ORIGIN Resolution Rather Than Guessing Library Search Paths.
                ProcessStartInfo startInfo = DependencyProcess.Create("/lib64/ld-linux-x86-64.so.2", ["--list", target]);
                startInfo.WorkingDirectory = managerStartInfo.WorkingDirectory;
                startInfo.Environment.Clear();

                foreach (KeyValuePair<string, string?> variable in managerStartInfo.Environment)
                    startInfo.Environment[variable.Key] = variable.Value;

                startInfo.Environment["LC_ALL"] = "C";

                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                DependencyProcessResult result = await runProcess(startInfo, timeout.Token).ConfigureAwait(false);

                if (result.ExitCode is not 0 || result.Output.Contains("=> not found", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(result.Error) is false)
                    return $"{target}: {(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()} (Exit Code {result.ExitCode})";
            }

            return null;
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return "The Debian 13 Dependency Check Timed Out."; }
        catch (Exception exception) { return $"Could Not Complete The Debian 13 Dependency Check: {exception.Message}"; }
    }
}
