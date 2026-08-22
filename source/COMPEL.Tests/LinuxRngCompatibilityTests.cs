using System.Diagnostics;

namespace COMPEL.Tests;

public sealed class LinuxRngCompatibilityTests
{
    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), $"COMPEL-RNG-{Guid.NewGuid():N}");

    [Test]
    public async Task An_Existing_Linux_Shim_Is_Applied_To_The_Child_Environment()
    {
        string directory = TemporaryDirectory();
        string shimPath = Path.Combine(directory, LinuxRngCompatibility.ShimRelativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
            await File.WriteAllBytesAsync(shimPath, [ 0x7F, (byte)'E', (byte)'L', (byte)'F' ]);

            ProcessStartInfo startInfo = new ();
            startInfo.Environment.Remove("LD_PRELOAD");

            string? activatedPath = LinuxRngCompatibility.Apply(startInfo, directory, isLinux: true);

            using (Assert.Multiple())
            {
                await Assert.That(activatedPath).IsEqualTo(Path.GetFullPath(shimPath));
                await Assert.That(startInfo.Environment["LD_PRELOAD"]).IsEqualTo(Path.GetFullPath(shimPath));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Missing_Shim_Does_Not_Change_The_Child_Environment()
    {
        ProcessStartInfo startInfo = new ();
        startInfo.Environment.Remove("LD_PRELOAD");

        string? activatedPath = LinuxRngCompatibility.Apply(startInfo, TemporaryDirectory(), isLinux: true);

        using (Assert.Multiple())
        {
            await Assert.That(activatedPath).IsNull();
            await Assert.That(startInfo.Environment.ContainsKey("LD_PRELOAD")).IsFalse();
        }
    }

    [Test]
    public async Task A_Non_Linux_Launch_Does_Not_Apply_An_Available_Shim()
    {
        string directory = TemporaryDirectory();
        string shimPath = Path.Combine(directory, LinuxRngCompatibility.ShimRelativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
            await File.WriteAllBytesAsync(shimPath, [ 0x7F, (byte)'E', (byte)'L', (byte)'F' ]);

            ProcessStartInfo startInfo = new ();
            startInfo.Environment.Remove("LD_PRELOAD");

            string? activatedPath = LinuxRngCompatibility.Apply(startInfo, directory, isLinux: false);

            using (Assert.Multiple())
            {
                await Assert.That(activatedPath).IsNull();
                await Assert.That(startInfo.Environment.ContainsKey("LD_PRELOAD")).IsFalse();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task The_Distribution_Shim_Replaces_An_Older_Copy_And_Preserves_Unrelated_Preloads()
    {
        string directory = TemporaryDirectory();
        string shimPath = Path.Combine(directory, LinuxRngCompatibility.ShimRelativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shimPath)!);
            await File.WriteAllBytesAsync(shimPath, [ 0x7F, (byte)'E', (byte)'L', (byte)'F' ]);

            ProcessStartInfo startInfo = new ();
            startInfo.Environment["LD_PRELOAD"] = "/tmp/old/libhon-rng-forksafe.so:/opt/operator/libaudit.so";

            string? activatedPath = LinuxRngCompatibility.Apply(startInfo, directory, isLinux: true);

            string expected = $"{Path.GetFullPath(shimPath)}:/opt/operator/libaudit.so";

            using (Assert.Multiple())
            {
                await Assert.That(activatedPath).IsEqualTo(Path.GetFullPath(shimPath));
                await Assert.That(startInfo.Environment["LD_PRELOAD"]).IsEqualTo(expected);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
