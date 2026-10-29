using System.Globalization;

namespace CodeBridge.IotBindings;

/// <summary>Small pieces the blocks share, public so the C# a flow is exported to can call them too.</summary>
public static class IotHelpers
{
    /// <summary>Bytes read from a chip as one number (1 to 4 bytes).</summary>
    public static double ToNumber(byte[] bytes, bool bigEndian, bool signed)
    {
        if (bytes.Length is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(bytes), "A register value is 1 to 4 bytes.");

        long value = 0;
        for (var i = 0; i < bytes.Length; i++)
            value = (value << 8) | bytes[bigEndian ? i : bytes.Length - 1 - i];

        if (signed && (value & (1L << (bytes.Length * 8 - 1))) != 0)
            value -= 1L << (bytes.Length * 8);

        return value;
    }

    /// <summary>
    /// The bytes a chip returned, or a clear error when it did not answer: the firmware gives an empty answer (not an error) for an address nobody
    /// listens to, which would otherwise show up as a confusing message about byte counts.
    /// </summary>
    public static byte[] Answered(byte[] bytes, int length, int address)
    {
        if (bytes.Length != length)
            throw new InvalidOperationException($"Nothing answered at I2C address 0x{address:X2} (asked for {length} byte(s), got {bytes.Length}). Check the wiring, the power and the address; an I2C Scan block lists the chips on the bus.");

        return bytes;
    }

    /// <summary>A number as 1 to 4 bytes, most significant first or last.</summary>
    public static byte[] ToBytes(double number, int length, bool bigEndian)
    {
        if (length is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(length), "A register value is 1 to 4 bytes.");

        var value = (long)Math.Round(number);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[bigEndian ? length - 1 - i : i] = (byte)(value >> (8 * i));

        return bytes;
    }

    /// <summary>A number as text that fits four digits (and a minus sign taking one): 23.456 is 23.46, 1234.5 is 1235, 12345 is ----.</summary>
    public static string Format(double value)
    {
        if (double.IsNaN(value) || Math.Abs(value) >= 9999.5)
            return "----";

        var digits = Math.Floor(Math.Abs(value)).ToString("0", CultureInfo.InvariantCulture).Length + (value < 0 ? 1 : 0);
        var decimals = Math.Clamp(4 - digits, 0, 3);
        return Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    /// <summary>The four cells of a 4-digit 7-segment display for a number (right-aligned, the decimal point lit on the digit before it).</summary>
    public static Iot.Device.Display.Segment[] Digits(double value)
    {
        var cells = new List<Iot.Device.Display.Segment>();
        foreach (var character in Format(value))
        {
            if (character == '.')
            {
                if (cells.Count > 0)
                    cells[^1] |= Iot.Device.Display.Segment.Dot;

                continue;
            }

            cells.Add(character switch
            {
                '-' => (Iot.Device.Display.Segment)(byte)Iot.Device.Display.Font.Symbol_Minus,
                >= '0' and <= '9' => (Iot.Device.Display.Segment)(byte)Enum.Parse<Iot.Device.Display.Font>("Digit_" + character),
                _ => Iot.Device.Display.Segment.None
            });
        }

        var result = new Iot.Device.Display.Segment[4];
        for (var i = 0; i < cells.Count && i < 4; i++)
            result[4 - Math.Min(cells.Count, 4) + i] = cells[i];

        return result;
    }

    /// <summary>The addresses a scan found, as <c>0x44, 0x76</c>.</summary>
    public static string Describe(IEnumerable<byte> addresses) =>
        string.Join(", ", addresses.Select(a => "0x" + a.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>The keys of a touch sensor as one number: bit 0 is key 0 (the dictionary keys are named <c>Channel00</c>, <c>Channel01</c>...).</summary>
    public static int TouchMask<TKey>(IEnumerable<KeyValuePair<TKey, bool>> channels) where TKey : notnull
    {
        var mask = 0;
        foreach (var (channel, touched) in channels)
        {
            var name = channel.ToString() ?? string.Empty;
            var digits = new string(name.Where(char.IsDigit).ToArray());
            if (touched && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index is >= 0 and < 31)
                mask |= 1 << index;
        }

        return mask;
    }
}
