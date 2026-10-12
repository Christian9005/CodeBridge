#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using CodeBridge.VisualStudio.Editor;
using CodeBridge.VisualStudio.Monitor;
using Task = System.Threading.Tasks.Task;

namespace CodeBridge.VisualStudio.Firmware
{
    /// <summary>
    /// Code mode: Tools > CodeBridge Firmware (New project, Build, Upload, Clean). The work is done by PlatformIO through CodeBridge.FlowHost;
    /// the compiler output goes to the CodeBridge Output pane and every error and warning to the Error List, with a link to the line.
    /// </summary>
    internal sealed class FirmwareCommands
    {
        private readonly AsyncPackage _package;
        private readonly ErrorListProvider _errors;
        private bool _busy;

        private FirmwareCommands(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            _errors = new ErrorListProvider(package) { ProviderName = "CodeBridge Firmware", ProviderGuid = new Guid("0b3d5a3c-7c0f-4f3e-8a55-1b8e4c9d6a21") };

            Add(commandService, PackageIds.NewFirmwareProjectCommandId, (_, __) => Run(NewProjectAsync));
            Add(commandService, PackageIds.BuildFirmwareCommandId, (_, __) => Run(() => RunPioAsync("build")));
            Add(commandService, PackageIds.UploadFirmwareCommandId, (_, __) => Run(() => RunPioAsync("upload")));
            Add(commandService, PackageIds.CleanFirmwareCommandId, (_, __) => Run(() => RunPioAsync("clean")));
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
                _ = new FirmwareCommands(package, commandService);
        }

        private static void Add(OleMenuCommandService service, int id, EventHandler handler) =>
            service.AddCommand(new MenuCommand(handler, new CommandID(PackageGuids.CommandSet, id)));

        private void Run(Func<Task> action)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // never let an exception escape a command handler: it would take Visual Studio down with it
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Write("ERROR: " + ex.Message);
                    SetStatus("CodeBridge Firmware: " + ex.Message);
                }
            });
        }

        // ------------------------------------------------------------------ new project

        private async Task NewProjectAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!HostClient.IsAvailable)
            {
                ShowMessage("The CodeBridge helper is missing from the extension. Reinstall CodeBridge Visual Studio Tools.");
                return;
            }

            var window = new NewFirmwareProjectWindow();
            if (window.ShowDialog() != true)
                return;

            VsOutput.TryShow();
            Write($"Creating {window.TemplateName} firmware project for {window.BoardName} in {window.Directory}...");
            var created = await RunHostAsync($"pio-new --board {window.BoardId} --dir {HostClient.Quote(window.Directory)} --template {window.Template}", null);
            if (created.Entry == null)
            {
                SetStatus("The firmware project could not be created.");
                return;
            }

            Write($"Created {created.Entry}. Build it with Tools > CodeBridge Firmware: Build.");
            SetStatus("Firmware project created.");
            var dte = await _package.GetServiceAsync(typeof(SDTE)) as DTE;
            dte?.ItemOperations.OpenFile(created.Entry);
        }

        // ------------------------------------------------------------------ build / upload / clean

        private async Task RunPioAsync(string action)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_busy)
            {
                SetStatus("CodeBridge Firmware: another operation is still running.");
                return;
            }

            if (!HostClient.IsAvailable)
            {
                ShowMessage("The CodeBridge helper is missing from the extension. Reinstall CodeBridge Visual Studio Tools.");
                return;
            }

            var root = await LocateProjectAsync();
            if (root == null)
            {
                ShowMessage("No PlatformIO project was found. Open a file of the firmware project (the folder with platformio.ini), or create one with Tools > CodeBridge Firmware: New project.");
                return;
            }

            var arguments = $"pio-{action} --dir {HostClient.Quote(root)}";
            if (action == "upload")
            {
                var port = SerialMonitorHandoff.PreferredPort ?? EditorSettings.Load().LastPort;
                if (string.IsNullOrWhiteSpace(port) || !BoardTokens.IsSerialPort(port!))
                {
                    ShowMessage("Select the board's USB port first: open a flow in the CodeBridge editor and pick the port in the toolbar, or open the Serial Monitor and choose it there.");
                    return;
                }

                // a serial monitor holding the port would make the upload fail
                if (SerialPortLease.Release(port!))
                {
                    Write($"The serial monitor was closed so {port} can be used for the upload.");
                    await Task.Delay(500);
                }

                arguments += $" --port {HostClient.Quote(port!)}";
            }

            VsOutput.TryShow();
            Write($"=== PlatformIO {action}: {root} ===");
            _errors.Tasks.Clear();
            _busy = true;
            try
            {
                var outcome = await RunHostAsync(arguments, root);
                SetStatus(outcome.Message ?? $"CodeBridge Firmware {action} finished.");
                if (outcome.Errors > 0)
                    _errors.Show();
            }
            finally
            {
                _busy = false;
            }
        }

        private async System.Threading.Tasks.Task<string?> LocateProjectAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = await _package.GetServiceAsync(typeof(SDTE)) as DTE2;
            if (dte == null)
                return null;

            var paths = new List<string?>();
            try
            {
                paths.Add(dte.ActiveDocument?.FullName);
                foreach (SelectedItem item in dte.SelectedItems)
                {
                    paths.Add(item.ProjectItem?.FileNames[1]);
                    paths.Add(item.Project?.FullName);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is ArgumentException || ex is InvalidCastException)
            {
                // a document without a file name: use the next hint
            }

            string? solution = null;
            try { solution = string.IsNullOrEmpty(dte.Solution?.FullName) ? null : Path.GetDirectoryName(dte.Solution!.FullName); }
            catch (ArgumentException) { }

            return FirmwareProjects.Locate(paths, solution);
        }

        // ------------------------------------------------------------------ talking to the host

        private sealed class Outcome
        {
            public string? Message { get; set; }
            public int Errors { get; set; }
            public string? Entry { get; set; }
        }

        private async System.Threading.Tasks.Task<Outcome> RunHostAsync(string arguments, string? projectRoot)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var outcome = new Outcome();
            var finished = new System.Threading.Tasks.TaskCompletionSource<bool>();

            void OnMessage(HostMessage message) => dispatcher.BeginInvoke(new Action(() =>
            {
                switch (message.Type)
                {
                    case "log":
                        Write(message.Str("text") ?? string.Empty);
                        break;

                    case "status":
                        SetStatus(message.Str("message") ?? string.Empty);
                        break;

                    case "diagnostic":
                        AddDiagnostic(message);
                        if (message.Str("severity") == "error")
                            outcome.Errors++;
                        break;

                    case "pio-created":
                        outcome.Entry = message.Str("entry");
                        break;

                    case "result":
                        outcome.Message = message.Str("message");
                        Write(outcome.Message ?? string.Empty);
                        break;

                    case "error":
                        outcome.Message = message.Str("message");
                        outcome.Errors++;
                        Write("ERROR: " + message.Str("message"));
                        break;
                }
            }));

            HostClient.Start(
                arguments,
                OnMessage,
                (code, error) => dispatcher.BeginInvoke(new Action(() =>
                {
                    if (code != 0 && outcome.Message == null && !string.IsNullOrWhiteSpace(error))
                    {
                        outcome.Message = HostClient.Friendly(error);
                        Write("ERROR: " + outcome.Message);
                    }

                    finished.TrySetResult(true);
                })));

            await finished.Task;
            return outcome;
        }

        private void AddDiagnostic(HostMessage message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var isError = message.Str("severity") == "error";
            int.TryParse(message.Str("line"), out var line);
            int.TryParse(message.Str("column"), out var column);

            var task = new ErrorTask
            {
                Category = TaskCategory.BuildCompile,
                ErrorCategory = isError ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
                Text = message.Str("message") ?? string.Empty,
                Document = message.Str("file") ?? string.Empty,
                Line = Math.Max(0, line - 1),
                Column = Math.Max(0, column - 1)
            };
            task.Navigate += (s, e) => _errors.Navigate((ErrorTask)s!, VSConstants.LOGVIEWID.Code_guid);
            _errors.Tasks.Add(task);
        }

        // ------------------------------------------------------------------ small things

        private static void Write(string text) => VsOutput.TryWrite(text + "\r\n");

        private void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SDTE)) is DTE2 dte)
                    dte.StatusBar.Text = text;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
        }

        private void ShowMessage(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(_package, text, "CodeBridge Firmware", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
