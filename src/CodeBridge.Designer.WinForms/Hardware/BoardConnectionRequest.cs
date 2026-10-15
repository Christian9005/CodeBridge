using CodeBridge.Flow;

namespace CodeBridge.Designer.WinForms.Hardware;

internal sealed record BoardConnectionRequest(
    BoardProfile BoardProfile,
    CodeBridgeTransportMode TransportMode,
    string PortName,
    int BaudRate,
    string Host,
    int TcpPort,
    string? AccessToken = null,
    Action<string, string>? CommandObserver = null)
{
    /// <summary>A transport that is already built (for example a node of the ESP-NOW mesh). When set, the port and host are ignored.</summary>
    public CodeBridge.Core.Abstractions.ITransport? Transport { get; init; }
}
