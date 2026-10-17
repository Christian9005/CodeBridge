using System.ComponentModel;
using System.Reflection;
using CodeBridge.Flow;
using CodeBridge.Flow.Serialization;

namespace CodeBridge.Designer.WinForms;

/// <summary>Tells which flow file was loaded and whether it was a first load or a reload after the file changed.</summary>
public sealed class FlowFileEventArgs : EventArgs
{
    public FlowFileEventArgs(string filePath, bool isReload)
    {
        FilePath = filePath;
        IsReload = isReload;
    }

    public string FilePath { get; }

    public bool IsReload { get; }
}

// The flow of the control can live in a .cbflow file of the project: the one the Visual Studio editor opens.
public sealed partial class CodeBridgeFlowControl
{
    private string _flowFile = string.Empty;
    private string? _resolvedFlowFile;
    private bool _watchFlowFile = true;
    private FileSystemWatcher? _flowFileWatcher;
    private System.Threading.Timer? _flowFileDebounce;

    /// <summary>How a reload gets back to the UI thread after the file changed. Replaced by the tests.</summary>
    internal Action<Action>? PostToUi;

    /// <summary>Raised after the flow file was read: the first time, and again every time it is saved.</summary>
    public event EventHandler<FlowFileEventArgs>? FlowFileLoaded;

    /// <summary>Raised when the flow file is missing or cannot be read. The flow that was loaded before keeps working.</summary>
    public event EventHandler<Exception>? FlowFileFailed;

    [Category("CodeBridge")]
    [DefaultValue("")]
    [Description("The .cbflow file this control runs, for example Flows\\Blink.cbflow. A relative path is looked up in the project folder while debugging and next to the program otherwise. The control reloads it when it is saved.")]
    public string FlowFile
    {
        get => _flowFile;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (value == _flowFile)
                return;

            _flowFile = value;
            if (IsDesignTime)
            {
                Invalidate();
                return;
            }

            LoadFlowFile();
        }
    }

    [Category("CodeBridge")]
    [DefaultValue(true)]
    [Description("Reload the flow file when it is saved, so a change made in the editor shows up in the running app.")]
    public bool WatchFlowFile
    {
        get => _watchFlowFile;
        set
        {
            _watchFlowFile = value;
            if (!value)
                StopWatchingFlowFile();
            else if (_resolvedFlowFile is not null && !IsDesignTime)
                StartWatchingFlowFile(_resolvedFlowFile);
        }
    }

    /// <summary>The file the flow was read from (the project's own file while debugging), or null when there is none.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? ResolvedFlowFile => _resolvedFlowFile;

    /// <summary>Finds <see cref="FlowFile"/> again and reads it. Returns false (and raises <see cref="FlowFileFailed"/>) when that is not possible.</summary>
    public bool ReloadFlowFile() => LoadFlowFile(isReload: true);

    /// <summary>Opens the flow file in the editor: in Visual Studio when the project is open there, otherwise with the program Windows has for .cbflow.</summary>
    public bool OpenFlowFile()
    {
        var path = _resolvedFlowFile ?? FlowFileLocator.Resolve(_flowFile, Assembly.GetEntryAssembly());
        return path is not null && FlowEditorLauncher.Open(path);
    }

    private bool IsDesignTime => DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime;

    private bool LoadFlowFile(bool isReload = false)
    {
        StopWatchingFlowFile();
        if (_flowFile.Length == 0)
        {
            _resolvedFlowFile = null;
            return false;
        }

        var path = FlowFileLocator.Resolve(_flowFile, Assembly.GetEntryAssembly());
        if (path is null)
        {
            _resolvedFlowFile = null;
            RaiseFlowFileFailed(new FileNotFoundException(
                $"The flow file '{_flowFile}' was not found. Add it to the project (it is copied next to the program when you build) or give its full path.",
                _flowFile));
            return false;
        }

        _resolvedFlowFile = path;
        var loaded = ReadFlowFile(path, isReload);
        StartWatchingFlowFile(path);
        return loaded;
    }

    private bool ReadFlowFile(string path, bool isReload)
    {
        FlowDocument document;
        try
        {
            document = FlowDocumentJson.Deserialize(ReadAllTextShared(path));
        }
        catch (Exception ex)
        {
            // Half-saved or being edited: keep the flow that works and say what is wrong.
            RaiseFlowFileFailed(new InvalidDataException($"The flow file '{path}' could not be loaded: {ex.Message}", ex));
            return false;
        }

        _document = document;
        InvalidateSafe();
        FlowFileLoaded?.Invoke(this, new FlowFileEventArgs(path, isReload));
        return true;
    }

    /// <summary>An editor may still hold the file for a moment when it saves it, so a few short retries.</summary>
    private static string ReadAllTextShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(60);
            }
        }
    }

    private void RaiseFlowFileFailed(Exception exception) => FlowFileFailed?.Invoke(this, exception);

    private void StartWatchingFlowFile(string path)
    {
        StopWatchingFlowFile();
        var directory = Path.GetDirectoryName(path);
        if (!_watchFlowFile || IsDesignTime || directory is null || !Directory.Exists(directory))
            return;

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        watcher.Changed += (_, _) => FlowFileTouched(path);
        watcher.Created += (_, _) => FlowFileTouched(path);
        watcher.Renamed += (_, _) => FlowFileTouched(path);
        watcher.EnableRaisingEvents = true;
        _flowFileWatcher = watcher;
    }

    private void StopWatchingFlowFile()
    {
        _flowFileWatcher?.Dispose();
        _flowFileWatcher = null;
    }

    /// <summary>A save usually shows up as several events in a row: wait until they stop, then reload once on the UI thread.</summary>
    private void FlowFileTouched(string path)
    {
        var debounce = _flowFileDebounce ??= new System.Threading.Timer(
            _ => (PostToUi ?? PostToUiThread)(() =>
            {
                if (!IsDisposed && _resolvedFlowFile == path)
                    ReadFlowFile(path, isReload: true);
            }),
            null,
            Timeout.Infinite,
            Timeout.Infinite);

        try
        {
            debounce.Change(200, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // The control went away while the file changed.
        }
    }

    private void PostToUiThread(Action action)
    {
        if (!IsDisposed && IsHandleCreated)
            BeginInvoke(action);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopWatchingFlowFile();
            _flowFileDebounce?.Dispose();
            _flowFileDebounce = null;
        }

        base.Dispose(disposing);
    }
}
