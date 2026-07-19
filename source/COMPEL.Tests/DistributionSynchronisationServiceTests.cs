using COMPEL.Services.ContentBroker;

namespace COMPEL.Tests;

/// <summary>
///     Verifies discovery of the installed legacy distribution version used by the UDP server-browser responder when CDN synchronisation is disabled.
/// </summary>
public sealed class DistributionSynchronisationServiceTests
{
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
