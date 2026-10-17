using CodeBridge.Transport;
using Xunit;

namespace CodeBridge.Core.Tests.Transport;

public class BoardDiscoveryTests
{
    private static PortInfo Legacy(string name) => new(name) { PnpDeviceId = @"ACPI\PNP0501\1", Description = "Communications Port" };

    private static PortInfo Usb(string name, string vid = "0403", string pid = "6001") =>
        new(name) { PnpDeviceId = $@"FTDIBUS\VID_{vid}+PID_{pid}+A\0000", VendorId = vid, ProductId = pid };

    private static PortInfo Board(string name) =>
        new(name)
        {
            PnpDeviceId = @"USB\VID_10C4&PID_EA60\0001",
            VendorId = "10C4",
            ProductId = "EA60",
            BoardHint = "Silicon Labs CP210x (ESP32 DevKit)"
        };

    [Fact]
    public void RankCandidates_PutsARecognisedBoardBeforeOtherUsbAdapters()
    {
        var ranked = BoardDiscovery.RankCandidates(new[] { Usb("COM3"), Board("COM7"), Usb("COM4") });

        Assert.Equal(new[] { "COM7", "COM3", "COM4" }, ranked.Select(p => p.Name));
    }

    [Fact]
    public void RankCandidates_LeavesOutTheBuiltInPortOfADesktop()
    {
        var ranked = BoardDiscovery.RankCandidates(new[] { Legacy("COM1"), Board("COM5") });

        Assert.Equal(new[] { "COM5" }, ranked.Select(p => p.Name));
    }

    [Fact]
    public void RankCandidates_WithOnlyBuiltInOrBluetoothPorts_FindsNothing()
    {
        var bluetooth = new PortInfo("COM9") { PnpDeviceId = @"BTHENUM\DEV_001122334455\7&1" };

        var ranked = BoardDiscovery.RankCandidates(new[] { Legacy("COM1"), Legacy("COM2"), bluetooth });

        Assert.Empty(ranked);
    }

    [Fact]
    public void RankCandidates_WhenThePortsCouldNotBeDescribed_KeepsEverythingButCom1()
    {
        var ranked = BoardDiscovery.RankCandidates(new[] { new PortInfo("COM1"), new PortInfo("COM6"), new PortInfo("/dev/ttyUSB0") });

        Assert.Equal(new[] { "/dev/ttyUSB0", "COM6" }, ranked.Select(p => p.Name));
    }

    [Theory]
    [InlineData(@"USB\VID_303A&PID_1001\ABC", null, true)]
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A\0000", null, true)]
    [InlineData(@"BTHENUM\DEV_001122334455\7&1", null, false)]
    [InlineData(@"ACPI\PNP0501\1", null, false)]
    [InlineData(null, "10C4", true)]
    [InlineData(null, null, false)]
    public void IsUsb_FollowsTheDeviceId(string? pnpId, string? vendorId, bool expected)
    {
        var port = new PortInfo("COM3") { PnpDeviceId = pnpId, VendorId = vendorId };

        Assert.Equal(expected, port.IsUsb);
    }
}
