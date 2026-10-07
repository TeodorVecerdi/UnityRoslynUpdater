using Microsoft.Win32;
using NuGet.Versioning;

namespace UnityRoslynUpdater;

internal sealed record DotNetSdk(string Location, SemanticVersion Version)
{
    public string Name => Path.GetFileName(Location);
}

internal sealed class DotNetRoot
{
    public static IEnumerable<DotNetSdk> EnumerateSDKs(string root)
    {
        var sdkDirectory = Path.Combine(root, "sdk");

        if (!Directory.Exists(sdkDirectory))
            yield break;

        foreach (string directory in Directory.EnumerateDirectories(sdkDirectory))
        {
            var directoryName = Path.GetFileName(directory);

            if (!SemanticVersion.TryParse(directoryName, out SemanticVersion? version))
                continue;

            // Uninstalling an SDK can leave its (empty) directory behind, so make
            // sure the directory actually contains a compiler.
            if (!File.Exists(Path.Combine(directory, "Roslyn", "bincore", "csc.dll")))
                continue;

            yield return new DotNetSdk(Path.GetFullPath(directory), version);
        }
    }

    /// <summary>
    /// Selects the newest installed SDK, or the newest one matching <paramref name="requestedVersion"/>,
    /// which can be a full version (10.0.204) or a prefix of one (10, 10.0, 11.0.100-rc.1).
    /// </summary>
    public static DotNetSdk SelectSdk(string root, string? requestedVersion)
    {
        var sdks = EnumerateSDKs(root).OrderBy(static sdk => sdk.Version).ToList();

        if (sdks.Count == 0)
            throw new UpdateException($"No .NET SDKs were found in {root}.");

        if (requestedVersion is null)
            return sdks[^1];

        return sdks.LastOrDefault(sdk => IsVersionMatch(sdk.Name, requestedVersion))
            ?? throw new UpdateException($"No installed .NET SDK matches '{requestedVersion}'. Installed SDKs: {string.Join(", ", sdks.Select(sdk => sdk.Name))}.");
    }

    private static bool IsVersionMatch(string name, string requestedVersion)
    {
        return name.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(requestedVersion + ".", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(requestedVersion + "-", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetLocation()
    {
        string? location = Environment.GetEnvironmentVariable("DOTNET_ROOT");

        if (string.IsNullOrEmpty(location))
            location = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64", "InstallLocation", null)?.ToString();

        if (string.IsNullOrEmpty(location))
            location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        return location;
    }
}
