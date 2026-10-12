using CodeBridge.Core.Enums;
using CodeBridge.Core.Tests.Mocks;
using CodeBridge.ESP32;

namespace CodeBridge.ESP32.Tests;

public class ESP32GpioControllerTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(40)]
    [InlineData(100)]
    public async Task SetPinModeAsync_InvalidPin_ShouldThrowArgumentOutOfRangeException(int invalidPin)
    {
        var transport = new MockTransport();
        var controller = new ESP32GpioController(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.SetPinModeAsync(invalidPin, PinMode.Output));
    }

    [Fact]
    public async Task PwmWriteAsync_InvalidDutyCycle_ShouldThrowArgumentOutOfRangeException()
    {
        var transport = new MockTransport();
        var controller = new ESP32GpioController(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.PwmWriteAsync(2, 256));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.PwmWriteAsync(2, -1));
    }
}
