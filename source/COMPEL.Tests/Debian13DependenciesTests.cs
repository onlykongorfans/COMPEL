namespace COMPEL.Tests;

public sealed class Debian13DependenciesTests
{
    [Test]
    public async Task Only_Debian_13_On_A_Native_X64_Host_Is_Supported()
    {
        await Assert.That(Debian13Platform.IsSupported(true, Architecture.X64, Architecture.X64, "ID=debian\nVERSION_ID=\"13\"\n")).IsTrue();
        await Assert.That(Debian13Platform.IsSupported(true, Architecture.X64, Architecture.X64, "# Comment\r\nID='debian'\r\nVERSION_ID=13\r\n")).IsTrue();

        foreach (string release in new[]
        {
            "ID=ubuntu\nID_LIKE=debian\nVERSION_ID=13",
            "ID=linuxmint\nID_LIKE=debian\nVERSION_ID=13",
            "ID=debian\nVERSION_ID=12",
            "ID=debian\nVERSION_ID=14",
            "ID=debian\nVERSION_ID=13.1",
            "ID_LIKE=debian\nVERSION_ID=13",
            "ID=debian\nVERSION_ID=\"13",
            "ID=debian\nVERSION_ID=$(echo 13)",
            "ID=debian", "", "ID=fedora\nVERSION_ID=43"
        })
            await Assert.That(Debian13Platform.IsSupported(true, Architecture.X64, Architecture.X64, release)).IsFalse();

        await Assert.That(Debian13Platform.IsSupported(false, Architecture.X64, Architecture.X64, "ID=debian\nVERSION_ID=13")).IsFalse();
        await Assert.That(Debian13Platform.IsSupported(true, Architecture.Arm64, Architecture.X64, "ID=debian\nVERSION_ID=13")).IsFalse();
        await Assert.That(Debian13Platform.IsSupported(true, Architecture.X64, Architecture.X86, "ID=debian\nVERSION_ID=13")).IsFalse();
        await Assert.That(Debian13Platform.IsSupported(true, Architecture.Arm64, Architecture.Arm64, "ID=debian\nVERSION_ID=13")).IsFalse();
    }

    [Test]
    public async Task Unsupported_And_Unprivileged_Setup_Cannot_Run_Commands_Or_Create_Files()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-dependency-test-{Guid.NewGuid():N}");
        FakeHost host = new ();

        await Assert.That(() => host.Create(supportedHost: false).Install(directory, TextWriter.Null)).Throws<PlatformNotSupportedException>();
        await Assert.That(() => host.Create(privileged: false).Install(directory, TextWriter.Null)).Throws<InvalidOperationException>();
        await Assert.That(host.Commands.Count).IsEqualTo(0);
        await Assert.That(host.Downloads.Count).IsEqualTo(0);
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    [Test]
    public async Task Unsupported_Preflight_Does_Not_Inspect_Binaries_Or_Run_Commands()
    {
        FakeHost host = new ();
        ProcessStartInfo startInfo = new ("not-a-real-executable");
        startInfo.Environment["LD_PRELOAD"] = "unchanged";

        await Assert.That(await host.Create(supportedHost: false).Check(startInfo, CancellationToken.None)).IsNull();
        await Assert.That(host.Commands.Count).IsEqualTo(0);
        await Assert.That(startInfo.Environment["LD_PRELOAD"]).IsEqualTo("unchanged");
    }

    [Test]
    public async Task Setup_Refuses_An_Installation_Already_Locked_By_COMPEL()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            await Assert.That(SingleInstanceGuard.TryAcquire(Path.Combine(directory.FullName, "COMPEL.lock"), out SingleInstanceGuard guard)).IsTrue();

            using (guard)
            {
                FakeHost host = new ();
                await Assert.That(() => host.Create().Install(directory.FullName, TextWriter.Null)).Throws<InvalidOperationException>();
                await Assert.That(host.Commands.Count).IsEqualTo(0);
            }
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Already_Installed_Packages_Do_Not_Trigger_Downloads_Or_APT()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            FakeHost host = new () { PackagesInstalled = true };
            await host.Create().Install(directory.FullName, TextWriter.Null);
            await Assert.That(host.Downloads.Count).IsEqualTo(0);
            await Assert.That(host.Commands.Any(command => command.FileName == "/usr/bin/apt-get")).IsFalse();
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Package_Architecture_Is_Checked_Before_Any_Download_Or_APT_Call()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            FakeHost host = new () { PackageArchitecture = "arm64" };
            await Assert.That(() => host.Create().Install(directory.FullName, TextWriter.Null)).Throws<PlatformNotSupportedException>();
            await Assert.That(host.Commands.Count).IsEqualTo(1);
            await Assert.That(host.Downloads.Count).IsEqualTo(0);
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Fresh_Setup_Verifies_Downloads_Before_APT_And_Is_Idempotent()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            FakeHost host = new ();
            Debian13Dependencies dependencies = host.Create();
            await dependencies.Install(directory.FullName, TextWriter.Null);

            await Assert.That(host.Events.IndexOf("download:libncurses5") < host.Events.IndexOf("apt:update")).IsTrue();
            ProcessStartInfo installation = host.Commands.Single(command => command.FileName == "/usr/bin/apt-get" && command.ArgumentList.Contains("install"));
            await Assert.That(installation.ArgumentList.Contains("--no-remove")).IsTrue();
            await Assert.That(installation.ArgumentList.Contains("--no-upgrade")).IsTrue();
            await Assert.That(installation.ArgumentList.Contains("--allow-downgrades")).IsFalse();
            await Assert.That(installation.ArgumentList.Contains("--allow-unauthenticated")).IsFalse();
            await Assert.That(installation.ArgumentList.Contains("libfontconfig1:amd64")).IsTrue();
            await Assert.That(installation.ArgumentList.Contains("libfreetype6:amd64")).IsTrue();
            await Assert.That(installation.UseShellExecute).IsFalse();
            await Assert.That(installation.RedirectStandardOutput).IsFalse();
            await Assert.That(host.Downloads.All(download => installation.ArgumentList.Contains(download))).IsTrue();
            await Assert.That(host.Downloads.All(download => Directory.Exists(Path.GetDirectoryName(download)) is false)).IsTrue();

            await dependencies.Install(directory.FullName, TextWriter.Null);
            await Assert.That(host.Downloads.Count).IsEqualTo(2);
            await Assert.That(host.Commands.Count(command => command.FileName == "/usr/bin/apt-get")).IsEqualTo(2);
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Failed_Download_Cannot_Invoke_APT_And_Releases_The_Lock()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            FakeHost host = new () { FailDownload = true };
            await Assert.That(() => host.Create().Install(directory.FullName, TextWriter.Null)).Throws<InvalidDataException>();
            await Assert.That(host.Commands.Any(command => command.FileName == "/usr/bin/apt-get")).IsFalse();
            await Assert.That(host.Downloads.All(download => Directory.Exists(Path.GetDirectoryName(download)) is false)).IsTrue();
            await Assert.That(SingleInstanceGuard.TryAcquire(Path.Combine(directory.FullName, "COMPEL.lock"), out SingleInstanceGuard guard)).IsTrue();
            guard.Dispose();
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Failed_APT_Update_Does_Not_Proceed_To_Installation()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            FakeHost host = new () { FailAPT = true };
            await Assert.That(() => host.Create().Install(directory.FullName, TextWriter.Null)).Throws<InvalidOperationException>();
            await Assert.That(host.Commands.Count(command => command.FileName == "/usr/bin/apt-get")).IsEqualTo(1);
            await Assert.That(host.Downloads.All(download => Directory.Exists(Path.GetDirectoryName(download)) is false)).IsTrue();
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Downloaded_Package_Must_Match_The_Pinned_Hash_Before_It_Is_Written()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            using HttpClient client = new (new PackageHandler());
            string destination = Path.Combine(directory.FullName, "test.deb");
            DebianCompatibilityPackage officialPackage = Debian13Dependencies.CompatibilityPackages[0];

            await Assert.That(() => Debian13Dependencies.DownloadPackage(client, officialPackage, destination)).Throws<InvalidDataException>();
            await Assert.That(File.Exists(destination)).IsFalse();

            string expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("test-package")));
            DebianCompatibilityPackage testPackage = new ("test", "test.deb", expectedHash);
            await Debian13Dependencies.DownloadPackage(client, testPackage, destination);
            await Assert.That(await File.ReadAllTextAsync(destination)).IsEqualTo("test-package");
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Preflight_Uses_The_Real_Launch_Environment_And_Only_Server_Components()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("COMPEL-dependency-test-");

        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "game"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "game", "game-x86_64-server.so"), "test-library");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "game", "game-x86.so"), "ignored-client-library");
            ProcessStartInfo manager = new (Path.Combine(directory.FullName, "hon-x86_64-server")) { WorkingDirectory = directory.FullName };
            manager.Environment["LD_LIBRARY_PATH"] = "/custom/libraries";
            manager.Environment["LD_PRELOAD"] = "/custom/shim.so";
            FakeHost host = new ();

            await Assert.That(await host.Create().Check(manager, CancellationToken.None)).IsNull();
            await Assert.That(host.Commands.Count).IsEqualTo(2);

            foreach (ProcessStartInfo command in host.Commands)
            {
                await Assert.That(command.FileName).IsEqualTo("/lib64/ld-linux-x86-64.so.2");
                await Assert.That(command.ArgumentList[0]).IsEqualTo("--list");
                await Assert.That(command.WorkingDirectory).IsEqualTo(directory.FullName);
                await Assert.That(command.Environment["LD_LIBRARY_PATH"]).IsEqualTo("/custom/libraries");
                await Assert.That(command.Environment["LD_PRELOAD"]).IsEqualTo("/custom/shim.so");
                await Assert.That(command.Environment["LC_ALL"]).IsEqualTo("C");
                await Assert.That(command.UseShellExecute).IsFalse();
            }

            await Assert.That(host.Downloads.Count).IsEqualTo(0);
        }

        finally { directory.Delete(recursive: true); }
    }

    [Test]
    public async Task Preflight_Fails_Closed_For_Missing_Libraries_And_Ignored_Preloads()
    {
        foreach (DependencyProcessResult result in new[]
        {
            new DependencyProcessResult(127, "", "error while loading shared libraries: libncurses.so.5: cannot open shared object file"),
            new DependencyProcessResult(0, "libfontconfig.so.1 => not found", ""),
            new DependencyProcessResult(0, "", "ERROR: ld.so: object cannot be preloaded: ignored.")
        })
        {
            FakeHost host = new () { ProbeResult = result };
            string? failure = await host.Create().Check(new ProcessStartInfo("/missing/hon-x86_64-server"), CancellationToken.None);
            await Assert.That(failure).IsNotNull();
            await Assert.That(host.Commands.Count).IsEqualTo(1);
            await Assert.That(host.Downloads.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Preflight_Reports_Probe_Failures_But_Propagates_Shutdown_Cancellation()
    {
        Debian13Dependencies dependencies = new (true, true,
            (startInfo, cancellationToken) => throw new OperationCanceledException(),
            (package, destination) => throw new InvalidOperationException("Unexpected Download"));

        await Assert.That(await dependencies.Check(new ProcessStartInfo("/missing/hon"), CancellationToken.None)).IsNotNull();
        await Assert.That(() => dependencies.Check(new ProcessStartInfo("/missing/hon"), new CancellationToken(true))).Throws<OperationCanceledException>();
    }

    private sealed class PackageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("test-package") });
    }

    private sealed class FakeHost
    {
        internal List<ProcessStartInfo> Commands { get; } = [];
        internal List<string> Downloads { get; } = [];
        internal List<string> Events { get; } = [];
        internal bool PackagesInstalled { get; set; }
        internal bool FailDownload { get; init; }
        internal bool FailAPT { get; init; }
        internal string PackageArchitecture { get; init; } = "amd64";
        internal DependencyProcessResult ProbeResult { get; init; } = new (0, "libc.so.6 => /lib/libc.so.6", "");

        internal Debian13Dependencies Create(bool supportedHost = true, bool privileged = true)
            => new (supportedHost, privileged, Run, Download);

        private Task<DependencyProcessResult> Run(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            Commands.Add(startInfo);

            if (startInfo.FileName == "/usr/bin/dpkg")
                return Task.FromResult(new DependencyProcessResult(0, PackageArchitecture, ""));

            if (startInfo.FileName == "/usr/bin/dpkg-query")
                return Task.FromResult(PackagesInstalled ? new DependencyProcessResult(0, "installed\tamd64", "") : new DependencyProcessResult(1, "", "not installed"));

            if (startInfo.FileName == "/usr/bin/apt-get")
            {
                bool installing = startInfo.ArgumentList.Contains("install");
                Events.Add(installing ? "apt:install" : "apt:update");
                PackagesInstalled = installing && FailAPT is false;
                return Task.FromResult(new DependencyProcessResult(FailAPT ? 100 : 0, "", ""));
            }

            if (startInfo.FileName == "/lib64/ld-linux-x86-64.so.2")
                return Task.FromResult(ProbeResult);

            throw new InvalidOperationException($"Unexpected Command: {startInfo.FileName}");
        }

        private Task Download(DebianCompatibilityPackage package, string destination)
        {
            Downloads.Add(destination);
            Events.Add("download:" + package.Name);

            if (FailDownload)
                throw new InvalidDataException("Test Verification Failure");

            return Task.CompletedTask;
        }
    }
}
