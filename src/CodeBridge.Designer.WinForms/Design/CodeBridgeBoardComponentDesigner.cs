using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms.Design;
using CodeBridge.Flow;
using CodeBridge.Transport;

namespace CodeBridge.Designer.WinForms;

internal sealed class CodeBridgeBoardComponentDesigner : ComponentDesigner
{
    private DesignerActionListCollection? _actionLists;
    private readonly DesignerVerbCollection _verbs;

    public CodeBridgeBoardComponentDesigner()
    {
        _verbs =
        [
            new DesignerVerb("Detect Serial Ports...", (_, _) => DetectSerialPorts()),
            new DesignerVerb("Upload Firmware...", async (_, _) => await UploadFirmwareAsync()),
            new DesignerVerb("Use ESP32 DevKit", (_, _) => SetBoard(BuiltInBoardProfiles.Esp32DevKit)),
            new DesignerVerb("Use Arduino Uno", (_, _) => SetBoard(BuiltInBoardProfiles.ArduinoUno)),
            new DesignerVerb("Use Serial", (_, _) => SetTransportMode(CodeBridgeTransportMode.Serial)),
            new DesignerVerb("Use WiFi", (_, _) => SetTransportMode(CodeBridgeTransportMode.WiFi))
        ];
    }

    public override DesignerVerbCollection Verbs => _verbs;

    public override DesignerActionListCollection ActionLists =>
        _actionLists ??=
        [
            new CodeBridgeBoardComponentActionList(this)
        ];

    internal void DetectSerialPorts()
    {
        if (Component is not CodeBridgeBoardComponent component)
            return;

        var ports = BoardDiscovery.DiscoverPorts().ToList();
        if (ports.Count == 0)
        {
            MessageBox.Show(
                "No serial ports were detected. Connect a board by USB and try again.",
                "CodeBridge Board",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // a real board before the legacy COM1 of the PC
        var selectedPort = (BoardDiscovery.RankCandidates(ports).FirstOrDefault() ?? ports[0]).Name;
        var suggestedBoard = Hardware.BoardPortDetector.SuggestBoardForPort(selectedPort);
        SetProperty(component, nameof(CodeBridgeBoardComponent.TransportMode), CodeBridgeTransportMode.Serial);
        SetProperty(component, nameof(CodeBridgeBoardComponent.PortName), selectedPort);
        if (suggestedBoard is not null)
            SetProperty(component, nameof(CodeBridgeBoardComponent.BoardId), suggestedBoard.Id);

        var boardText = suggestedBoard is null
            ? "Board type was not recognized automatically."
            : $"Detected board: {suggestedBoard.DisplayName}";

        MessageBox.Show(
            "Detected serial ports:" + Environment.NewLine +
            string.Join(Environment.NewLine, ports.Select(port => port.Name)) +
            Environment.NewLine + Environment.NewLine +
            $"Selected: {selectedPort}" + Environment.NewLine +
            boardText,
            "CodeBridge Board",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    internal async Task UploadFirmwareAsync()
    {
        if (Component is not CodeBridgeBoardComponent component)
            return;

        try
        {
            var result = await component.UploadFirmwareAsync();
            MessageBox.Show(
                FormatUploadResult(component, result),
                "CodeBridge Firmware",
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "CodeBridge Firmware",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    internal void SetBoard(BoardProfile boardProfile)
    {
        if (Component is not CodeBridgeBoardComponent component)
            return;

        SetProperty(component, nameof(CodeBridgeBoardComponent.BoardId), boardProfile.Id);
        if (!Hardware.BoardConnectionService.IsEsp32Profile(boardProfile))
            SetProperty(component, nameof(CodeBridgeBoardComponent.TransportMode), CodeBridgeTransportMode.Serial);
    }

    internal void SetTransportMode(CodeBridgeTransportMode mode)
    {
        if (Component is CodeBridgeBoardComponent component)
            SetProperty(component, nameof(CodeBridgeBoardComponent.TransportMode), mode);
    }

    internal void SetProperty(object component, string propertyName, object? value)
    {
        var property = TypeDescriptor.GetProperties(component)[propertyName];
        if (property is null)
            return;

        var oldValue = property.GetValue(component);
        var changeService = (IComponentChangeService?)GetService(typeof(IComponentChangeService));
        changeService?.OnComponentChanging(component, property);
        property.SetValue(component, value);
        changeService?.OnComponentChanged(component, property, oldValue, value);
    }

    private static string FormatUploadResult(CodeBridgeBoardComponent component, Hardware.FirmwareUploadResult result)
    {
        var title = result.Success
            ? $"Firmware uploaded to {component.BoardName} on {component.PortName}."
            : $"Firmware upload failed for {component.BoardName} on {component.PortName}.";

        return string.Join(
            Environment.NewLine,
            title,
            result.Success ? "Connect after the board restarts." : "Check the tool output below.",
            "",
            result.Log.Length > 2500 ? result.Log[..2500] : result.Log);
    }
}

internal sealed class CodeBridgeBoardComponentActionList : DesignerActionList
{
    private readonly CodeBridgeBoardComponentDesigner _designer;
    private readonly CodeBridgeBoardComponent _component;

    public CodeBridgeBoardComponentActionList(CodeBridgeBoardComponentDesigner designer)
        : base(designer.Component)
    {
        _designer = designer;
        _component = (CodeBridgeBoardComponent)designer.Component;
    }

    public string BoardId
    {
        get => _component.BoardId;
        set => _designer.SetProperty(_component, nameof(CodeBridgeBoardComponent.BoardId), value);
    }

    public CodeBridgeTransportMode TransportMode
    {
        get => _component.TransportMode;
        set => _designer.SetProperty(_component, nameof(CodeBridgeBoardComponent.TransportMode), value);
    }

    public string PortName
    {
        get => _component.PortName;
        set => _designer.SetProperty(_component, nameof(CodeBridgeBoardComponent.PortName), value);
    }

    public int BaudRate
    {
        get => _component.BaudRate;
        set => _designer.SetProperty(_component, nameof(CodeBridgeBoardComponent.BaudRate), value);
    }

    public string Host
    {
        get => _component.Host;
        set => _designer.SetProperty(_component, nameof(CodeBridgeBoardComponent.Host), value);
    }

    public int TcpPort
    {
        get => _component.TcpPort;
        set => _designer.SetProperty(_component, nameof(CodeBridgeBoardComponent.TcpPort), value);
    }

    public void DetectSerialPorts() => _designer.DetectSerialPorts();

    public void UploadFirmware()
    {
        _ = SafeUploadFirmwareAsync();
    }

    private async Task SafeUploadFirmwareAsync()
    {
        try
        {
            await _designer.UploadFirmwareAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "CodeBridge Firmware Upload Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    public void UseEsp32DevKit() => _designer.SetBoard(BuiltInBoardProfiles.Esp32DevKit);

    public void UseArduinoUno() => _designer.SetBoard(BuiltInBoardProfiles.ArduinoUno);

    public void UseSerial() => _designer.SetTransportMode(CodeBridgeTransportMode.Serial);

    public void UseWiFi() => _designer.SetTransportMode(CodeBridgeTransportMode.WiFi);

    public override DesignerActionItemCollection GetSortedActionItems()
    {
        return
        [
            new DesignerActionHeaderItem("CodeBridge Board"),
            new DesignerActionMethodItem(this, nameof(DetectSerialPorts), "Detect Serial Ports...", "CodeBridge Board", "Find connected serial ports and select the first one.", includeAsDesignerVerb: true),
            new DesignerActionMethodItem(this, nameof(UploadFirmware), "Upload Firmware...", "CodeBridge Board", "Flash CodeBridge firmware to the selected board and port.", includeAsDesignerVerb: true),
            new DesignerActionPropertyItem(nameof(BoardId), "Board", "CodeBridge Board", "Board profile used for firmware and pin validation."),
            new DesignerActionPropertyItem(nameof(TransportMode), "Transport", "CodeBridge Board", "Serial or ESP32 WiFi connection mode."),
            new DesignerActionPropertyItem(nameof(PortName), "Port Name", "CodeBridge Board", "Serial port, for example COM3. \"auto\" uses the board found on USB."),
            new DesignerActionPropertyItem(nameof(BaudRate), "Baud Rate", "CodeBridge Board", "Serial baud rate."),
            new DesignerActionPropertyItem(nameof(Host), "WiFi Host", "CodeBridge Board", "ESP32 IP address when using WiFi."),
            new DesignerActionPropertyItem(nameof(TcpPort), "TCP Port", "CodeBridge Board", "ESP32 TCP port when using WiFi."),
            new DesignerActionMethodItem(this, nameof(UseEsp32DevKit), "Use ESP32 DevKit", "CodeBridge Board", "Set ESP32 DevKit as the active board.", includeAsDesignerVerb: false),
            new DesignerActionMethodItem(this, nameof(UseArduinoUno), "Use Arduino Uno", "CodeBridge Board", "Set Arduino Uno as the active board.", includeAsDesignerVerb: false),
            new DesignerActionMethodItem(this, nameof(UseSerial), "Use Serial", "CodeBridge Board", "Configure the component for USB serial communication.", includeAsDesignerVerb: false),
            new DesignerActionMethodItem(this, nameof(UseWiFi), "Use WiFi", "CodeBridge Board", "Configure ESP32 WiFi communication.", includeAsDesignerVerb: false)
        ];
    }
}
