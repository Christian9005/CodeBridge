using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeBridge.VisualStudio;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("CodeBridge", "Visual flows and ESP32 toolbox integration for WinForms.", "0.3.0")]
[ProvideMenuResource("CodeBridgeCommands.CTMENU", 1)]
[Guid(PackageGuids.PackageGuidString)]
[ProvideBindingPath]
[ProvideToolWindow(typeof(CodeBridgeToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
[ProvideToolWindow(typeof(Monitor.SerialMonitorToolWindow), Style = VsDockStyle.Tabbed, Window = "34e76e81-ee4a-11d0-ae2e-00a0c90fffc3")]
[ProvideToolWindow(typeof(Tour.TourToolWindow), Style = VsDockStyle.MDI, DocumentLikeTool = true, Transient = true)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideEditorFactory(typeof(Editor.FlowEditorFactory), 110, CommonPhysicalViewAttributes = (int)__VSPHYSICALVIEWATTRIBUTES.PVA_SupportsPreview, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
[ProvideEditorExtension(typeof(Editor.FlowEditorFactory), ".cbflow", 50, DefaultName = "CodeBridge Flow Editor")]
public sealed class CodeBridgePackage : AsyncPackage
{
    protected override async System.Threading.Tasks.Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await OpenCodeBridgeSetupCommand.InitializeAsync(this);
        await InstallCodeBridgeWinFormsPackageCommand.InitializeAsync(this);
        await AddCodeBridgeStarterFormCommand.InitializeAsync(this);
        await Tour.OpenTourCommand.InitializeAsync(this);
        await Monitor.OpenSerialMonitorCommand.InitializeAsync(this);
        await Firmware.FirmwareCommands.InitializeAsync(this);
        RegisterEditorFactory(new Editor.FlowEditorFactory());
    }
}
