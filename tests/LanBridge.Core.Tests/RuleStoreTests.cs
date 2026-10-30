using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public sealed class RuleStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch",
        "LanBridgeCoreTests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(directory, "rules.json");

    public RuleStoreTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Save_atomically_replaces_snapshot_while_existing_reader_keeps_original_bytes()
    {
        var store = new RuleStore(StorePath);
        var original = ValidationTests.Valid with { Name = "old" };
        var replacement = original with { Name = new string('x', 256) };
        await store.SaveAsync(new[] { original });
        byte[] originalBytes = await File.ReadAllBytesAsync(StorePath);
        // Deny in-place writes, but permit atomic replacement of the directory entry.
        using var reader = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        await store.SaveAsync(new[] { replacement });
        Assert.Equal(replacement, Assert.Single(await new RuleStore(StorePath).LoadAsync()));
        using var oldSnapshot = new MemoryStream();
        await reader.CopyToAsync(oldSnapshot);
        Assert.Equal(originalBytes, oldSnapshot.ToArray());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Failed_replace_or_canceled_save_preserves_original_and_cleans_temporary_files()
    {
        var store = new RuleStore(StorePath);
        var original = ValidationTests.Valid;
        await store.SaveAsync(new[] { original });
        byte[] originalBytes = await File.ReadAllBytesAsync(StorePath);
        using (var blocker = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.SaveAsync(new[] { original with { Name = "not committed" } }));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a replacement failure.");
        }
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(StorePath));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new[] { original with { Name = "canceled" } }, canceled.Token));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(StorePath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"unexpected\":true}]")]
    [InlineData("[{}]")]
    public async Task Corrupt_or_invalid_configuration_fails_visibly_without_rewriting(string contents)
    {
        await File.WriteAllTextAsync(StorePath, contents);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RuleStore(StorePath).LoadAsync());
        Assert.Equal(contents, await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task Invalid_duplicate_or_too_many_rules_never_replace_original_snapshot()
    {
        var store = new RuleStore(StorePath);
        var original = ValidationTests.Valid;
        await store.SaveAsync(new[] { original });
        byte[] before = await File.ReadAllBytesAsync(StorePath);
        ForwardRule[][] invalid = [
            [original with { ListenPort = 0 }], [original, original], [null!],
            Enumerable.Range(0, ForwardingEngine.MaximumRules + 1).Select(_ => original with { Id = Guid.NewGuid() }).ToArray()
        ];
        foreach (var snapshot in invalid)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(snapshot));
            Assert.Equal(before, await File.ReadAllBytesAsync(StorePath));
        }
    }

    [Fact]
    public async Task Oversized_input_is_rejected_before_deserialization_without_rewriting()
    {
        byte[] bytes = Enumerable.Repeat((byte)' ', 1024 * 1024 + 1).ToArray();
        await File.WriteAllBytesAsync(StorePath, bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new RuleStore(StorePath).LoadAsync());
        Assert.Contains("1 MiB", error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(StorePath));
    }

    [Fact]
    public async Task Concurrent_readers_observe_only_complete_snapshots_during_concurrent_saves()
    {
        var store = new RuleStore(StorePath);
        var rules = Enumerable.Range(0, 32).Select(_ => ValidationTests.Valid).ToArray();
        await store.SaveAsync(rules);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task ReadSnapshots()
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                var snapshot = await new RuleStore(StorePath).LoadAsync(timeout.Token);
                Assert.Equal(rules.Length, snapshot.Count);
                Assert.Single(snapshot.Select(r => r.Name).Distinct());
            }
        }
        async Task WriteSnapshots(string prefix)
        {
            for (int iteration = 0; iteration < 20; iteration++)
                await store.SaveAsync(rules.Select(r => r with { Name = prefix + iteration }), timeout.Token);
        }
        await Task.WhenAll(Task.Run(ReadSnapshots), Task.Run(ReadSnapshots), Task.Run(() => WriteSnapshots("A")), Task.Run(() => WriteSnapshots("B")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task Save_roundtrips_exact_fields_and_empty_snapshot_across_new_instances()
    {
        var store = new RuleStore(StorePath);
        Assert.Empty(await store.LoadAsync());
        var rule = ValidationTests.Valid with { Name = "虚拟机 UDP", Protocol = TransportProtocol.Udp, Enabled = false };
        await store.SaveAsync(new[] { rule });
        Assert.Equal(rule, Assert.Single(await new RuleStore(StorePath).LoadAsync()));
        await store.SaveAsync(Array.Empty<ForwardRule>());
        Assert.Empty(await new RuleStore(StorePath).LoadAsync());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
}
