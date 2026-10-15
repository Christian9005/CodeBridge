using CodeBridge.Core.Enums;
using CodeBridge.Core.Exceptions;
using CodeBridge.Core.Protocol;
using CodeBridge.Core.Tests.Mocks;

namespace CodeBridge.ESP32.Tests;

public class CodeBridgeProtocolBoardTests
{
    [Fact]
    public async Task ConnectAsync_WhenVersionMatches_ShouldConnect()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK:PONG");
        transport.EnqueueResponse($"OK:{BridgeProtocol.EXPECTED_FIRMWARE_VERSION}");
        
        var board = new CodeBridgeProtocolBoard(transport, "TestBoard", BoardFamily.ESP32);
        await board.ConnectAsync();
        
        Assert.Equal(BridgeProtocol.EXPECTED_FIRMWARE_VERSION, board.FirmwareVersion);
    }
    
    [Fact]
    public async Task ConnectAsync_WhenVersionMismatches_ShouldThrowProtocolMismatchException()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK:PONG");
        transport.EnqueueResponse("OK:0.1.0"); // Old version
        
        var board = new CodeBridgeProtocolBoard(transport, "TestBoard", BoardFamily.ESP32);
        
        var ex = await Assert.ThrowsAsync<ProtocolMismatchException>(() => board.ConnectAsync());
        Assert.Equal(BridgeProtocol.EXPECTED_FIRMWARE_VERSION, ex.ExpectedVersion);
        Assert.Equal("0.1.0", ex.ActualVersion);
    }
    
    [Fact]
    public async Task ConnectAsync_WhenPingFails_ShouldThrowInvalidOperationException()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("ERR:No PONG");
        
        var board = new CodeBridgeProtocolBoard(transport, "TestBoard", BoardFamily.ESP32);
        
        await Assert.ThrowsAsync<InvalidOperationException>(() => board.ConnectAsync());
    }
}
