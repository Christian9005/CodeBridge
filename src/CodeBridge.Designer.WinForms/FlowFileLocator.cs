using System.Reflection;

namespace CodeBridge.Designer.WinForms;

/// <summary>
/// Finds the .cbflow file a flow control was told to run.
/// While debugging from Visual Studio the file in the project folder wins, so a change saved in the editor is the one the running app sees;
/// otherwise the copy next to the program is used (the CodeBridge package copies every .cbflow of the project there when it builds).
/// </summary>
internal static class FlowFileLocator
{
    /// <summary>Written into Debug builds by the CodeBridge package: the folder of the project the app was built from.</summary>
    public const string ProjectDirectoryMetadataKey = "CodeBridge.ProjectDirectory";

    public static string? Resolve(string? flowFile, Assembly? application) =>
        Resolve(flowFile, ProjectDirectoryOf(application), AppContext.BaseDirectory);

    public static string? Resolve(string? flowFile, string? projectDirectory, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(flowFile))
            return null;

        flowFile = flowFile.Trim();
        if (Path.IsPathRooted(flowFile))
            return File.Exists(flowFile) ? Path.GetFullPath(flowFile) : null;

        foreach (var directory in new[] { projectDirectory, baseDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            var candidate = Path.GetFullPath(Path.Combine(directory, flowFile));
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? ProjectDirectoryOf(Assembly? application) =>
        application?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == ProjectDirectoryMetadataKey)
            ?.Value;
}
