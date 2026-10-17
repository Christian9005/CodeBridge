using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using EnvDTE;
using Microsoft.VisualStudio.TemplateWizard;
using Microsoft.VisualStudio.Shell;

namespace CodeBridge.VisualStudio
{
    public class TemplateWizard : IWizard
    {
        private DTE _dte = null!;
        private string _destinationDirectory = null!;
        private string _safeProjectName = null!;

        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary, WizardRunKind runKind, object[] customParams)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = (DTE)automationObject;
            _destinationDirectory = replacementsDictionary["$destinationdirectory$"];
            _safeProjectName = replacementsDictionary["$safeprojectname$"];

            // The project file asks for the CodeBridge package of this very version, so a new project restores from NuGet like any other.
            replacementsDictionary["$codebridgeversion$"] = ResolvePackageVersion();
        }

        public void ProjectFinishedGenerating(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var projectFullName = project.FullName;
                var projectDir = Path.GetDirectoryName(projectFullName) ?? _destinationDirectory;

                // 1. Always ensure Program.cs exists — this is the #1 cause of CS5001.
                EnsureProgramCs(projectDir, _safeProjectName);

                // 2. Generate the starter form.
                var context = new VisualStudioProjectContext(project, projectFullName, _safeProjectName);
                CodeBridgeStarterFormService.AddStarterForm(context);
            }
            catch (Exception ex)
            {
                ActivityLog.LogError("CodeBridge", $"Template Wizard generation error: {ex.Message}");
            }
        }

        public void RunFinished() { }
        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
        public bool ShouldAddProjectItem(string filePath) => true;
        public void BeforeOpeningFile(ProjectItem projectItem) { }

        private static string ResolvePackageVersion()
        {
            try
            {
                return ToolboxManifestLoader.LoadDefault().Package.Version;
            }
            catch (Exception ex)
            {
                ActivityLog.LogWarning("CodeBridge", $"Template Wizard could not read the package version from the manifest: {ex.Message}");
                var version = typeof(TemplateWizard).Assembly.GetName().Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }

        /// <summary>
        /// Writes Program.cs to disk if it does not already exist.
        /// This guarantees the project always has a valid entry point,
        /// regardless of whether the vstemplate file-copy succeeded.
        /// </summary>
        private static void EnsureProgramCs(string projectDir, string namespaceName)
        {
            var programPath = Path.Combine(projectDir, "Program.cs");
            if (File.Exists(programPath))
                return;

            var code = $@"using System;
using System.Windows.Forms;

namespace {namespaceName}
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
}}
";
            File.WriteAllText(programPath, code, Encoding.UTF8);
        }
    }
}
