using System.Buffers.Binary;
using LanBridge.Control;
using Xunit;

namespace LanBridge.Host.Tests;

public class ControlProtocolTests
{
    [Fact]
    public async Task Client_roundtrips_real_pipe_and_surfaces_remote_error()
    {
        string name = "LanBridge.Test." + Guid.NewGuid().ToString("N");
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var request = await PipeProtocol.ReadAsync<BridgeRequest>(server);
            Assert.Equal("getState", request.Command);
            await PipeProtocol.WriteAsync(server, new BridgeResponse(false, "测试错误"));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new BridgeClient(name).GetStateAsync());
        Assert.Equal("测试错误", error.Message);
        await serving;
    }

    [Fact]
    public async Task Framing_rejects_oversized_outgoing_message_without_writing_a_header()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.WriteAsync(stream,
            new BridgeResponse(false, new string('x', PipeProtocol.MaximumPayloadBytes))));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task Client_deadline_bounds_a_connected_but_silent_server()
    {
        string name = "LanBridge.Test." + Guid.NewGuid().ToString("N");
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var serving = server.WaitForConnectionAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => new BridgeClient(name, TimeSpan.FromMilliseconds(150)).GetStateAsync());
        await serving.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Framing_rejects_oversized_length_before_reading_payload()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, PipeProtocol.MaximumPayloadBytes + 1);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync<BridgeRequest>(stream));
    }
}
