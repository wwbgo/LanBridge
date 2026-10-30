using LanBridge.Control;

namespace LanBridge.Host;

// Compatibility endpoint; desktop discovery calls HyperVQuery in the UI token directly.
public static class HyperVDiscovery
{
    public static Task<List<VirtualMachineInfo>> GetAsync(CancellationToken ct = default) => HyperVQuery.GetAsync(ct);
}
