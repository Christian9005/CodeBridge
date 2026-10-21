using CodeBridge.Transport.Simulation;

namespace CodeBridge.IotBindings.Tests;

/// <summary>A chip with a register file and a register pointer that moves on after every byte (what most I2C chips do).</summary>
internal class RegisterChip : ISimulatedI2cDevice
{
    protected readonly byte[] Registers = new byte[256];
    protected int Pointer;

    public virtual void Write(byte[] data)
    {
        if (data.Length == 0)
            return;

        Pointer = data[0];
        for (var i = 1; i < data.Length; i++)
            Registers[Pointer++ & 0xFF] = data[i];
    }

    public virtual byte[] Read(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = Registers[Pointer++ & 0xFF];

        return bytes;
    }
}

/// <summary>MCP9808: 16-bit registers selected by a pointer: 5 the ambient temperature (1/16 °C), 6 and 7 the manufacturer and device ids the driver checks.</summary>
internal sealed class Mcp9808Chip : ISimulatedI2cDevice
{
    private readonly Dictionary<byte, ushort> _words = new();
    private byte _pointer;

    public Mcp9808Chip(double celsius)
    {
        _words[5] = (ushort)((int)Math.Round(celsius * 16) & 0x1FFF);
        _words[6] = 0x0054;
        _words[7] = 0x0400;
        _words[8] = 0x0003;
    }

    public void Write(byte[] data)
    {
        if (data.Length > 0)
            _pointer = data[0];
    }

    public byte[] Read(int length)
    {
        var word = _words.GetValueOrDefault(_pointer);
        return new[] { (byte)(word >> 8), (byte)word }.Take(length).ToArray();
    }
}

/// <summary>DS3231: BCD time in registers 0 to 6, temperature in 0x11 and 0x12.</summary>
internal sealed class Ds3231Chip : RegisterChip
{
    public Ds3231Chip(DateTime time, double celsius)
    {
        Registers[0] = Bcd(time.Second);
        Registers[1] = Bcd(time.Minute);
        Registers[2] = Bcd(time.Hour);
        Registers[3] = Bcd((int)time.DayOfWeek + 1);
        Registers[4] = Bcd(time.Day);
        Registers[5] = (byte)(Bcd(time.Month) | (time.Year >= 2000 ? 0x80 : 0));   // bit 7 = century
        Registers[6] = Bcd(time.Year % 100);
        Registers[0x11] = (byte)Math.Floor(celsius);
        Registers[0x12] = (byte)((int)((celsius - Math.Floor(celsius)) * 4) << 6);
    }

    private static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);
}

/// <summary>SHT3x: a two-byte command starts a measurement, then six bytes come back (temperature, CRC, humidity, CRC).</summary>
internal sealed class Sht3xChip : ISimulatedI2cDevice
{
    private readonly byte[] _answer;

    public Sht3xChip(double celsius, double humidity)
    {
        var t = (ushort)Math.Round((celsius + 45) / 175 * 65535);
        var h = (ushort)Math.Round(humidity / 100 * 65535);
        _answer = [(byte)(t >> 8), (byte)t, Crc(t), (byte)(h >> 8), (byte)h, Crc(h)];
    }

    public void Write(byte[] data)
    {
    }

    public byte[] Read(int length) => _answer.Take(length).ToArray();

    private static byte Crc(ushort word)
    {
        byte crc = 0xFF;
        foreach (var b in new[] { (byte)(word >> 8), (byte)word })
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? (byte)(crc << 1 ^ 0x31) : (byte)(crc << 1);
        }

        return crc;
    }
}

/// <summary>ADS1115: register 1 is the configuration (the converter is always ready), register 0 the result: 8192 per channel step.</summary>
internal sealed class Ads1115Chip : ISimulatedI2cDevice
{
    private int _pointer;
    private ushort _config = 0x8583;

    public void Write(byte[] data)
    {
        if (data.Length == 0)
            return;

        _pointer = data[0] & 3;
        if (_pointer == 1 && data.Length >= 3)
            _config = (ushort)(data[1] << 8 | data[2]);
    }

    public byte[] Read(int length)
    {
        ushort value = _pointer switch
        {
            1 => (ushort)(_config | 0x8000),
            0 => (ushort)(8192 * ((_config >> 12 & 7) - 3)),   // multiplexer 4..7 = AIN0..AIN3
            _ => 0
        };

        return new[] { (byte)(value >> 8), (byte)value }.Take(length).ToArray();
    }
}

/// <summary>PCA9685: a register file; the test reads back what the driver wrote to the LED registers.</summary>
internal sealed class Pca9685Chip : RegisterChip
{
    public int OffCount(int channel) => Registers[0x08 + 4 * channel] | Registers[0x09 + 4 * channel] << 8;
}

/// <summary>MLX90614: SMBus reads of the RAM temperatures (0.02 K per step) with the packet error code the driver checks.</summary>
internal sealed class Mlx90614Chip : ISimulatedI2cDevice
{
    private readonly byte _address;
    private readonly ushort _ambient;
    private readonly ushort _object;
    private byte _command;

    public Mlx90614Chip(byte address, double ambientCelsius, double objectCelsius)
    {
        _address = address;
        _ambient = Raw(ambientCelsius);
        _object = Raw(objectCelsius);
    }

    public void Write(byte[] data)
    {
        if (data.Length > 0)
            _command = data[0];
    }

    public byte[] Read(int length)
    {
        var word = (_command & 0x1F) == 0x06 ? _ambient : _object;
        var lo = (byte)word;
        var hi = (byte)(word >> 8);
        var pec = Crc8([(byte)(_address << 1), _command, (byte)(_address << 1 | 1), lo, hi]);
        return new[] { lo, hi, pec }.Take(length).ToArray();
    }

    private static ushort Raw(double celsius) => (ushort)Math.Round((celsius + 273.15) / 0.02);

    private static byte Crc8(byte[] bytes)
    {
        byte crc = 0;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? (byte)(crc << 1 ^ 0x07) : (byte)(crc << 1);
        }

        return crc;
    }
}
