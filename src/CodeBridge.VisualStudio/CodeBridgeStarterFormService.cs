using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeBridge.VisualStudio;

internal static class CodeBridgeStarterFormService
{
    private const string StarterFormClassName = "CodeBridgeStarterForm";

    public static string AddStarterForm(VisualStudioProjectContext context)
    {
        var filePath = GetUniqueFilePath(context.ProjectDirectory, StarterFormClassName, ".cs");
        var source = CodeBridgeStarterFormTemplate.Create(
            context.DefaultNamespace,
            Path.GetFileNameWithoutExtension(filePath));

        File.WriteAllText(filePath, source, Encoding.UTF8);

        // The form runs this flow file; it comes with the form so the project works as it is.
        CodeBridgeStarterFlow.EnsureIn(context.ProjectDirectory);
        return filePath;
    }

    public static bool TryUseStarterFormAsDefault(VisualStudioProjectContext context)
    {
        var programPath = Path.Combine(context.ProjectDirectory, "Program.cs");
        if (!File.Exists(programPath))
        {
            // If Program.cs is missing (e.g. vstemplate failed to copy it), generate it completely.
            var code = $@"using System;
using System.Windows.Forms;

namespace {context.DefaultNamespace}
{{
    internal static class Program
    {{
        [STAThread]
        public static void Main(string[] args)
        {{
            ApplicationConfiguration.Initialize();
            Application.Run(new CodeBridgeStarterForm());
        }}
    }}
}}";
            File.WriteAllText(programPath, code, Encoding.UTF8);
            return true;
        }

        var source = File.ReadAllText(programPath);
        var updated = Regex.Replace(
            source,
            @"Application\.Run\s*\(\s*new\s+Form1\s*\(\s*\)\s*\)\s*;",
            "Application.Run(new CodeBridgeStarterForm());",
            RegexOptions.CultureInvariant);

        if (string.Equals(source, updated, StringComparison.Ordinal))
            return false;

        File.WriteAllText(programPath, updated, Encoding.UTF8);
        return true;
    }

    public static async Task OpenFileAsync(AsyncPackage package, string filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE2;
        dte?.ItemOperations.OpenFile(filePath);
    }

    private static string GetUniqueFilePath(string directory, string baseName, string extension)
    {
        var filePath = Path.Combine(directory, baseName + extension);
        if (!File.Exists(filePath))
            return filePath;

        for (var index = 2; index < 100; index++)
        {
            filePath = Path.Combine(directory, $"{baseName}{index}{extension}");
            if (!File.Exists(filePath))
                return filePath;
        }

        throw new InvalidOperationException("Could not create a unique CodeBridge starter form filename.");
    }
}
