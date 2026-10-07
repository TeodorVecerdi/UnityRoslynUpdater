namespace UnityRoslynUpdater;

internal sealed class UpdateSdkOperation : IUpdateOperation
{
    public Task ExecuteAsync(UpdateContext context)
    {
        var sdk = context.Sdk;
        var dotNetRoot = DotNetRoot.GetLocation();

        //
        // Depending on the version, Unity uses some of these directories:
        // * Editor/Data/NetCoreRuntime
        // * Editor/Data/DotNetSdkRoslyn
        // * Editor/Data/DotNetSdk (Unity 6.5+)
        //
        // Each one the editor ships with is moved into a "BuiltInDotNetSdk"
        // directory and replaced with a symbolic link to the selected SDK.
        //
        LinkDirectory(context, "NetCoreRuntime", dotNetRoot);
        LinkDirectory(context, "DotNetSdkRoslyn", Path.Combine(sdk.Location, "Roslyn", "bincore"));
        LinkDirectory(context, "DotNetSdk", dotNetRoot);

        // Leave behind a file denoting which SDK we are currently linked to.
        File.WriteAllText(Path.Combine(context.EditorDataPath, ".dotnet-link"), sdk.Location);

        Console.WriteLine($"Linked to .NET SDK at {sdk.Location}");
        return Task.CompletedTask;
    }

    private static void LinkDirectory(UpdateContext context, string name, string target)
    {
        var path = Path.Combine(context.EditorDataPath, name);
        var builtInPath = Path.Combine(context.EditorDataPath, "BuiltInDotNetSdk", name);
        var directory = new DirectoryInfo(path);

        if (directory.LinkTarget is not null)
        {
            // The directory IS a symbolic link, meaning we have most likely
            // patched this installation previously. We'll delete the link so
            // it can be updated. (A link whose target didn't exist when it was
            // created may be a file link rather than a directory link.)
            if (directory.Attributes.HasFlag(FileAttributes.Directory))
                directory.Delete();
            else
                File.Delete(path);
        }
        else if (directory.Exists)
        {
            // The directory is not a symbolic link, so either we haven't patched
            // this Unity installation yet, or it was repaired since. We'll move the
            // directory into the BuiltInDotNetSdk directory, replacing any older
            // copy from a previous run, so the symbolic link can be created.
            if (Directory.Exists(builtInPath))
                Directory.Delete(builtInPath, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(builtInPath)!);
            Directory.Move(path, builtInPath);
        }

        // Unity doesn't ship with this directory, so there's nothing to redirect.
        // (Older versions of this tool created such links anyway; they were removed above.)
        if (!Directory.Exists(builtInPath))
            return;

        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch
        {
            // Don't leave the editor without the directory.
            Directory.Move(builtInPath, path);
            throw;
        }
    }
}
