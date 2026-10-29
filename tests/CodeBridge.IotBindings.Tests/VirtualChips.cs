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

    /// <param name="sht4x">The SHT4x reads humidity as -6 + 125 x raw / 65535 instead of 100 x raw / 65535.</param>
    public Sht3xChip(double celsius, double humidity, bool sht4x = false)
    {
        var t = (ushort)Math.Round((celsius + 45) / 175 * 65535);
        var h = (ushort)Math.Round((sht4x ? humidity + 6 : humidity) / (sht4x ? 125 : 100) * 65535);
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

/// <summary>LM75: register 0 is the temperature in the top 11 bits of two bytes (1/8 °C); the others are the configuration and the limits.</summary>
internal sealed class Lm75Chip : ISimulatedI2cDevice
{
    private readonly ushort _temperature;
    private byte _pointer;

    public Lm75Chip(double celsius) => _temperature = (ushort)(((int)Math.Round(celsius * 8) & 0x7FF) << 5);

    public void Write(byte[] data)
    {
        if (data.Length > 0)
            _pointer = data[0];
    }

    public byte[] Read(int length)
    {
        var word = _pointer == 0 ? _temperature : (ushort)0;
        return new[] { (byte)(word >> 8), (byte)word }.Take(length).ToArray();
    }
}

/// <summary>MAX44009: the light is an exponent and a mantissa split over registers 3 and 4: lux = 2^exponent x mantissa x 0.045.</summary>
internal sealed class Max44009Chip : RegisterChip
{
    public Max44009Chip(int exponent, int mantissa)
    {
        Registers[3] = (byte)(exponent << 4 | mantissa >> 4);
        Registers[4] = (byte)(mantissa & 0x0F);
    }
}

/// <summary>DS1307: BCD time in registers 0 to 6 (bit 7 of the seconds is the halt flag and stays clear).</summary>
internal sealed class Ds1307Chip : RegisterChip
{
    public Ds1307Chip(DateTime time)
    {
        Registers[0] = Bcd(time.Second);
        Registers[1] = Bcd(time.Minute);
        Registers[2] = Bcd(time.Hour);
        Registers[3] = Bcd((int)time.DayOfWeek + 1);
        Registers[4] = Bcd(time.Day);
        Registers[5] = Bcd(time.Month);
        Registers[6] = Bcd(time.Year % 100);
    }

    private static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);
}

/// <summary>PCF8563: BCD time in registers 2 to 8 (seconds, minutes, hours, days, weekdays, months with the century bit, years).</summary>
internal sealed class Pcf8563Chip : RegisterChip
{
    public Pcf8563Chip(DateTime time)
    {
        Registers[2] = Bcd(time.Second);
        Registers[3] = Bcd(time.Minute);
        Registers[4] = Bcd(time.Hour);
        Registers[5] = Bcd(time.Day);
        Registers[6] = Bcd((int)time.DayOfWeek);
        Registers[7] = (byte)(Bcd(time.Month) | (time.Year >= 2000 ? 0x80 : 0));
        Registers[8] = Bcd(time.Year % 100);
    }

    private static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);
}

/// <summary>PCF8574: one latch of 8 pins; what was written is what reads back (a pin written as 1 reads whatever the outside pulls it to).</summary>
internal sealed class Pcf8574Chip : ISimulatedI2cDevice
{
    public byte Latch { get; private set; } = 0xFF;

    public void Write(byte[] data)
    {
        if (data.Length > 0)
            Latch = data[^1];
    }

    public byte[] Read(int length) => Enumerable.Repeat(Latch, length).ToArray();
}

/// <summary>A chip that only listens (a display driver): everything written to it is kept.</summary>
internal sealed class RecordingChip : ISimulatedI2cDevice
{
    public List<byte[]> Writes { get; } = new();

    public void Write(byte[] data) => Writes.Add(data);

    public byte[] Read(int length) => new byte[length];
}

/// <summary>BMP280 with the calibration and raw readings of the example in its datasheet: 25.08 °C and 1006.53 hPa.</summary>
internal sealed class Bmp280Chip : RegisterChip
{
    public void SetRegister(int register, byte value) => Registers[register] = value;

    public Bmp280Chip()
    {
        short[] calibration = [27504, 26435, -1000, unchecked((short)36477), -10685, 3024, 2855, 140, -7, 15500, -14600, 6000];
        for (var i = 0; i < calibration.Length; i++)
        {
            Registers[0x88 + i * 2] = (byte)calibration[i];
            Registers[0x89 + i * 2] = (byte)(calibration[i] >> 8);
        }

        Registers[0xD0] = 0x58;                                           // chip id
        (Registers[0xF7], Registers[0xF8], Registers[0xF9]) = (0x65, 0x5A, 0xC0);   // pressure, raw 415148
        (Registers[0xFA], Registers[0xFB], Registers[0xFC]) = (0x7E, 0xED, 0x00);   // temperature, raw 519888
    }
}

/// <summary>
/// A BME280 with the calibration and the raw reading of a real chip (captured from a board on 2026-10-21): about 19.5 °C, 70 % and 716 hPa. The registers are
/// kept as the chip holds them, so the driver is tested against what a real BME280 sends and not against numbers made up to fit.
/// </summary>
internal sealed class Bme280Chip : RegisterChip
{
    public Bme280Chip()
    {
        Registers[0xD0] = 0x60;
        Put(0x88, "706E60673200C68E48D6D00B882377FFF9FFAC260AD8BD10");
        Put(0xA1, "4B");
        Put(0xE1, "650100142A031E");
        Put(0xF7, "7503007D63008599");
    }

    private void Put(int register, string hex) => Convert.FromHexString(hex).CopyTo(Registers, register);

    public byte[] Snapshot() => (byte[])Registers.Clone();
}

/// <summary>The compensation formulas of the BME280 datasheet (floating point version), applied to the registers of a chip: the answer the driver must agree with.</summary>
internal static class Bme280Reference
{
    public static (double Celsius, double Hectopascals, double Humidity) Compute(byte[] r)
    {
        ushort U16(int i) => (ushort)(r[i] | r[i + 1] << 8);
        short S16(int i) => (short)U16(i);
        double t1 = U16(0x88), t2 = S16(0x8A), t3 = S16(0x8C);
        double p1 = U16(0x8E), p2 = S16(0x90), p3 = S16(0x92), p4 = S16(0x94), p5 = S16(0x96), p6 = S16(0x98), p7 = S16(0x9A), p8 = S16(0x9C), p9 = S16(0x9E);
        double h1 = r[0xA1], h2 = S16(0xE1), h3 = r[0xE3];
        double h4 = (sbyte)r[0xE4] * 16 | r[0xE5] & 0x0F, h5 = (sbyte)r[0xE6] * 16 | r[0xE5] >> 4, h6 = (sbyte)r[0xE7];
        double adcP = r[0xF7] << 12 | r[0xF8] << 4 | r[0xF9] >> 4, adcT = r[0xFA] << 12 | r[0xFB] << 4 | r[0xFC] >> 4, adcH = r[0xFD] << 8 | r[0xFE];

        var v1 = (adcT / 16384.0 - t1 / 1024.0) * t2;
        var v2 = (adcT / 131072.0 - t1 / 8192.0) * (adcT / 131072.0 - t1 / 8192.0) * t3;
        var tFine = v1 + v2;

        v1 = tFine / 2.0 - 64000.0;
        v2 = v1 * v1 * p6 / 32768.0;
        v2 += v1 * p5 * 2.0;
        v2 = v2 / 4.0 + p4 * 65536.0;
        v1 = (p3 * v1 * v1 / 524288.0 + p2 * v1) / 524288.0;
        v1 = (1.0 + v1 / 32768.0) * p1;
        var p = (1048576.0 - adcP - v2 / 4096.0) * 6250.0 / v1;
        p += (p9 * p * p / 2147483648.0 + p * p8 / 32768.0 + p7) / 16.0;

        var h = tFine - 76800.0;
        h = (adcH - (h4 * 64.0 + h5 / 16384.0 * h)) * (h2 / 65536.0 * (1.0 + h6 / 67108864.0 * h * (1.0 + h3 / 67108864.0 * h)));
        h *= 1.0 - h1 * h / 524288.0;

        return (tFine / 5120.0, p / 100.0, Math.Clamp(h, 0, 100));
    }
}

/// <summary>A plain register file for the generic blocks.</summary>
internal sealed class PlainChip : RegisterChip
{
    public PlainChip(params (int Register, byte Value)[] values)
    {
        foreach (var (register, value) in values)
            Registers[register] = value;
    }

    public byte this[int register] => Registers[register];
}
