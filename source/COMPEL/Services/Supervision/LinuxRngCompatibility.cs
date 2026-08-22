using System.Diagnostics;

namespace COMPEL.Services.Supervision;

/// <summary>
///     Applies the ABI-specific fork-safe shuffle RNG interposer supplied by compatible Linux match-server distributions.
///     The distribution controls activation by including or omitting the library; COMPEL only changes CowMaster's child environment, so the interposer is never loaded into COMPEL itself.
/// </summary>
internal static class LinuxRngCompatibility
{
    internal const string ShimFileName = "libhon-rng-forksafe.so";
    internal static readonly string ShimRelativePath = Path.Combine("compatibility", ShimFileName);

    /// <summary>
    ///     Adds the distribution's fork-safe RNG interposer to the child process environment when it is present.
    ///     CowMaster loads the library before its libc++ and every forked slave inherits it; the interposer then detects the child's PID and seeds a process-local stream.
    /// </summary>
    /// <returns>The fully qualified shim path when activated; otherwise <see langword="null"/>.</returns>
    internal static string? Apply(ProcessStartInfo startInfo, string installationDirectory, bool isLinux)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);

        if (isLinux is false)
            return null;

        string shimPath = Path.GetFullPath(Path.Combine(installationDirectory, ShimRelativePath));

        if (File.Exists(shimPath) is false)
            return null;

        startInfo.Environment.TryGetValue("LD_PRELOAD", out string? existingPreloads);
        startInfo.Environment["LD_PRELOAD"] = BuildPreloadValue(shimPath, existingPreloads);

        return shimPath;
    }

    private static string BuildPreloadValue(string shimPath, string? existingPreloads)
    {
        if (string.IsNullOrWhiteSpace(existingPreloads))
            return shimPath;

        // A manually tested or older copy of this same shim may already be inherited from COMPEL's own launch environment. Remove it by file name before prepending the distribution-managed copy, while preserving unrelated operator-supplied preload libraries.
        string[] retainedPreloads = existingPreloads
            .Split([':', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => string.Equals(Path.GetFileName(path), ShimFileName, StringComparison.Ordinal) is false)
            .ToArray();

        return retainedPreloads.Length is 0
            ? shimPath
            : string.Join(':', [shimPath, .. retainedPreloads]);
    }
}
