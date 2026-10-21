using System.Device.I2c;
using CodeBridge.Core.Abstractions;

namespace CodeBridge.IotBindings;

/// <summary>
/// A <see cref="I2cDevice"/> (System.Device.I2c) whose bus is the I2C controller of a CodeBridge board. Every driver of the .NET IoT bindings that takes an
/// <c>I2cDevice</c> (more than a hundred: temperature, light, distance, converters, expanders, displays, clocks...) works on a board over USB or Wi-Fi
/// without touching the firmware: each read and write travels as one <c>IW</c>, <c>IR</c>, <c>IWR</c> or <c>IRR</c> command.
/// </summary>
/// <remarks>
/// The .NET IoT API is synchronous and the bridge is asynchronous, so each call waits for its command on a pool thread (never on the caller's
/// synchronization context). A transfer takes a few milliseconds; drivers that talk a lot when they start (a laser distance sensor) are best kept alive.
/// </remarks>
public sealed class BridgeI2cDevice : I2cDevice
{
    private readonly II2cController _bus;
    private readonly byte _address;

    public BridgeI2cDevice(II2cController bus, int address)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (address is < 0 or > 0x7F)
            throw new ArgumentOutOfRangeException(nameof(address), address, "An I2C address is 7 bits (0 to 0x7F).");

        _bus = bus;
        _address = (byte)address;
    }

    public override I2cConnectionSettings ConnectionSettings => new(busId: 0, deviceAddress: _address);

    public override byte ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        Read(one);
        return one[0];
    }

    public override void Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return;

        var length = buffer.Length;
        var data = Wait(() => _bus.ReadAsync(_address, length));
        if (data.Length != length)
            throw new IOException($"I2C 0x{_address:X2}: asked for {length} byte(s), the board returned {data.Length}.");

        data.CopyTo(buffer);
    }

    public override void WriteByte(byte value) => Write([value]);

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var data = buffer.ToArray();
        Wait(() => _bus.WriteAsync(_address, data));
    }

    public override void WriteRead(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
    {
        if (writeBuffer.Length == 1 && !readBuffer.IsEmpty)
        {
            // the common "register, then read" transaction: one command, with the repeated start done by the firmware
            var register = writeBuffer[0];
            var length = readBuffer.Length;
            var data = Wait(() => _bus.ReadRegisterAsync(_address, register, length));
            if (data.Length != length)
                throw new IOException($"I2C 0x{_address:X2}: asked for {length} byte(s) of register 0x{register:X2}, the board returned {data.Length}.");

            data.CopyTo(readBuffer);
            return;
        }

        Write(writeBuffer);
        Read(readBuffer);
    }

    private static void Wait(Func<Task> action) => Task.Run(action).GetAwaiter().GetResult();

    private static T Wait<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();
}
