using System.Text.RegularExpressions;

namespace CodeBridge.Flow;

/// <summary>
/// Tells which board profile a connected board most likely is: from the chip the firmware reports about itself (exact), or from
/// the USB identity of its port (only a guess: a CP210x bridge is on many ESP32 boards, a CH340 on boards of every kind).
/// </summary>
public static class BoardIdentification
{
    private static readonly Regex Esp32Chip = new(@"ESP32(?<variant>[SCHP]\d)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UsbIds = new(@"VID_(?<vid>[0-9A-F]{4}).{0,3}PID_(?<pid>[0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// From the chip name of the firmware's INFO answer ("ESP32-D0WDQ6", "ESP32-S3 (rev 0.1)", "ESP32-C3") or of an Arduino ("ATmega328P").
    /// Null when it is a chip CodeBridge has no profile for (an ESP32-C6, for example), so a caller never takes a guess for the answer.
    /// </summary>
    public static BoardProfile? FromChip(string? chip)
    {
        if (string.IsNullOrWhiteSpace(chip))
            return null;

        var text = chip.ToUpperInvariant().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        if (text.Contains("ATMEGA2560"))
            return BuiltInBoardProfiles.ArduinoMega;
        if (text.Contains("ATMEGA328"))
            return BuiltInBoardProfiles.ArduinoUno;

        var esp32 = Esp32Chip.Match(text);
        if (!esp32.Success)
            return null;

        return esp32.Groups["variant"].Value switch
        {
            "" => BuiltInBoardProfiles.Esp32DevKit,
            "S3" => BuiltInBoardProfiles.Esp32S3DevKit,
            "S2" => BuiltInBoardProfiles.Esp32S2Mini,
            "C3" => BuiltInBoardProfiles.Esp32C3DevKit,
            _ => null
        };
    }

    /// <summary>
    /// From what Windows says about the port: the USB vendor and product ids, and any text that describes it (its friendly name,
    /// or the device id that carries the ids). Arduino boards are told apart by their product id; the bridge chips that ESP32 boards
    /// carry give the ESP32 DevKit, whose exact variant the board tells once it is connected (<see cref="FromChip"/>).
    /// Null for a bridge that does not say what is behind it (CH340) or a port that is not a board.
    /// </summary>
    public static BoardProfile? FromUsb(string? vendorId, string? productId, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(productId))
        {
            var ids = text is null ? null : UsbIds.Match(text);
            if (ids is { Success: true })
            {
                vendorId = ids.Groups["vid"].Value;
                productId = ids.Groups["pid"].Value;
            }
        }

        var vendor = vendorId?.Trim().ToUpperInvariant();
        var product = productId?.Trim().ToUpperInvariant();

        if (vendor is "2341" or "2A03")
        {
            return product is "0010" or "0042" or "0210" or "0242"
                ? BuiltInBoardProfiles.ArduinoMega
                : BuiltInBoardProfiles.ArduinoUno;
        }

        if (vendor is "10C4" or "303A" || (vendor == "1A86" && product == "55D4"))
            return BuiltInBoardProfiles.Esp32DevKit;

        if (text is null)
            return null;

        if (Contains(text, "arduino"))
            return Contains(text, "mega") ? BuiltInBoardProfiles.ArduinoMega : BuiltInBoardProfiles.ArduinoUno;

        if (Contains(text, "esp32") || Contains(text, "usb jtag") || Contains(text, "silicon labs") || Contains(text, "cp210"))
            return BuiltInBoardProfiles.Esp32DevKit;

        return null;
    }

    /// <summary>
    /// False when a flow made for one board would be run on a board that cannot take it: another family (an Arduino for an ESP32 flow)
    /// or another ESP32 chip (the pins differ). Boards that only differ in their shape (an S3 DevKit and a XIAO S3) are compatible.
    /// </summary>
    public static bool IsCompatible(BoardProfile flowBoard, BoardProfile connectedBoard)
    {
        if (flowBoard.Family != connectedBoard.Family)
            return false;

        // An Uno cannot take the pins of a flow made for a Mega.
        if (string.IsNullOrWhiteSpace(flowBoard.FlashChip) || string.IsNullOrWhiteSpace(connectedBoard.FlashChip))
            return connectedBoard.MaxPin >= flowBoard.MaxPin;

        return string.Equals(flowBoard.FlashChip, connectedBoard.FlashChip, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string value, string filter) =>
        value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
}
