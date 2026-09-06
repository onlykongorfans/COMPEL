namespace COMPEL.Services.Supervision;

/// <summary>
///     Launches and supervises the Heroes Of Newerth match server manager process.
///     It waits for the distribution to be synchronised, resolves the server address, then keeps the manager running, restarting it (with backoff) if it exits unexpectedly or fails to start in the first place.
///     The control plane drives <see cref="RequestStart"/>, <see cref="RequestStop"/>, and <see cref="RequestRestart"/>; on shutdown the manager and its spawned servers are stopped.
/// </summary>
public sealed class MatchServerManagerSupervisor : BackgroundService
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LinuxIdleActivationDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LinuxStartupCommandRetryDelay = TimeSpan.FromSeconds(5);

    private const string LinuxConsoleDirectory = "/var/run/hon";
    private const string LinuxManagerConsoleName = "manager";

    private readonly MatchServerManagerOptions options;
    private readonly DistributionSynchronisationService distribution;
    private readonly UDPProxyService proxy;
    private readonly AddressResolver addressResolver;
    private readonly ArtefactsLocator artefacts;
    private readonly ILogger<MatchServerManagerSupervisor> logger;
    private readonly Debian13Dependencies dependencies = new ();

    private readonly SemaphoreSlim reconcileSignal = new (0, int.MaxValue);
    private readonly SemaphoreSlim lifecycleGate = new (1, 1);

    private volatile bool desiredRunning = true;
    private volatile bool managerRunning;
    private Process? managerProcess;
    private CancellationTokenSource? linuxStartupCompletionCancellation;
    private long lastAttemptTicks;

    public MatchServerManagerSupervisor(IOptions<MatchServerManagerOptions> options, DistributionSynchronisationService distribution, UDPProxyService proxy, AddressResolver addressResolver, ArtefactsLocator artefacts, PortPlan ports, ILogger<MatchServerManagerSupervisor> logger)
    {
        this.options = options.Value;
        this.distribution = distribution;
        this.proxy = proxy;
        this.addressResolver = addressResolver;
        this.artefacts = artefacts;
        this.logger = logger;

        Ports = ports;
    }

    public PortPlan Ports { get; }

    public string? ServerAddress { get; private set; }

    // Backed By A Volatile Flag Maintained By The Launch, Exit, And Stop Paths Rather Than Reading "Process.HasExited" Live: The Control Plane And Health Checks Read This From Other Threads, And Touching A "Process" Instance That The Launch Or Stop Path Is Concurrently Disposing Would Throw.
    public bool IsRunning => managerRunning;

    /// <summary>
    ///     Whether the manager is intended to be running. Distinct from <see cref="IsRunning"/>: a crashed manager reports <see cref="IsRunning"/> as <see langword="false"/> while this stays <see langword="true"/> because the reconcile loop will relaunch it. The control plane requires this to be <see langword="false"/> before a synchronisation is accepted.
    /// </summary>
    public bool DesiredRunning => desiredRunning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        KillOrphanedProcesses();

        logger.LogInformation("Waiting For The Match Server Distribution To Be Ready");

        try { await distribution.WaitUntilReady(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Resolve The Advertised Address, Retrying With Backoff Rather Than Giving Up: A Transient Failure (For Example A DNS Hiccup Or A Public-IP Lookup Timing Out) At Startup Must Not Permanently Disable The Supervisor While COMPEL Keeps Running And Reporting Success.
        while (stoppingToken.IsCancellationRequested is false)
        {
            try
            {
                ServerAddress = await addressResolver.ResolveServerAddress(stoppingToken).ConfigureAwait(false);

                break;
            }

            catch (OperationCanceledException)
            {
                return;
            }

            catch (Exception exception)
            {
                logger.LogError(exception, "Unable To Resolve The Server Address; Retrying In {Seconds} Second(s)", RestartBackoff.TotalSeconds);

                try { await Task.Delay(RestartBackoff, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        LogPortAllocation();

        // When The Proxy Is Enabled The Manager Advertises Public Ports (Local + 10000) That Only Work If The Proxy Bound Them. If No Forwarder Could Bind, Launching The Manager Would Register Unreachable Public Ports With The Master Server, So The Launch Is Refused Instead.
        if (options.UseProxy)
        {
            bool proxyReady;

            try { proxyReady = await proxy.WaitUntilReady(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (proxyReady is false)
            {
                logger.LogError("The Proxy Is Enabled But No Public Port Could Be Bound; The Manager Will Not Be Launched");

                return;
            }
        }

        // Event-Driven Reconcile Loop: Each Pass Brings The Process State Into Line With The Desired State, Then Waits For The Next Change (A Process Exit Or A Control-Plane Request).
        // Whenever The Manager Should Be Running But Isn't, The Wait Is Bounded By "RestartBackoff" So A Launch Failure (E.G. A Missing Executable) Retries Automatically Instead Of Stalling Forever With No Signal To Wake It.
        while (stoppingToken.IsCancellationRequested is false)
        {
            await Reconcile(stoppingToken).ConfigureAwait(false);

            TimeSpan wakeTimeout = desiredRunning && IsRunning is false ? RestartBackoff : Timeout.InfiniteTimeSpan;

            try { await reconcileSignal.WaitAsync(wakeTimeout, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        StopProcess();
    }

    private async Task Reconcile(CancellationToken stoppingToken)
    {
        await lifecycleGate.WaitAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            if (desiredRunning && IsRunning is false)
            {
                // Never Launch While A Synchronisation Is Rewriting The Installation Directory: The Servers Would Hold Files The Synchronisation Is Replacing. The Reconcile Loop Retries After The Backoff. This Is Not Counted As A Launch Attempt, So The Backoff Is Not Consumed While Waiting.
                if (distribution.IsSynchronising)
                    return;

                long elapsed = Environment.TickCount64 - lastAttemptTicks;

                if (lastAttemptTicks is not 0 && elapsed < RestartBackoff.TotalMilliseconds)
                {
                    TimeSpan wait = RestartBackoff - TimeSpan.FromMilliseconds(elapsed);

                    logger.LogWarning("Restarting The Match Server Manager In {Seconds:F0} Second(s)", wait.TotalSeconds);

                    try { await Task.Delay(wait, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }

                // Recorded Before The Attempt, Not Only On Success, So A Launch That Throws Still Enforces The Backoff On The Next Attempt Rather Than Retrying In A Tight Loop.
                lastAttemptTicks = Environment.TickCount64;

                await LaunchProcess(stoppingToken).ConfigureAwait(false);
            }

            else if (desiredRunning is false && IsRunning)
            {
                StopProcess();
            }
        }

        catch (Exception exception)
        {
            logger.LogError(exception, "Failed To Reconcile The Match Server Manager State");
        }

        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task LaunchProcess(CancellationToken cancellationToken)
    {
        string executable = distribution.ManagerExecutablePath;

        if (File.Exists(executable) is false)
            throw new FileNotFoundException($@"Manager Executable Was Not Found At ""{executable}""");

        // The Synchronised Server Binary Arrives Without The Unix Execute Bit, So It Is Set Here Before Every Launch (On Windows This Is A No-Op).
        EnsureExecutable(executable);

        string address = ServerAddress ?? throw new InvalidOperationException("The Server Address Has Not Been Resolved");

        // Dispose The Previous Exited Process Object Before Replacing It.
        Process? previous = managerProcess;

        if (previous is not null)
        {
            previous.Exited -= OnProcessExited;
            previous.Dispose();
        }

        // Best-Effort: Heroes Of Newerth Creates Its Own Artefacts Directory On Launch, So A Failure To Pre-Create It (For Example A Permissions Issue On The Fixed Linux Location) Must Not Block The Launch.
        try { Directory.CreateDirectory(artefacts.ArtefactsDirectory); }
        catch (Exception exception) { logger.LogDebug(exception, "Could Not Pre-Create The Artefacts Directory {Directory}", artefacts.ArtefactsDirectory); }

        // Sweep Before Every Launch, Not Only At Startup: A Previous Manager's Spawned Servers Can Be Reparented (For Example To The Init Process) And So Escape "Process.Kill(entireProcessTree)", Leaving Them Holding The Ports This Launch Is About To Bind.
        KillOrphanedProcesses();

        PrepareLinuxConsoleDirectory();

        // Runtime Changes To "man_maxServers" Are Stored By CowMaster But Do Not Trigger Creation Of Another Slave, So The Full Configured Capacity Must Be Present At Initialisation. On Linux, Waking A Slave While CowMaster Is Still Forking The Remaining Capacity Can Lose The First Child While Its File Descriptors Remain Open. Start The Pool Sleeping With Native Respawn Temporarily Disabled, Then Activate The Configured Idle Target And Restore Respawn After The Fork Burst Has Settled.
        bool linuxForkWorkaround = OperatingSystem.IsLinux();
        string[] arguments = ManagerArguments.Build
        (
            options,
            Ports,
            address,
            addressResolver.MasterServerHostAndPort,
            disableNativeRespawn: linuxForkWorkaround,
            initialIdleTarget: linuxForkWorkaround ? 0 : null
        );

        ProcessStartInfo startInfo = new (executable)
        {
            WorkingDirectory = distribution.InstallationDirectory,
            UseShellExecute = false
        };

        // Windows Receives The Command Line Verbatim, So Heroes Of Newerth Sees The Literal Quotes Around The "-execute" Payload. On Linux ".NET" Strips Grouping Quotes From A Joined String, So Each Argument Is Passed Individually Via "ArgumentList" And The Payload Retains Its Own Literal Quotes When Heroes Of Newerth Rejoins Its Command Line.
        if (OperatingSystem.IsWindows())
            startInfo.Arguments = string.Join(' ', arguments);
        else
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

        // Heroes Of Newerth Derives Its Documents Tree From The Home Directory, So The Child's Home Is Redirected To The Resolved Artefacts Location.
        if (OperatingSystem.IsWindows())
            startInfo.EnvironmentVariables["USERPROFILE"] = artefacts.ProfileDirectory;
        else
            startInfo.EnvironmentVariables["HOME"] = artefacts.ProfileDirectory;

        // Compatible LAS Distributions Carry An ABI-Specific libc++ Interposer Which Replaces The Fixed, Fork-Inherited Shuffle RNG Stream With A PID-Aware Stream. Apply It Only To CowMaster's Child Environment: CowMaster Loads It Before libc++, And Every Forked Slave Inherits The Mapping Without Loading The Shim Into COMPEL Or Requiring A Global LD_PRELOAD Setting.
        if (OperatingSystem.IsLinux())
        {
            string expectedShimPath = Path.Combine(distribution.InstallationDirectory, LinuxRngCompatibility.ShimRelativePath);
            string? activatedShimPath = LinuxRngCompatibility.Apply(startInfo, distribution.InstallationDirectory, isLinux: true);

            if (activatedShimPath is not null)
                logger.LogInformation("Activated The Linux Fork-Safe Shuffle RNG Compatibility Shim {Path}", activatedShimPath);
            else
                logger.LogWarning("The Linux Fork-Safe Shuffle RNG Compatibility Shim Was Not Found At {Path}; CowMaster Will Start Without It", expectedShimPath);
        }

        string? dependencyFailure = await dependencies.Check(startInfo, cancellationToken).ConfigureAwait(false);

        // A Stop Request Can Arrive While The Read-Only Dependency Probe Is Awaited. Do Not Launch A New Manager After That Request Or During Application Shutdown.
        cancellationToken.ThrowIfCancellationRequested();

        if (desiredRunning is false)
            return;

        if (dependencyFailure is not null)
        {
            // A Missing Host Library Cannot Be Repaired By Respawning HoN. Pause Automatic Launches, But Leave The Control Plane Available So An Operator Can Retry After Repairing Dependencies.
            desiredRunning = false;
            logger.LogError("Match Server Launch Paused: {Reason}. Stop COMPEL, Run ./COMPEL --install-dependencies As Root, Then Start COMPEL Again. After A Manual Repair, /instances/start Also Retries The Check", dependencyFailure);
            return;
        }

        Process process = new () { StartInfo = startInfo, EnableRaisingEvents = true };

        process.Exited += OnProcessExited;

        if (process.Start() is false)
            throw new InvalidOperationException("The Match Server Manager Process Failed To Start");

        managerProcess = process;
        managerRunning = true;

        logger.LogInformation("Launched The Match Server Manager (Process {ProcessID})", process.Id);

        StartLinuxStartupCompletion(process);

        // If The Process Exited Between "Start" And Now, The "Exited" Event May Have Already Run And Cleared The Running Flag Before This Method Set It. Re-Check So An Instantly-Exiting Manager Does Not Leave The State Stuck Reporting Running With No Live Process, And Wake The Reconcile Loop To Apply The Backoff And Relaunch.
        if (process.HasExited)
        {
            managerRunning = false;

            reconcileSignal.Release();
        }
    }

    private void StartLinuxStartupCompletion(Process process)
    {
        CancelLinuxStartupCompletion();

        if (OperatingSystem.IsLinux() is false)
            return;

        CancellationTokenSource cancellation = new ();

        linuxStartupCompletionCancellation = cancellation;

        _ = CompleteLinuxStartupWorkaround(process, cancellation.Token);
    }

    private async Task CompleteLinuxStartupWorkaround(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(LinuxIdleActivationDelay, cancellationToken).ConfigureAwait(false);

            // These Two Commands Are Deliberately Tracked Independently. A Transient Failure To Activate The Idle Pool Must Not Leave Native Slave Replacement Disabled, And A Successful Respawn Command Must Not Prevent A Failed Idle Command From Being Retried.
            bool idleTargetActivated = options.IdleTarget is 0;
            bool nativeRespawnRestored = false;

            while (cancellationToken.IsCancellationRequested is false && IsCurrentManager(process) && (idleTargetActivated is false || nativeRespawnRestored is false))
            {
                if (idleTargetActivated is false)
                {
                    idleTargetActivated = await TrySendLinuxStartupCommand
                    (
                        $"Set man_idleTarget {options.IdleTarget}",
                        "Activate The Configured CowMaster Idle Target",
                        cancellationToken
                    ).ConfigureAwait(false);

                    if (idleTargetActivated)
                        logger.LogInformation("Activated CowMaster Idle Target Of {IdleTarget} Instance(s) After The Linux Fork Startup Window", options.IdleTarget);
                }

                if (nativeRespawnRestored is false)
                {
                    nativeRespawnRestored = await TrySendLinuxStartupCommand
                    (
                        "Set man_respawnServers true",
                        "Restore Native CowMaster Slave Respawning",
                        cancellationToken
                    ).ConfigureAwait(false);

                    if (nativeRespawnRestored)
                        logger.LogInformation("Restored Native CowMaster Slave Respawning After The Linux Fork Startup Window");
                }

                if (idleTargetActivated is false || nativeRespawnRestored is false)
                    await Task.Delay(LinuxStartupCommandRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected When The Manager Stops Or Restarts Before The Delayed Activation Completes.
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could Not Complete The Linux CowMaster Startup Workaround");
        }
    }

    private bool IsCurrentManager(Process process) => IsRunning && ReferenceEquals(managerProcess, process);

    private async Task<bool> TrySendLinuxStartupCommand(string command, string operation, CancellationToken cancellationToken)
    {
        try
        {
            await SendLinuxManagerCommand(command, cancellationToken).ConfigureAwait(false);

            return true;
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        catch (Exception exception)
        {
            // The Manager Socket And HCon Helper Can Be Briefly Unavailable While The Native Process Finishes Initialising. Retrying Keeps COMPEL's Desired Idle Capacity And Long-Term Respawn Supervision From Depending On One Timing-Sensitive Command.
            logger.LogWarning
            (
                exception,
                "Could Not {Operation}; Retrying In {Seconds} Second(s)",
                operation,
                LinuxStartupCommandRetryDelay.TotalSeconds
            );

            return false;
        }
    }

    private async Task SendLinuxManagerCommand(string command, CancellationToken cancellationToken)
    {
        string helper = Path.Combine(distribution.InstallationDirectory, "linux_server", "hcon");

        if (File.Exists(helper) is false)
            throw new FileNotFoundException("The Heroes Of Newerth HCon Helper Was Not Found", helper);

        EnsureExecutable(helper);

        string managerSocketPath = Path.Combine(LinuxConsoleDirectory, LinuxManagerConsoleName + ".sock");

        if (File.Exists(managerSocketPath) is false)
            throw new InvalidOperationException($@"The Match Server Manager Console Socket Was Not Found At ""{managerSocketPath}""");

        ProcessStartInfo startInfo = new (helper)
        {
            WorkingDirectory = distribution.InstallationDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(LinuxManagerConsoleName);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);

        using Process console = new () { StartInfo = startInfo };

        if (console.Start() is false)
            throw new InvalidOperationException("The Heroes Of Newerth HCon Helper Failed To Start");

        Task<string> standardOutput = console.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = console.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try { await console.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
        {
            try { console.Kill(); }
            catch { /* Best-Effort Cleanup Of A Hung Helper. */ }

            throw new TimeoutException("The Heroes Of Newerth HCon Helper Did Not Return Within Ten Seconds");
        }

        string output = (await standardOutput.ConfigureAwait(false)).Trim();
        string error = (await standardError.ConfigureAwait(false)).Trim();

        if (console.ExitCode is not 0)
            throw new InvalidOperationException($"The Heroes Of Newerth HCon Helper Exited With Code {console.ExitCode}: {error}");

        if (string.IsNullOrWhiteSpace(output) is false)
            logger.LogDebug("HCon: {Output}", output);
    }

    private void PrepareLinuxConsoleDirectory()
    {
        if (OperatingSystem.IsLinux() is false)
            return;

        try
        {
            Directory.CreateDirectory(LinuxConsoleDirectory);

            // HoN Does Not Remove Its Unix Console Socket After A Forced Exit. Delete Only Socket Files Which The Kernel No Longer Reports As Active, Preserving Consoles Belonging To Any Other Live HoN Installation On The Host.
            HashSet<string> activeSockets = new (StringComparer.Ordinal);

            foreach (string line in File.ReadLines("/proc/net/unix"))
            {
                int pathIndex = line.IndexOf('/');

                if (pathIndex >= 0)
                    activeSockets.Add(line[pathIndex..].Trim());
            }

            foreach (string socket in Directory.EnumerateFiles(LinuxConsoleDirectory, "*.sock"))
                if (activeSockets.Contains(socket) is false)
                    File.Delete(socket);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could Not Prepare The Heroes Of Newerth Local Console Directory {Directory}", LinuxConsoleDirectory);
        }
    }

    private void CancelLinuxStartupCompletion()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref linuxStartupCompletionCancellation, null);

        if (cancellation is null)
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    /// <summary>
    ///     Ensures the executable carries the Unix execute bit before it is launched. This is a no-op on Windows, where the concept does not apply.
    /// </summary>
    private void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            UnixFileMode currentMode = File.GetUnixFileMode(path);
            UnixFileMode executableMode = currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            if (executableMode != currentMode)
                File.SetUnixFileMode(path, executableMode);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could Not Set The Execute Bit On {Path}", path);
        }
    }

    /// <summary>
    ///     Kills any match server manager process left over from a previous, uncleanly-terminated run (a crash, a forced kill, a host reboot), identified by matching its executable path against this installation's manager executable, so it cannot keep holding the ports the new manager is about to bind.
    /// </summary>
    private void KillOrphanedProcesses()
    {
        string executableName = Path.GetFileNameWithoutExtension(HeroesOfNewerthExecutable.FileName);

        // Linux Exposes The Process Name Via "/proc/[pid]/comm", Which Is Truncated To 15 Characters, So The Lookup Name Is Truncated To Match; The Executable-Path Comparison Below Still Confirms The Process Identity.
        if (OperatingSystem.IsLinux() && executableName.Length > 15)
            executableName = executableName[..15];

        string executablePath = distribution.ManagerExecutablePath;

        foreach (Process process in Process.GetProcessesByName(executableName))
        {
            try
            {
                string? modulePath = process.MainModule?.FileName;

                if (string.Equals(modulePath, executablePath, StringComparison.OrdinalIgnoreCase) is false)
                    continue;

                logger.LogWarning("Killing An Orphaned Match Server Process (Process {ProcessID}) Left Over From A Previous Run", process.Id);

                process.Kill(entireProcessTree: true);
            }

            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed To Inspect Or Kill A Potential Orphan Process (Process {ProcessID})", process.Id);
            }

            finally
            {
                process.Dispose();
            }
        }
    }

    private void StopProcess()
    {
        managerRunning = false;
        CancelLinuxStartupCompletion();

        Process? process = Interlocked.Exchange(ref managerProcess, null);

        if (process is null)
            return;

        process.Exited -= OnProcessExited;

        try
        {
            if (process.HasExited is false)
            {
                logger.LogInformation("Stopping The Match Server Manager (Process {ProcessID})", process.Id);

                process.Kill(entireProcessTree: true);
            }
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed To Stop The Match Server Manager Cleanly");
        }

        finally
        {
            process.Dispose();
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArguments)
    {
        managerRunning = false;
        CancelLinuxStartupCompletion();

        reconcileSignal.Release();
    }

    /// <summary>
    ///     Requests that the manager be running.
    /// </summary>
    public void RequestStart()
    {
        desiredRunning = true;

        reconcileSignal.Release();
    }

    /// <summary>
    ///     Requests that the manager be stopped and kept stopped.
    /// </summary>
    public void RequestStop()
    {
        desiredRunning = false;

        reconcileSignal.Release();
    }

    /// <summary>
    ///     Requests that the manager be restarted. Killing the running process triggers the reconcile loop to relaunch it.
    ///     Acquires the same lifecycle gate as <see cref="Reconcile"/> so this cannot race a concurrent launch or stop for the same process.
    /// </summary>
    public async Task RequestRestart(CancellationToken cancellationToken)
    {
        desiredRunning = true;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Process? process = managerProcess;

            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }

        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed To Signal A Restart To The Match Server Manager");
        }

        finally
        {
            lifecycleGate.Release();
        }

        reconcileSignal.Release();
    }

    private void LogPortAllocation()
    {
        logger.LogInformation
        (
            "Match Server Manager Configured: {Instances} Instance(s), {IdleTarget} Kept IDLE, Server Address {ServerAddress}, Master Server {MasterServer}",
            options.Instances, options.IdleTarget, ServerAddress, addressResolver.MasterServerHostAndPort
        );

        logger.LogInformation
        (
            "Port Allocation: Game {LocalGameStart}-{LocalGameEnd}, Voice {LocalVoiceStart}-{LocalVoiceEnd}, Public Game {PublicGameStart}-{PublicGameEnd}, Public Voice {PublicVoiceStart}-{PublicVoiceEnd}, Ping {PingPort}{ProxyNote}",
            Ports.LocalGameStart, Ports.LocalGameEnd, Ports.LocalVoiceStart, Ports.LocalVoiceEnd,
            Ports.PublicGameStart, Ports.PublicGameEnd, Ports.PublicVoiceStart, Ports.PublicVoiceEnd,
            Ports.PingPort, Ports.UseProxy ? " (Proxy Enabled)" : string.Empty
        );

        logger.LogInformation("Runtime Artefacts Directory: {Directory}", artefacts.ArtefactsDirectory);

        if (artefacts.RuntimeArtefactsPathApplies is false)
            logger.LogInformation(@"The ""RuntimeArtefactsPath"" Setting Does Not Apply On This Platform; The Heroes Of Newerth Server Build Writes To A Fixed Location");
    }
}
