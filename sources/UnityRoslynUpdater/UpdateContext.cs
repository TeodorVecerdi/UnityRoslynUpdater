namespace UnityRoslynUpdater;

internal sealed class UpdateContext
{
    /// <summary>The path to the Unity editor installation.</summary>
    public required string EditorPath { get; init; }

    /// <summary>The path to the Unity editor data directory.</summary>
    public string EditorDataPath => Path.Combine(EditorPath, "Data");

    /// <summary>The SDK version requested with --sdk, or null to use the newest installed SDK.</summary>
    public string? RequestedSdkVersion { get; init; }

    /// <summary>The .NET SDK to link the editor to. Selected on first use, so operations that don't need it don't require one.</summary>
    public DotNetSdk Sdk => field ??= DotNetRoot.SelectSdk(DotNetRoot.GetLocation(), RequestedSdkVersion);
}
