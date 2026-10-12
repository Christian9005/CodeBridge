#nullable enable
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace CodeBridge.VisualStudio.Monitor
{
    /// <summary>The CodeBridge serial monitor and plotter (Tools > CodeBridge Serial Monitor), docked next to the Output window.</summary>
    [Guid("5a8e0b71-2d64-4f0a-9c35-6b1f7e2d4a90")]
    public sealed class SerialMonitorToolWindow : ToolWindowPane
    {
        private readonly SerialMonitorControl _control;

        public SerialMonitorToolWindow() : base(null)
        {
            Caption = "CodeBridge Serial Monitor";
            _control = new SerialMonitorControl();
            Content = _control;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _control.Shutdown();

            base.Dispose(disposing);
        }
    }

    internal sealed class OpenSerialMonitorCommand
    {
        private readonly AsyncPackage _package;

        private OpenSerialMonitorCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            commandService.AddCommand(new MenuCommand(Execute, new CommandID(PackageGuids.CommandSet, PackageIds.OpenSerialMonitorCommandId)));
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
                _ = new OpenSerialMonitorCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.ShowToolWindowAsync(typeof(SerialMonitorToolWindow), 0, true, _package.DisposalToken);
            });
        }
    }

    /// <summary>Lets code that has no reference to the package (the flow editor) open the monitor.</summary>
    internal static class SerialMonitorLauncher
    {
        public static bool TryOpen(string? port)
        {
            SerialMonitorHandoff.PreferredPort = port;
            try
            {
                return OpenCore();
            }
            catch (Exception ex) when (ex is IOException || ex is TypeLoadException || ex is InvalidOperationException || ex is COMException)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool OpenCore()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell))
                return false;

            var group = PackageGuids.CommandSet;
            object? argument = null;
            return ErrorHandler.Succeeded(shell.PostExecCommand(ref group, (uint)PackageIds.OpenSerialMonitorCommandId, 0, ref argument));
        }
    }
}
