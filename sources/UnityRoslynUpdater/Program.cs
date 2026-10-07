using UnityRoslynUpdater;

(string Name, IUpdateOperation Operation)[] operations =
[
    ("sdk", new UpdateSdkOperation()),
    ("source-generator", new PatchSourceGeneratorOperation()),
    ("assemblies", new PatchUnityAssembliesOperation()),
    ("docs", new DownloadBclDocumentationOperation()),
];

string? editorArgument = null;
string? sdkVersion = null;
HashSet<string>? only = null;
var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--only" when i + 1 < args.Length:
            only ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            only.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            break;

        case "--skip" when i + 1 < args.Length:
            skip.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            break;

        case "--sdk" when i + 1 < args.Length:
            sdkVersion = args[++i];
            break;

        case var arg when arg.StartsWith("--") || editorArgument is not null:
            PrintUsage($"Unexpected argument '{arg}'.");
            return 1;

        default:
            editorArgument = args[i];
            break;
    }
}

var unknownNames = (only ?? []).Concat(skip).Where(name => !operations.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();

if (unknownNames.Count > 0)
{
    PrintUsage($"Unknown operation(s): {string.Join(", ", unknownNames)}.");
    return 1;
}

string editorPath = editorArgument is not null ? Path.GetFullPath(editorArgument) : EditorFinder.ChooseEditorFullPath();

if (string.IsNullOrEmpty(editorPath) || !Directory.Exists(Path.Combine(editorPath, "Data")))
{
    Console.Error.WriteLine(
        """
        Please provide the path to the 'Editor' directory of the Unity
        installation that you wish to link to a newer .NET SDK version.
        """
    );

    return 1;
}

var context = new UpdateContext
{
    EditorPath = editorPath,
    RequestedSdkVersion = sdkVersion
};

try
{
    foreach (var (name, operation) in operations)
    {
        if ((only is not null && !only.Contains(name)) || skip.Contains(name))
            continue;

        await operation.ExecuteAsync(context);
    }
}
catch (UpdateException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

return 0;

void PrintUsage(string error)
{
    Console.Error.WriteLine(
        $"""
        {error}

        Usage: UnityRoslynUpdater [<path to Unity Editor folder>] [--only <operations>] [--skip <operations>] [--sdk <version>]

        Operations (comma-separated): {string.Join(", ", operations.Select(o => o.Name))}
        SDK version: a full version (10.0.204) or a prefix (10, 11.0.100-rc); defaults to the newest installed SDK.
        """
    );
}
