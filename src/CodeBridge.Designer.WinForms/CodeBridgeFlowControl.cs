using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using CodeBridge.Core.Abstractions;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;

namespace CodeBridge.Designer.WinForms;

[ToolboxItem(true)]
[ToolboxBitmap(typeof(CodeBridgeFlowControl), "CodeBridge.Designer.WinForms.Resources.CodeBridgeFlowControl.bmp")]
[Designer(typeof(CodeBridgeFlowControlDesigner))]
[DesignerCategory("Code")]
[DisplayName("CodeBridge Flow")]
[Description("Hosts, edits, and runs a CodeBridge visual flow inside a WinForms app.")]
[DefaultProperty(nameof(FlowName))]
[DefaultEvent(nameof(FlowCompleted))]
public sealed partial class CodeBridgeFlowControl : Control
{
    private FlowDocument _document = new() { Name = "Embedded Flow" };
    private FlowExecutionMode _executionMode = FlowExecutionMode.Trigger;
    private int _loopIntervalMs = 1000;
    private int _traceDelayMs;
    private CancellationTokenSource? _runCancellation;
    private FlowExecutionEvent? _lastTraceEvent;
    private bool _isRunning;
    private RunOutcome _outcome;
    private Component? _boardComponent;

    private enum RunOutcome
    {
        None,
        Completed,
        Failed
    }

    public CodeBridgeFlowControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable,
            true);

        Size = new Size(220, 72);
        MinimumSize = new Size(160, 56);
        BackColor = DesignerTheme.Surface;
        ForeColor = DesignerTheme.Text;
        Font = DesignerTheme.UiFont;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event EventHandler? FlowStarted;
    public event EventHandler<FlowExecutionResult>? FlowCompleted;
    public event EventHandler<Exception>? FlowFailed;
    public event EventHandler? FlowStopped;
    public event EventHandler<FlowExecutionEvent>? FlowTrace;

    [Category("CodeBridge")]
    [DefaultValue("Embedded Flow")]
    [Description("Friendly name shown by the embedded flow component.")]
    public string FlowName
    {
        get => _document.Name;
        set
        {
            _document.Name = string.IsNullOrWhiteSpace(value) ? "Embedded Flow" : value;
            Invalidate();
        }
    }

    [Category("CodeBridge")]
    [DefaultValue(FlowExecutionMode.Trigger)]
    [Description("Runs once from triggers, or repeats the flow continuously when started from code.")]
    public FlowExecutionMode ExecutionMode
    {
        get => _executionMode;
        set
        {
            _executionMode = value;
            Invalidate();
        }
    }

    [Category("CodeBridge")]
    [DefaultValue(1000)]
    [Description("Delay between loop iterations when ExecutionMode is Loop.")]
    public int LoopIntervalMs
    {
        get => _loopIntervalMs;
        set
        {
            _loopIntervalMs = Math.Max(100, value);
            Invalidate();
        }
    }

    [Category("CodeBridge")]
    [DefaultValue(0)]
    [Description("Optional delay after each node event so execution is visible in UI traces.")]
    public int TraceDelayMs
    {
        get => _traceDelayMs;
        set => _traceDelayMs = Math.Max(0, value);
    }

    [Category("CodeBridge")]
    [DefaultValue(null)]
    [Description("Optional CodeBridge Board component whose active connection is reused by the flow editor and runtime.")]
    public Component? BoardComponent
    {
        get => _boardComponent;
        set
        {
            _boardComponent = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsRunning => _isRunning;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FlowExecutionEvent? LastTraceEvent => _lastTraceEvent;

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public string FlowJson
    {
        get => FlowDocumentJson.Serialize(_document);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _document = new FlowDocument { Name = FlowName };
            }
            else
            {
                _document = FlowDocumentJson.Deserialize(value);
            }

            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FlowDocument Document
    {
        get => Clone(_document);
        set
        {
            _document = Clone(value);
            Invalidate();
        }
    }

    public async Task<FlowExecutionResult> RunAsync(IBoard? board = null, CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            throw new InvalidOperationException("The CodeBridge flow is already running.");

        board ??= ResolveBoardFromComponent(_boardComponent) ?? ResolveConnectedBoardFromContainer() ?? ResolveConnectedBoardFromOwner(FindForm());

        using var stopSource = new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stopSource.Token);

        _runCancellation = stopSource;
        _isRunning = true;
        _outcome = RunOutcome.None;
        _lastTraceEvent = null;
        InvalidateSafe();

        FlowExecutionResult? lastResult = null;
        FlowStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            do
            {
                linkedSource.Token.ThrowIfCancellationRequested();
                lastResult = await ExecuteOnceAsync(board, linkedSource.Token);
                _outcome = RunOutcome.Completed;
                FlowCompleted?.Invoke(this, lastResult);

                if (ExecutionMode != FlowExecutionMode.Loop)
                    return lastResult;

                await Task.Delay(LoopIntervalMs, linkedSource.Token);
            }
            while (!linkedSource.Token.IsCancellationRequested);

            return lastResult ?? EmptyResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return lastResult ?? EmptyResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _outcome = RunOutcome.Failed;
            FlowFailed?.Invoke(this, ex);
            throw;
        }
        finally
        {
            _isRunning = false;
            _runCancellation = null;
            InvalidateSafe();
            FlowStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop()
    {
        _runCancellation?.Cancel();
    }

    public bool ShowEditor(IWin32Window? owner = null)
    {
        return EditFlow(null, owner);
    }

#pragma warning disable CS0618 // the obsolete component is still accepted
    public bool ShowEditor(CodeBridgeEsp32Component? boardComponent, IWin32Window? owner = null)
    {
        var previous = _boardComponent;
        _boardComponent = boardComponent;
        try
        {
            return EditFlow(null, owner);
        }
        finally
        {
            _boardComponent = previous;
            Invalidate();
        }
    }

    public bool ShowEditor(CodeBridgeBoardComponent? boardComponent, IWin32Window? owner = null)
    {
        var previous = _boardComponent;
        _boardComponent = boardComponent;
        try
        {
            return EditFlow(null, owner);
        }
        finally
        {
            _boardComponent = previous;
            Invalidate();
        }
    }

    public bool ShowPresetDialog(IWin32Window? owner = null)
    {
        return ShowPresetDialog(null, owner);
    }

    public void LoadPreset(FlowDocument document)
    {
        ApplyDocument(document);
    }

    public void LoadBlinkPreset(int pin = 2, int delayMs = 1500, bool activeLow = false)
    {
        LoadPreset(BuiltInFlowPresets.CreateBlinkOnce(pin, delayMs, activeLow));
    }

    public void LoadDigitalReadDebugPreset(int pin = 2)
    {
        LoadPreset(BuiltInFlowPresets.CreateDigitalReadDebug(pin));
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        EditFlow(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F4)
        {
            EditFlow(null);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var background = new SolidBrush(BackColor);
        using var border = new Pen(Focused ? DesignerTheme.Copper : DesignerTheme.Border);
        var bounds = ClientRectangle;
        bounds.Width -= 1;
        bounds.Height -= 1;
        e.Graphics.FillRectangle(background, bounds);
        e.Graphics.DrawRectangle(border, bounds);

        // The badge says the state twice, by colour and by shape: ready (blue ring), running (green dot), completed (green check), failed (red cross).
        var glyphBounds = new Rectangle(12, 14, 36, 36);
        var accent = _isRunning ? DesignerTheme.Signal : _outcome switch
        {
            RunOutcome.Completed => DesignerTheme.Signal,
            RunOutcome.Failed => DesignerTheme.Error,
            _ => DesignerTheme.Copper
        };
        using var glyphBrush = new SolidBrush(accent);
        using var glyphPen = new Pen(DesignerTheme.Workbench, 2.4F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (!_isRunning && _outcome == RunOutcome.None)
        {
            using var ring = new Pen(accent, 2.4F);
            e.Graphics.DrawEllipse(ring, Rectangle.Inflate(glyphBounds, -1, -1));
            using var play = new SolidBrush(accent);
            e.Graphics.FillPolygon(play, new[]
            {
                new Point(glyphBounds.Left + 14, glyphBounds.Top + 10),
                new Point(glyphBounds.Left + 14, glyphBounds.Top + 26),
                new Point(glyphBounds.Left + 27, glyphBounds.Top + 18)
            });
        }
        else
        {
            e.Graphics.FillEllipse(glyphBrush, glyphBounds);
            if (_isRunning)
            {
                using var hole = new SolidBrush(DesignerTheme.Workbench);
                e.Graphics.FillEllipse(hole, Rectangle.Inflate(glyphBounds, -13, -13));
            }
            else if (_outcome == RunOutcome.Failed)
            {
                e.Graphics.DrawLine(glyphPen, glyphBounds.Left + 12, glyphBounds.Top + 12, glyphBounds.Left + 24, glyphBounds.Top + 24);
                e.Graphics.DrawLine(glyphPen, glyphBounds.Left + 24, glyphBounds.Top + 12, glyphBounds.Left + 12, glyphBounds.Top + 24);
            }
            else
            {
                e.Graphics.DrawLine(glyphPen, glyphBounds.Left + 10, glyphBounds.Top + 18, glyphBounds.Left + 16, glyphBounds.Top + 24);
                e.Graphics.DrawLine(glyphPen, glyphBounds.Left + 16, glyphBounds.Top + 24, glyphBounds.Left + 26, glyphBounds.Top + 12);
            }
        }

        var titleBounds = new Rectangle(58, 10, Math.Max(10, Width - 68), 22);
        var subtitleBounds = new Rectangle(58, 33, Math.Max(10, Width - 68), 20);
        TextRenderer.DrawText(e.Graphics, FlowName, DesignerTheme.TitleFont, titleBounds, ForeColor, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            e.Graphics,
            GetSubtitleText(),
            DesignerTheme.SmallFont,
            subtitleBounds,
            DesignerTheme.MutedText,
            TextFormatFlags.EndEllipsis);
    }

    internal bool EditFlow(IComponentChangeService? changeService, IWin32Window? owner = null)
    {
        var externalBoard = ResolveEditorBoard(owner);
        using var dialog = new CodeBridgeFlowEditorDialog(_document, externalBoard);
        if (dialog.ShowDialog(owner ?? FindForm()) != DialogResult.OK)
            return false;

        var oldValue = FlowJson;
        ApplyDocument(dialog.Document, changeService, oldValue);
        return true;
    }

    internal bool ShowPresetDialog(IComponentChangeService? changeService, IWin32Window? owner = null)
    {
        var boardProfile = BuiltInBoardProfiles.FindById(_document.BoardId) ?? BuiltInBoardProfiles.Default;
        using var dialog = new CodeBridgeFlowPresetDialog(boardProfile);
        if (dialog.ShowDialog(owner ?? FindForm()) != DialogResult.OK)
            return false;

        ApplyDocument(dialog.Document, changeService);
        return true;
    }

    private async Task<FlowExecutionResult> ExecuteOnceAsync(IBoard? board, CancellationToken cancellationToken)
    {
        var boardProfile = BuiltInBoardProfiles.FindById(_document.BoardId) ?? BuiltInBoardProfiles.Default;
        var runtime = new FlowRuntime(BuiltInBlockCatalog.Create(boardProfile));
        return await runtime.ExecuteAsync(
            _document,
            new FlowExecutionContext
            {
                Board = board,
                EventHandler = HandleRuntimeEventAsync,
                TraceDelay = TimeSpan.FromMilliseconds(TraceDelayMs)
            },
            cancellationToken);
    }

    private ValueTask HandleRuntimeEventAsync(
        FlowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        _lastTraceEvent = executionEvent;
        FlowTrace?.Invoke(this, executionEvent);
        InvalidateSafe();
        return ValueTask.CompletedTask;
    }

    private string GetSubtitleText()
    {
        if (_isRunning && _lastTraceEvent is not null)
            return $"{FormatTraceKind(_lastTraceEvent.Kind)} | {_lastTraceEvent.NodeId}";

        var state = _isRunning ? "Running" : _outcome switch
        {
            RunOutcome.Completed => "Completed",
            RunOutcome.Failed => "Failed",
            _ => "Ready"
        };
        return $"{state} | {ExecutionMode} | {_document.Nodes.Count} blocks";
    }

    private static string FormatTraceKind(FlowExecutionEventKind kind)
    {
        return kind switch
        {
            FlowExecutionEventKind.NodeStarted => "Running",
            FlowExecutionEventKind.NodeCompleted => "Completed",
            FlowExecutionEventKind.NodeSkipped => "Skipped",
            FlowExecutionEventKind.NodeFailed => "Failed",
            _ => kind.ToString()
        };
    }

    private void ApplyDocument(
        FlowDocument document,
        IComponentChangeService? changeService = null,
        string? oldFlowJson = null)
    {
        var property = TypeDescriptor.GetProperties(this)[nameof(FlowJson)];
        var oldValue = oldFlowJson ?? FlowJson;
        changeService?.OnComponentChanging(this, property);
        _document = Clone(document);
        Invalidate();
        changeService?.OnComponentChanged(this, property, oldValue, FlowJson);
    }

    private void InvalidateSafe()
    {
        if (IsDisposed)
            return;

        if (IsHandleCreated && InvokeRequired)
            BeginInvoke((MethodInvoker)Invalidate);
        else
            Invalidate();
    }

    private static FlowExecutionResult EmptyResult() =>
        new(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase));

    private static FlowDocument Clone(FlowDocument document) =>
        FlowDocumentJson.Deserialize(FlowDocumentJson.Serialize(document));

    private IBoard? ResolveEditorBoard(IWin32Window? owner) =>
        ResolveBoardFromComponent(_boardComponent) ??
        ResolveConnectedBoardFromContainer() ??
        ResolveConnectedBoardFromOwner(owner) ??
        ResolveConnectedBoardFromOwner(FindForm());

    private IBoard? ResolveConnectedBoardFromContainer()
    {
        var components = Site?.Container?.Components;
        if (components is null)
            return null;

        foreach (var component in components.OfType<Component>())
        {
            if (ReferenceEquals(component, this))
                continue;

            var board = ResolveBoardFromComponent(component);
            if (board is not null)
                return board;
        }

        return null;
    }

    private static IBoard? ResolveConnectedBoardFromOwner(object? owner)
    {
        if (owner is null)
            return null;

        var type = owner.GetType();
        while (type is not null && type != typeof(object))
        {
            foreach (var field in type.GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic))
            {
                if (!typeof(Component).IsAssignableFrom(field.FieldType))
                    continue;

                try
                {
                    var board = ResolveBoardFromComponent(field.GetValue(owner) as Component);
                    if (board is not null)
                        return board;
                }
                catch
                {
                    // Reflection-based discovery is best effort so opening the editor never fails because of it.
                }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static IBoard? ResolveBoardFromComponent(Component? component)
    {
        var board = component switch
        {
            CodeBridgeBoardComponent boardComponent => boardComponent.Board,
            CodeBridgeEsp32Component esp32Component => esp32Component.Board,
            _ => null
        };
#pragma warning restore CS0618

        return board is { IsConnected: true } ? board : null;
    }
}
