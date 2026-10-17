using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CodeBridge.Designer.WinForms;

/// <summary>
/// Opens a .cbflow file for editing: in the Visual Studio that has the project open (its CodeBridge editor), or else with whatever Windows has for the file.
/// Visual Studio is reached through the running object table, so the app needs no Visual Studio assemblies.
/// </summary>
internal static class FlowEditorLauncher
{
    public static bool Open(string path)
    {
        if (!File.Exists(path))
            return false;

        return TryOpenInVisualStudio(path) || TryOpenWithShell(path);
    }

    /// <summary>True when the file lives inside the folder (the solution folder of a running Visual Studio).</summary>
    internal static bool IsInside(string directory, string path)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOpenInVisualStudio(string path)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            foreach (var instance in RunningVisualStudios())
            {
                try
                {
                    dynamic dte = instance;
                    string solution = dte.Solution.FullName;
                    if (string.IsNullOrWhiteSpace(solution) || !IsInside(Path.GetDirectoryName(solution) ?? string.Empty, path))
                        continue;

                    dte.ItemOperations.OpenFile(path);
                    dte.MainWindow.Activate();
                    return true;
                }
                catch (Exception)
                {
                    // A Visual Studio that is busy or shutting down: try the next one.
                }
            }
        }
        catch (Exception)
        {
            // The running object table cannot be read: use the shell.
        }

        return false;
    }

    private static bool TryOpenWithShell(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<object> RunningVisualStudios()
    {
        var found = new List<object>();
        if (GetRunningObjectTable(0, out var table) != 0 || table is null)
            return found;

        table.EnumRunning(out var monikers);
        if (CreateBindCtx(0, out var context) != 0 || context is null)
            return found;

        monikers.Reset();
        var current = new IMoniker[1];
        while (monikers.Next(1, current, IntPtr.Zero) == 0)
        {
            current[0].GetDisplayName(context, null, out var name);
            if (name is null || !name.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase))
                continue;

            if (table.GetObject(current[0], out var instance) == 0 && instance is not null)
                found.Add(instance);
        }

        return found;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable? table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx? context);
}
