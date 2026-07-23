using COMPEL.Services.ContentBroker;

namespace COMPEL.Tests;

/// <summary>
///     Verifies discovery of the installed legacy distribution version used by the UDP server-browser responder when CDN synchronisation is disabled.
/// </summary>
public sealed class DistributionSynchronisationServiceTests
{
    [Test]
    public async Task An_Existing_Linux_Freetype_Is_Migrated_Before_CDN_Synchronisation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");
        string libraryDirectory = Path.Combine(directory, "libs-x86_64");
        string originalPath = Path.Combine(libraryDirectory, "libfreetype.so.6");
        string backupPath = originalPath + ".bundled-incompatible-debian13";

        Directory.CreateDirectory(libraryDirectory);

        try
        {
            await File.WriteAllTextAsync(originalPath, "incompatible-bundled-library");

            string? migratedPath = DistributionSynchronisationService.MigrateLinuxFreetype(directory, isLinux: true);

            Manifest manifest = new ()
            {
                Version = "test",
                HashAlgorithm = "SHA-256",
                ExcludeFromSource = [],
                ExcludeFromTarget = [],
                Files = new Dictionary<string, ManifestEntry>
                {
                    ["libs-x86_64/libfreetype.so.6"] = new () { Size = 1, Hash = new string('0', 64) }
                }
            };

            SynchronisationSummary summary = await ContentBroker.Synchronise(manifest, "test", directory, protectedTargetPatterns: DistributionSynchronisationService.ResolveOwnFileProtectionPatterns(isLinux: true));

            await Assert.That(summary.FilesFailed).IsEqualTo(0);
            await Assert.That(migratedPath).IsEqualTo(backupPath);
            await Assert.That(File.Exists(originalPath)).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(backupPath)).IsEqualTo("incompatible-bundled-library");
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task An_Existing_Linux_Freetype_Backup_Is_Not_Overwritten_During_Migration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");
        string libraryDirectory = Path.Combine(directory, "libs-x86_64");
        string originalPath = Path.Combine(libraryDirectory, "libfreetype.so.6");
        string backupPath = originalPath + ".bundled-incompatible-debian13";

        Directory.CreateDirectory(libraryDirectory);

        try
        {
            await File.WriteAllTextAsync(originalPath, "newly-found-bundled-library");
            await File.WriteAllTextAsync(backupPath, "previously-preserved-library");

            string? migratedPath = DistributionSynchronisationService.MigrateLinuxFreetype(directory, isLinux: true);

            await Assert.That(migratedPath).IsEqualTo(backupPath + ".1");
            await Assert.That(File.Exists(originalPath)).IsFalse();
            await Assert.That(await File.ReadAllTextAsync(backupPath)).IsEqualTo("previously-preserved-library");
            await Assert.That(await File.ReadAllTextAsync(backupPath + ".1")).IsEqualTo("newly-found-bundled-library");
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task An_Already_Migrated_Linux_Installation_Is_Left_Unchanged()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");
        string libraryDirectory = Path.Combine(directory, "libs-x86_64");
        string backupPath = Path.Combine(libraryDirectory, "libfreetype.so.6.bundled-incompatible-debian13");

        Directory.CreateDirectory(libraryDirectory);

        try
        {
            await File.WriteAllTextAsync(backupPath, "previously-preserved-library");

            string? migratedPath = DistributionSynchronisationService.MigrateLinuxFreetype(directory, isLinux: true);

            await Assert.That(migratedPath).IsNull();
            await Assert.That(await File.ReadAllTextAsync(backupPath)).IsEqualTo("previously-preserved-library");
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Fresh_Linux_Installation_Does_Not_Create_A_Freetype_Backup()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");

        try
        {
            string? migratedPath = DistributionSynchronisationService.MigrateLinuxFreetype(directory, isLinux: true);

            await Assert.That(migratedPath).IsNull();
            await Assert.That(Directory.Exists(directory)).IsFalse();
        }

        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Non_Linux_Installations_Keep_Their_Bundled_Freetype_And_Do_Not_Protect_It()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");
        string libraryDirectory = Path.Combine(directory, "libs-x86_64");
        string originalPath = Path.Combine(libraryDirectory, "libfreetype.so.6");

        Directory.CreateDirectory(libraryDirectory);

        try
        {
            await File.WriteAllTextAsync(originalPath, "platform-bundled-library");

            string? migratedPath = DistributionSynchronisationService.MigrateLinuxFreetype(directory, isLinux: false);
            IReadOnlyList<string> patterns = DistributionSynchronisationService.ResolveOwnFileProtectionPatterns(isLinux: false);

            await Assert.That(migratedPath).IsNull();
            await Assert.That(await File.ReadAllTextAsync(originalPath)).IsEqualTo("platform-bundled-library");
            await Assert.That(patterns.Any(pattern => pattern.Contains("libfreetype", StringComparison.OrdinalIgnoreCase))).IsFalse();
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task The_Newest_Installed_Legacy_Manifest_Determines_The_Distribution_Version()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllBytes(Path.Combine(directory, "4.9.5.0.manifest.xml.zip"), []);
            File.WriteAllBytes(Path.Combine(directory, "4.10.1.0.manifest.xml.zip"), []);
            File.WriteAllBytes(Path.Combine(directory, "invalid.manifest.xml.zip"), []);

            string? version = DistributionSynchronisationService.ResolveInstalledDistributionVersion(directory);

            await Assert.That(version).IsEqualTo("4.10.1.0");
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Directory_Without_A_Legacy_Manifest_Has_No_Distribution_Version()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        try
        {
            string? version = DistributionSynchronisationService.ResolveInstalledDistributionVersion(directory);

            await Assert.That(version).IsNull();
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
