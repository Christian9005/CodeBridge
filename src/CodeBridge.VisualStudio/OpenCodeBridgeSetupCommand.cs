using System;
using System.ComponentModel.Design;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CodeBridge.VisualStudio;

internal sealed class OpenCodeBridgeSetupCommand
{
    private readonly AsyncPackage _package;
    private CodeBridgeToolWindow? _toolWindow;

    private OpenCodeBridgeSetupCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package;

        var commandId = new CommandID(PackageGuids.CommandSet, PackageIds.OpenSetupCommandId);
        var command = new MenuCommand(Execute, commandId);
        commandService.AddCommand(command);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        if (commandService is not null)
            _ = new OpenCodeBridgeSetupCommand(package, commandService);
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
            var packageFeedPath = CodeBridgePackageInstaller.ResolvePackageFeedPath(manifest);
            var viewModel = CreateSetupViewModel(manifest, context, packageFeedPath);

            var window = await _package.ShowToolWindowAsync(typeof(CodeBridgeToolWindow), 0, true, _package.DisposalToken) as CodeBridgeToolWindow;
            if (window?.Frame == null)
            {
                throw new NotSupportedException("Cannot create tool window");
            }

            _toolWindow = window;
            _toolWindow.SetupControl.ActionSelected -= OnActionSelected;
            _toolWindow.SetupControl.ActionSelected += OnActionSelected;
            _toolWindow.SetupControl.UpdateModel(viewModel);
        }
        catch (Exception ex)
        {
            ShowMessage(
                "CodeBridge VSIX base is installed, but the Toolbox manifest could not be loaded.\n\n" +
                ex.Message,
                OLEMSGICON.OLEMSGICON_CRITICAL);
        }
    }

    private void OnActionSelected(object sender, CodeBridgeSetupAction action)
    {
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var manifest = ToolboxManifestLoader.LoadDefault();
                var context = await VisualStudioProjectLocator.GetActiveCSharpProjectAsync(_package);
                var packageFeedPath = CodeBridgePackageInstaller.ResolvePackageFeedPath(manifest);

                switch (action)
                {
                    case CodeBridgeSetupAction.InstallPackage:
                        await InstallPackageAsync(manifest, context, packageFeedPath);
                        break;
                    case CodeBridgeSetupAction.RemovePackage:
                        await RemovePackageAsync(manifest, context);
                        break;
                    case CodeBridgeSetupAction.AddStarterForm:
                        await AddStarterFormAsync(manifest, context, packageFeedPath);
                        break;
                }
                
                // Refresh view model after action
                context = await VisualStudioProjectLocator.GetActiveCSharpProjectAsync(_package);
                var newModel = CreateSetupViewModel(manifest, context, packageFeedPath);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _toolWindow?.SetupControl.UpdateModel(newModel);
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowMessage(ex.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }).FileAndForget("CodeBridgeSetupAction");
    }

    private async Task InstallPackageAsync(
        ToolboxManifest manifest,
        VisualStudioProjectContext? context,
        string? packageFeedPath)
    {
        if (context is null || string.IsNullOrWhiteSpace(packageFeedPath))
        {
            ShowMessage("Select a C# WinForms project and ensure the embedded package feed is available.", OLEMSGICON.OLEMSGICON_WARNING);
            return;
        }

        var result = await CodeBridgePackageInstaller.InstallPackageAsync(context.ProjectPath, manifest.Package, packageFeedPath!);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ShowMessage(
            CreatePackageResultMessage(context.ProjectPath, packageFeedPath!, result),
            result.ExitCode == 0 ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_CRITICAL);
    }

    private async Task RemovePackageAsync(
        ToolboxManifest manifest,
        VisualStudioProjectContext? context)
    {
        if (context is null)
        {
            ShowMessage("Select a C# WinForms project before removing the CodeBridge package.", OLEMSGICON.OLEMSGICON_WARNING);
            return;
        }

        var result = await CodeBridgePackageInstaller.RemovePackageAsync(context.ProjectPath, manifest.Package);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ShowMessage(
            CreatePackageResultMessage(context.ProjectPath, null, result),
            result.ExitCode == 0 ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_CRITICAL);
    }

    private async Task AddStarterFormAsync(
        ToolboxManifest manifest,
        VisualStudioProjectContext? context,
        string? packageFeedPath)
    {
        if (context is null || string.IsNullOrWhiteSpace(packageFeedPath))
        {
            ShowMessage("Select a C# WinForms project and ensure the embedded package feed is available.", OLEMSGICON.OLEMSGICON_WARNING);
            return;
        }

        var result = await CodeBridgePackageInstaller.InstallPackageAsync(context.ProjectPath, manifest.Package, packageFeedPath!);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (result.ExitCode != 0)
        {
            ShowMessage(CreatePackageResultMessage(context.ProjectPath, packageFeedPath!, result), OLEMSGICON.OLEMSGICON_CRITICAL);
            return;
        }

        var formPath = CodeBridgeStarterFormService.AddStarterForm(context);
        var startupUpdated = CodeBridgeStarterFormService.TryUseStarterFormAsDefault(context);
        await CodeBridgeStarterFormService.OpenFileAsync(_package, formPath);

        ShowMessage(CreateStarterFormResultMessage(context, formPath, startupUpdated), OLEMSGICON.OLEMSGICON_INFO);
    }

    private static CodeBridgeSetupViewModel CreateSetupViewModel(
        ToolboxManifest manifest,
        VisualStudioProjectContext? context,
        string? packageFeedPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return new CodeBridgeSetupViewModel
        {
            ProjectPath = context?.ProjectPath,
            ProjectName = context?.Project.Name,
            PackageId = manifest.Package.Id,
            PackageVersion = manifest.Package.Version,
            PackageFeedPath = packageFeedPath,
            InstalledPackageVersion = context is null
                ? null
                : CodeBridgePackageInstaller.GetInstalledPackageVersion(context.ProjectPath, manifest.Package.Id),
            ToolboxCategory = manifest.Toolbox.Category,
            Components = manifest.Components
                .Where(component => component.IsVisible)
                .Select(component => $"{component.DisplayName} ({component.Kind})")
                .ToArray()
        };
    }


    private static string CreatePackageResultMessage(string projectPath, string? packageFeedPath, CommandResult result)
    {
        if (result.Action == PackageInstallAction.AlreadyCurrent)
        {
            return $"CodeBridge.Designer.WinForms {result.TargetVersion} is already installed in this project, so nothing changed." +
                   Environment.NewLine + Environment.NewLine + $"Project: {projectPath}";
        }

        var builder = new StringBuilder();
        builder.AppendLine(CreatePackageResultTitle(result));
        builder.AppendLine();
        builder.AppendLine($"Project: {projectPath}");
        if (!string.IsNullOrWhiteSpace(packageFeedPath))
            builder.AppendLine($"Package feed: {packageFeedPath}");
        if (!string.IsNullOrWhiteSpace(result.PreviousVersion))
            builder.AppendLine($"Previous version: {result.PreviousVersion}");
        if (!string.IsNullOrWhiteSpace(result.TargetVersion))
            builder.AppendLine($"Target version: {result.TargetVersion}");
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

    private static string CreatePackageResultTitle(CommandResult result) =>
        result.Action switch
        {
            PackageInstallAction.Installed => "CodeBridge.Designer.WinForms was installed in the project.",
            PackageInstallAction.Updated => "CodeBridge.Designer.WinForms was updated in the project.",
            PackageInstallAction.AlreadyCurrent => "CodeBridge.Designer.WinForms is already current.",
            PackageInstallAction.NewerInstalled => "The project already has a newer CodeBridge.Designer.WinForms package.",
            PackageInstallAction.Removed => "CodeBridge.Designer.WinForms was removed from the project.",
            PackageInstallAction.NotInstalled => "CodeBridge.Designer.WinForms is not installed in this project.",
            PackageInstallAction.UpdateFailed => "CodeBridge could not update the WinForms package.",
            PackageInstallAction.RemoveFailed => "CodeBridge could not remove the WinForms package.",
            _ => result.ExitCode == 0
                ? "CodeBridge.Designer.WinForms package operation completed."
                : "CodeBridge package operation failed."
        };

    private static string CreateStarterFormResultMessage(
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
