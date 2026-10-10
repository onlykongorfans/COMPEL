namespace COMPEL.Tests;

public sealed class MatchServerManagerSupervisorTests
{
    [Test]
    public async Task The_Linux_Orphan_Sweep_Finds_The_Full_Process_Name_And_Only_Stops_This_Installations_Process()
    {
        if (OperatingSystem.IsLinux() is false)
            return;

        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-orphan-test-");

        using Process matchingProcess = new ();
        using Process otherInstallationProcess = new ();

        try
        {
            string installationDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "installation")).FullName;
            string otherInstallationDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "other-installation")).FullName;

            StartProcess(matchingProcess, installationDirectory);
            StartProcess(otherInstallationProcess, otherInstallationDirectory);

            IOptions<MatchServerManagerOptions> options = Options.Create(new MatchServerManagerOptions { UseProxy = false });
            PortPlan ports = new (options.Value);
            DistributionSynchronisationService distribution = new
            (
                Options.Create(new CDNOptions { InstallationDirectory = installationDirectory, Synchronisation = false }),
                NullLogger<DistributionSynchronisationService>.Instance
            );

            using UDPProxyService proxy = new (options, ports, NullLogger<UDPProxyService>.Instance);
            using MatchServerManagerSupervisor supervisor = new
            (
                options,
                distribution,
                proxy,
                new AddressResolver(options, NullLogger<AddressResolver>.Instance),
                new ArtefactsLocator(options),
                ports,
                NullLogger<MatchServerManagerSupervisor>.Instance
            );

            // Invoke Only The Sweep So No Hosted Service, Distribution Synchronisation, Or Real Manager Launch Runs.
            MethodInfo sweep = typeof(MatchServerManagerSupervisor).GetMethod("KillOrphanedProcesses", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The Orphan Sweep Method Was Not Found");

            sweep.Invoke(supervisor, null);

            using CancellationTokenSource timeout = new (TimeSpan.FromSeconds(5));
            await matchingProcess.WaitForExitAsync(timeout.Token);

            await Assert.That(otherInstallationProcess.HasExited).IsFalse();
        }

        finally
        {
            await StopProcess(matchingProcess);
            await StopProcess(otherInstallationProcess);
            directory.Delete(recursive: true);
        }
    }

    private static void StartProcess(Process process, string installationDirectory)
    {
        string executable = Path.Combine(installationDirectory, HeroesOfNewerthExecutable.FileName);

        // An Inert Executable Uses The Manager's Long File Name To Exercise Linux Process Discovery Without Launching HoN.
        File.Copy("/usr/bin/sleep", executable);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        process.StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        process.StartInfo.ArgumentList.Add("60");

        if (process.Start() is false)
            throw new InvalidOperationException("The Test Process Failed To Start");
    }

    private static async Task StopProcess(Process process)
    {
        try
        {
            if (process.HasExited is false)
                process.Kill(entireProcessTree: true);
        }

        catch (InvalidOperationException)
        {
            return;
        }

        await process.WaitForExitAsync();
    }
}
