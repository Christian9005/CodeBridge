#pragma warning disable CS0618 // the designer of an obsolete component still has to name it
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms.Design;
using CodeBridge.Transport;

namespace CodeBridge.Designer.WinForms;

internal sealed class CodeBridgeEsp32ComponentDesigner : ComponentDesigner
{
    private DesignerActionListCollection? _actionLists;
    private readonly DesignerVerbCollection _verbs;

    public CodeBridgeEsp32ComponentDesigner()
    {
        _verbs =
        [
            new DesignerVerb("Detect Serial Ports...", (_, _) => DetectSerialPorts()),
            new DesignerVerb("Use Serial", (_, _) => SetTransportMode(CodeBridgeTransportMode.Serial)),
            new DesignerVerb("Use WiFi", (_, _) => SetTransportMode(CodeBridgeTransportMode.WiFi))
        ];
    }

    public override DesignerVerbCollection Verbs => _verbs;

    public override DesignerActionListCollection ActionLists =>
        _actionLists ??=
        [
            new CodeBridgeEsp32ComponentActionList(this)
        ];

    internal void DetectSerialPorts()
    {
        if (Component is not CodeBridgeEsp32Component component)
            return;

        var ports = BoardDiscovery.DiscoverPorts().ToList();
        if (ports.Count == 0)
        {
            MessageBox.Show(
                "No serial ports were detected. Connect the ESP32 and try again.",
                "CodeBridge ESP32",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        SetProperty(component, nameof(CodeBridgeEsp32Component.TransportMode), CodeBridgeTransportMode.Serial);
        SetProperty(component, nameof(CodeBridgeEsp32Component.PortName), ports[0].Name);

        MessageBox.Show(
            "Detected serial ports:" + Environment.NewLine + string.Join(Environment.NewLine, ports.Select(port => port.Name)) +
            Environment.NewLine + Environment.NewLine + $"Selected: {ports[0].Name}",
            "CodeBridge ESP32",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    internal void SetTransportMode(CodeBridgeTransportMode mode)
    {
        if (Component is CodeBridgeEsp32Component component)
            SetProperty(component, nameof(CodeBridgeEsp32Component.TransportMode), mode);
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
}

internal sealed class CodeBridgeEsp32ComponentActionList : DesignerActionList
{
    private readonly CodeBridgeEsp32ComponentDesigner _designer;
    private readonly CodeBridgeEsp32Component _component;

    public CodeBridgeEsp32ComponentActionList(CodeBridgeEsp32ComponentDesigner designer)
        : base(designer.Component)
    {
        _designer = designer;
        _component = (CodeBridgeEsp32Component)designer.Component;
    }

    public CodeBridgeTransportMode TransportMode
    {
        get => _component.TransportMode;
        set => _designer.SetProperty(_component, nameof(CodeBridgeEsp32Component.TransportMode), value);
    }

    public string PortName
    {
        get => _component.PortName;
        set => _designer.SetProperty(_component, nameof(CodeBridgeEsp32Component.PortName), value);
    }

    public int BaudRate
    {
        get => _component.BaudRate;
        set => _designer.SetProperty(_component, nameof(CodeBridgeEsp32Component.BaudRate), value);
    }

    public string Host
    {
        get => _component.Host;
        set => _designer.SetProperty(_component, nameof(CodeBridgeEsp32Component.Host), value);
    }

    public int TcpPort
    {
        get => _component.TcpPort;
        set => _designer.SetProperty(_component, nameof(CodeBridgeEsp32Component.TcpPort), value);
    }

    public void DetectSerialPorts() => _designer.DetectSerialPorts();

    public void UseSerial() => _designer.SetTransportMode(CodeBridgeTransportMode.Serial);

    public void UseWiFi() => _designer.SetTransportMode(CodeBridgeTransportMode.WiFi);

    public override DesignerActionItemCollection GetSortedActionItems()
    {
        return
        [
            new DesignerActionHeaderItem("CodeBridge ESP32"),
            new DesignerActionMethodItem(this, nameof(DetectSerialPorts), "Detect Serial Ports...", "CodeBridge ESP32", "Find connected serial ports and select the first one.", includeAsDesignerVerb: true),
            new DesignerActionPropertyItem(nameof(TransportMode), "Transport", "CodeBridge ESP32", "Serial or WiFi connection mode."),
            new DesignerActionPropertyItem(nameof(PortName), "Port Name", "CodeBridge ESP32", "Serial port, for example COM3."),
            new DesignerActionPropertyItem(nameof(BaudRate), "Baud Rate", "CodeBridge ESP32", "Serial baud rate."),
            new DesignerActionPropertyItem(nameof(Host), "WiFi Host", "CodeBridge ESP32", "ESP32 IP address when using WiFi."),
            new DesignerActionPropertyItem(nameof(TcpPort), "TCP Port", "CodeBridge ESP32", "ESP32 TCP port when using WiFi."),
            new DesignerActionMethodItem(this, nameof(UseSerial), "Use Serial", "CodeBridge ESP32", "Configure the component for serial communication.", includeAsDesignerVerb: false),
            new DesignerActionMethodItem(this, nameof(UseWiFi), "Use WiFi", "CodeBridge ESP32", "Configure the component for WiFi communication.", includeAsDesignerVerb: false)
        ];
    }
}
