using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeBridge.VisualStudio;

internal sealed class AddCodeBridgeStarterFormCommand
{
    private readonly AsyncPackage _package;

    private AddCodeBridgeStarterFormCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;

        var commandId = new CommandID(PackageGuids.CommandSet, PackageIds.AddStarterFormCommandId);
        var command = new MenuCommand(Execute, commandId);
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService is not null)
            _ = new AddCodeBridgeStarterFormCommand(package, commandService);
    }

    private void Execute(object sender, EventArgs e)
    {
        _package.JoinableTaskFactory.Run(ExecuteAsync);
    }

    private async Task ExecuteAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            var manifest = ToolboxManifestLoader.LoadDefault();
            var context = await VisualStudioProjectLocator.GetActiveCSharpProjectAsync(_package);
            if (context is null)
            {
                ShowMessage(
                    "Select a WinForms C# project or open one of its files before adding the CodeBridge starter form.",
                    OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var packageFeedPath = CodeBridgePackageInstaller.ResolvePackageFeedPath(manifest);
            if (string.IsNullOrWhiteSpace(packageFeedPath))
            {
                ShowMessage(
                    "CodeBridge could not find the packaged local NuGet feed.\n\n" +
                    "Please verify your CodeBridge installation or set the CODEBRIDGE_NUGET_FEED environment variable to a directory containing the CodeBridge packages.",
                    OLEMSGICON.OLEMSGICON_WARNING);
                return;
            }

            var installResult = await CodeBridgePackageInstaller.InstallPackageAsync(
                context.ProjectPath,
                manifest.Package,
                packageFeedPath!);
            if (installResult.ExitCode != 0)
            {
                ShowMessage(
                    CreateInstallFailureMessage(context.ProjectPath, packageFeedPath!, installResult),
                    OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            var formPath = CodeBridgeStarterFormService.AddStarterForm(context);
            var startupUpdated = CodeBridgeStarterFormService.TryUseStarterFormAsDefault(context);

            await CodeBridgeStarterFormService.OpenFileAsync(_package, formPath);

            ShowMessage(
                CreateSuccessMessage(context, formPath, startupUpdated),
                OLEMSGICON.OLEMSGICON_INFO);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
        }
    }

    private static string CreateInstallFailureMessage(
        string projectPath,
        string packageFeedPath,
        CommandResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CodeBridge could not install the WinForms package, so the starter form was not added.");
        builder.AppendLine();
        builder.AppendLine($"Project: {projectPath}");
        builder.AppendLine($"Package feed: {packageFeedPath}");
        builder.AppendLine($"dotnet exit code: {result.ExitCode}");

        var details = string.Join(
            Environment.NewLine,
            new[] { result.Output.Trim(), result.Error.Trim() }.Where(value => value.Length > 0));
        if (details.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine(details.Length > 2500 ? details.Substring(0, 2500) : details);
        }

        return builder.ToString();
    }

    private static string CreateSuccessMessage(
        VisualStudioProjectContext context,
        string formPath,
        bool startupUpdated)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CodeBridge starter form was added.");
        builder.AppendLine();
        builder.AppendLine($"Project: {context.ProjectPath}");
        builder.AppendLine($"Form: {formPath}");
        builder.AppendLine(startupUpdated
            ? "Program.cs was updated to start CodeBridgeStarterForm."
            : "Program.cs was not changed. Open Program.cs and run CodeBridgeStarterForm when you want this to be the startup form.");
        builder.AppendLine();
        builder.AppendLine("Build and run the project. The form runs Flows\\Blink.cbflow on the board it finds on USB (or on the simulator) and offers to upload the firmware if the board needs it. Open the .cbflow, change it and save: the running app picks the change up.");
        return builder.ToString();
    }

    private void ShowMessage(string message, OLEMSGICON icon)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        VsShellUtilities.ShowMessageBox(
            _package,
            message,
            "CodeBridge",
            icon,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
