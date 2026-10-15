using CodeBridge.Flow;
using CodeBridge.Flow.Serialization;
using CodeBridge.Core.Abstractions;

namespace CodeBridge.Designer.WinForms;

internal sealed class CodeBridgeFlowEditorDialog : Form
{
    private readonly CodeBridgeFlowDesignerControl _designer = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();

    public CodeBridgeFlowEditorDialog(FlowDocument document, IBoard? externalBoard = null)
    {
        Text = "CodeBridge Flow Editor";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(1120, 760);
        Size = GetInitialSize();
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DesignerTheme.Workbench;
        Font = DesignerTheme.UiFont;
        KeyPreview = true;

        _designer.Dock = DockStyle.Fill;
        _designer.Document = Clone(document);
        if (externalBoard is not null)
            _designer.UseExternalBoard(externalBoard, $"WinForms {externalBoard.Name} component");

        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Width = 86;

        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Width = 86;
        DesignerTheme.StyleButton(_okButton, primary: true);
        DesignerTheme.StyleButton(_cancelButton);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 7, 10, 7),
            BackColor = DesignerTheme.Surface
        };
        footer.Controls.Add(_okButton);
        footer.Controls.Add(_cancelButton);

        Controls.Add(_designer);
        Controls.Add(footer);
        DesignerTheme.ApplyDarkChrome(this);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        Shown += (_, _) =>
        {
            _designer.RestoreDesignerLayout();
            _designer.FitDocumentToView();
        };
    }

    public FlowDocument Document => Clone(_designer.Document);

    private static FlowDocument Clone(FlowDocument document) =>
        FlowDocumentJson.Deserialize(FlowDocumentJson.Serialize(document));

    private static Size GetInitialSize()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
        return new Size(
            Math.Min(1440, Math.Max(1120, area.Width - 96)),
            Math.Min(900, Math.Max(760, area.Height - 96)));
    }
}
