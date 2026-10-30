using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanBridge.Core;

namespace LanBridge.Control;

public sealed record BridgeState(List<ForwardRule> Rules, List<RuleStatus> Statuses, string Mode, string LogPath,
    int ProcessId = 0, string ProcessPath = "", long ProcessStartTimeUtcTicks = 0);
public sealed record VirtualMachineInfo(string Id, string Name, string State, string[] Addresses);
public sealed record BridgeRequest(string Command, ForwardRule? Rule = null, Guid? Id = null, bool? Enabled = null);
public sealed record BridgeResponse(bool Success, string? Error = null, BridgeState? State = null,
    string? Probe = null, List<VirtualMachineInfo>? VirtualMachines = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ListenCheckResult? ListenCheck = null);

public static class PipeProtocol
{
    public const int MaximumPayloadBytes = 256 * 1024;
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(12);
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string CurrentOwnerSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法确定当前 Windows 用户 SID。");
    public static string PipeName(string ownerSid)
    {
        var sid = new SecurityIdentifier(ownerSid);
        if (!sid.IsAccountSid()) throw new ArgumentException("必须提供用户 SID。", nameof(ownerSid));
        return "LanBridge." + sid.Value;
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumPayloadBytes) throw new InvalidDataException("管道消息长度无效。");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("管道消息为空。");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length > MaximumPayloadBytes) throw new InvalidDataException("管道消息过大。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
}
