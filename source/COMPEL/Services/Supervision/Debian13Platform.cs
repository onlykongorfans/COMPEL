namespace COMPEL.Services.Supervision;

/// <summary>
///     Limits the legacy x64 LAS dependency setup to Debian 13, not distributions which merely identify Debian as an ancestor.
/// </summary>
internal static class Debian13Platform
{
    internal static bool IsSupportedHost()
    {
        if (OperatingSystem.IsLinux() is false
            || RuntimeInformation.OSArchitecture is not Architecture.X64
            || RuntimeInformation.ProcessArchitecture is not Architecture.X64)
            return false;

        try
        {
            string path = File.Exists("/etc/os-release") ? "/etc/os-release" : "/usr/lib/os-release";

            return IsSupported(true, Architecture.X64, Architecture.X64, File.ReadAllText(path));
        }

        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool IsSupported(bool isLinux, Architecture operatingSystemArchitecture, Architecture processArchitecture, string release)
    {
        if (isLinux is false || operatingSystemArchitecture is not Architecture.X64 || processArchitecture is not Architecture.X64)
            return false;

        Dictionary<string, string> values = new (StringComparer.Ordinal);

        // Read Data Only: Sourcing This File In A Root Shell Would Turn Platform Detection Into Command Execution.
        foreach (string line in release.Split('\n'))
        {
            string entry = line.Trim();
            int separator = entry.IndexOf('=');

            if (entry.StartsWith('#') || separator <= 0)
                continue;

            string value = entry[(separator + 1)..].Trim();

            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            values[entry[..separator].Trim()] = value;
        }

        return values.GetValueOrDefault("ID") == "debian" && values.GetValueOrDefault("VERSION_ID") == "13";
    }
}
