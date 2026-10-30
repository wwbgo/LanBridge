using System.Text.Json;
using LanBridge.Control;
using LanBridge.Core;

namespace LanBridge.Host;

/// <summary>Host-owned, bounded JSON persistence. The host lease and coordinator serialize all access.</summary>
internal sealed class HostRuleRepository(string path)
{
    private const int MaximumFileBytes = 1024 * 1024;

    public async Task<IReadOnlyList<ForwardRule>> LoadAsync(CancellationToken ct = default)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return []; }
        // Access denied, a directory at the file path, or a missing data directory must fail visibly,
        // not masquerade as an empty store (File.Exists would suppress those errors).
        using var ownedStream = stream;
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("规则配置超过 1 MiB 上限；未覆盖配置。");
        // Read at most the recorded length; FileShare.Read denies writers for this bounded read.
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct);
        try
        {
            var result = JsonSerializer.Deserialize<List<ForwardRule>>(bytes, PipeProtocol.Json);
            if (result is null || result.Any(r => r is null)) throw new InvalidDataException("规则配置为空或包含空规则；未覆盖配置。");
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("规则配置 JSON 损坏；请修复 rules.json，未重置或覆盖。", ex); }
    }

    public async Task SaveAsync(IEnumerable<ForwardRule> rules)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(rules, PipeProtocol.Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("规则配置超过 1 MiB 上限。");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            // The temp file is on the same volume/directory. Replacement is atomic, never truncate the old file.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
