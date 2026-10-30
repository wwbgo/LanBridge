using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanBridge.Core;

/// <summary>JSON snapshots of forwarding rules at a caller-owned configuration path.</summary>
public sealed class RuleStore
{
    public const int MaximumFileBytes = 1024 * 1024;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new TransportProtocolJsonConverter() }
    };
    private readonly string path;

    public RuleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<IReadOnlyList<ForwardRule>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return []; }
        await using var ownedStream = stream;
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("规则配置超过 1 MiB 上限；未修改配置。");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = JsonSerializer.Deserialize<List<ForwardRule>>(bytes, Json)
                ?? throw new InvalidDataException("规则配置不能为空。");
            ValidateSnapshot(rules);
            return rules;
        }
        catch (JsonException ex) { throw new InvalidDataException("规则配置 JSON 损坏；未重置或覆盖配置。", ex); }
    }

    public async Task SaveAsync(IEnumerable<ForwardRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = rules.Take(ForwardingEngine.MaximumRules + 1).ToArray();
        ValidateSnapshot(snapshot);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("规则配置超过 1 MiB 上限。");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Same directory/volume: publish a complete snapshot without ever truncating the old file.
            AtomicFile.Publish(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { writeGate.Release(); }
        }
    }

    private static void ValidateSnapshot(IReadOnlyList<ForwardRule> rules)
    {
        if (rules.Count > ForwardingEngine.MaximumRules) throw new InvalidDataException("规则数量超过上限。");
        var ids = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            if (rule is null) throw new InvalidDataException("规则配置包含空规则。");
            var errors = RuleValidator.Validate(rule);
            if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
            if (!ids.Add(rule.Id)) throw new InvalidDataException("规则配置包含重复 ID。");
        }
    }

    private sealed class TransportProtocolJsonConverter : JsonConverter<TransportProtocol>
    {
        public override TransportProtocol Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("传输协议必须是字符串。");
            return reader.GetString() switch
            {
                "Tcp" => TransportProtocol.Tcp,
                "Udp" => TransportProtocol.Udp,
                "tcpUdp" => TransportProtocol.TcpUdp,
                _ => throw new JsonException("不支持的传输协议。")
            };
        }

        public override void Write(Utf8JsonWriter writer, TransportProtocol value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                TransportProtocol.Tcp => "Tcp",
                TransportProtocol.Udp => "Udp",
                TransportProtocol.TcpUdp => "tcpUdp",
                _ => throw new JsonException("不支持的传输协议。")
            });
        }
    }
}
