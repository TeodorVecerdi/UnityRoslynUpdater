using System.Text.Json;
using Microsoft.Win32;

namespace UnityRoslynUpdater;

internal static class EditorFinder
{
    private const string EditorExecutableName = "Unity.exe";

    /// <summary>
    /// Returns the 'Editor' directories of all Unity installations found in the registry and in the
    /// Unity Hub configuration, keeping only those that actually contain an editor executable.
    /// </summary>
    public static List<string> GetInstalledEditorPaths()
    {
        var candidates = GetRegistryEditorPaths().Concat(GetHubEditorPaths());
        var editorPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            string normalized;

            try
            {
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsEditorDirectory(normalized) && seen.Add(normalized))
                editorPaths.Add(normalized);
        }

        editorPaths.Sort(StringComparer.OrdinalIgnoreCase);
        return editorPaths;
    }

    /// <summary>
    /// Returns the 'Editor' directories recorded by Unity installers in the registry. Uninstalling an
    /// editor does not always remove these keys, so the returned paths may no longer exist.
    /// </summary>
    public static List<string> GetRegistryEditorPaths()
    {
        var editorPaths = new List<string>();

        foreach (var hive in (ReadOnlySpan<RegistryKey>)[Registry.CurrentUser, Registry.LocalMachine])
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\Unity Technologies\Installer");

            if (key == null)
                continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                var installPath = subKey?.GetValue("Location x64") as string;

                if (string.IsNullOrEmpty(installPath)) continue;

                var editorPath = Path.Combine(installPath, "Editor");
                editorPaths.Add(editorPath);
            }
        }

        return editorPaths;
    }

    /// <summary>
    /// Returns candidate 'Editor' directories known to Unity Hub: editors in the default and custom
    /// Hub install folders, plus editors that were manually located through Hub.
    /// </summary>
    public static List<string> GetHubEditorPaths()
    {
        var editorPaths = new List<string>();
        var hubDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");

        // Editors installed by Hub live in '<install folder>\<version>\Editor'.
        var installFolders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor"),
        };

        if (ReadJsonFile(Path.Combine(hubDataDirectory, "secondaryInstallPath.json")) is { ValueKind: JsonValueKind.String } secondary
            && secondary.GetString() is { Length: > 0 } secondaryInstallPath)
        {
            installFolders.Add(secondaryInstallPath);
        }

        foreach (var installFolder in installFolders)
        {
            try
            {
                if (!Directory.Exists(installFolder)) continue;

                foreach (var versionDirectory in Directory.EnumerateDirectories(installFolder))
                    editorPaths.Add(Path.Combine(versionDirectory, "Editor"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An inaccessible install folder should not prevent discovering the others.
            }
        }

        // Editors manually located in Hub are listed in editors-v2.json as
        // { "data": [ { "location": [ "<path to Unity.exe>" ], ... } ] }.
        if (ReadJsonFile(Path.Combine(hubDataDirectory, "editors-v2.json")) is { ValueKind: JsonValueKind.Object } editors
            && editors.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("location", out var location))
                    continue;

                IEnumerable<JsonElement> locations = location.ValueKind == JsonValueKind.Array ? location.EnumerateArray() : [location];

                foreach (var item in locations)
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } path)
                        editorPaths.Add(ToEditorDirectory(path));
                }
            }
        }

        return editorPaths;
    }

    public static string ChooseEditorFullPath()
    {
        var editorPaths = GetInstalledEditorPaths();

        if (editorPaths.Count == 0)
            return string.Empty;

        Console.WriteLine("Select an editor to patch:");

        for (var i = 0; i < editorPaths.Count; i++)
            Console.WriteLine($"  [{i}] {editorPaths[i]}");

        while (true)
        {
            Console.Write("Enter index number: ");

            var input = Console.ReadLine();

            if (input is null)
                return string.Empty;

            if (int.TryParse(input, out var index) && index >= 0 && index < editorPaths.Count)
                return editorPaths[index];

            Console.WriteLine("Invalid selection. Please try again.");
        }
    }

    private static bool IsEditorDirectory(string path)
    {
        return File.Exists(Path.Combine(path, EditorExecutableName));
    }

    /// <summary>
    /// Hub records the path to the editor executable; accept either that or the directory itself.
    /// </summary>
    private static string ToEditorDirectory(string path)
    {
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) ?? path : path;
    }

    private static JsonElement? ReadJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
