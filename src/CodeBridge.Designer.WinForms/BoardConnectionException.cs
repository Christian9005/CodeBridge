using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;

namespace CodeBridge.Designer.WinForms;

/// <summary>
/// A connection that did not work, with a message written for the person using the app rather than for a developer.
/// <see cref="FirmwareMissing"/> tells the UI it can offer to upload the CodeBridge firmware.
/// </summary>
public sealed class BoardConnectionException : InvalidOperationException
{
    public BoardConnectionException(string message, bool firmwareMissing = false, string? portName = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FirmwareMissing = firmwareMissing;
        PortName = portName;
    }

    /// <summary>The board answered on its port but is not running the CodeBridge firmware (yet).</summary>
    public bool FirmwareMissing { get; }

    /// <summary>The port that was being used, when there was one.</summary>
    public string? PortName { get; }

    internal static BoardConnectionException From(Exception exception, BoardProfile profile, string? portName) =>
        new(
            BoardConnectionService.FormatConnectionFailure(exception, profile, portName),
            BoardConnectionService.IsFirmwareMissing(exception),
            portName,
            exception);
}
