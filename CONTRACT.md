# LanBridge implementation contract

Windows-only C#/.NET 10 + WinUI 3 desktop port-forwarding application, Chinese primary UX. Local application-layer TCP/UDP forwarding from the Windows host to a reachable VM; no drivers, no SSH, no outbound cloud connection. This is a production-minded small MVP, not a mockup. Do not install a service or change system firewall automatically during development.

## Ownership
- Core agent: src/LanBridge.Core/** and tests/LanBridge.Core.Tests/**.
- Host agent: src/LanBridge.Control/**, src/LanBridge.Host/**, tests/LanBridge.Host.Tests/**, scripts/Install-Service.ps1 and scripts/Uninstall-Service.ps1.
- UI agent: src/LanBridge.App/**, tests/LanBridge.App.Tests/** if needed.
- Parent: solution/global props/build packaging scripts/docs/integration verification. Do not edit another agent's owned files. Shared working tree, no commits required.

## Core public API (namespace LanBridge.Core, target net10.0)
```csharp
public enum TransportProtocol { Tcp, Udp }
public sealed record ForwardRule {
 public Guid Id {get;init;} = Guid.NewGuid();
 public string Name {get;init;} = "";
 public string ListenAddress {get;init;} = "127.0.0.1";
 public int ListenPort {get;init;}
 public string TargetHost {get;init;} = "";
 public int TargetPort {get;init;}
 public TransportProtocol Protocol {get;init;} = TransportProtocol.Tcp;
 public bool Enabled {get;init;} = true;
}
public sealed record RuleStatus(Guid Id, bool Running, string? Error, int ActiveConnections, long BytesSent, long BytesReceived);
public static class RuleValidator { public static IReadOnlyList<string> Validate(ForwardRule rule); }
public sealed class ForwardingEngine : IAsyncDisposable {
 public ForwardingEngine();
 public Task StartAsync(ForwardRule rule, CancellationToken cancellationToken=default);
 public Task StopAsync(Guid id);
 public IReadOnlyList<RuleStatus> GetStatuses();
 public ValueTask DisposeAsync();
}
public sealed class RuleStore {
 public RuleStore(string path);
 public Task<IReadOnlyList<ForwardRule>> LoadAsync(CancellationToken cancellationToken=default);
 public Task SaveAsync(IEnumerable<ForwardRule> rules, CancellationToken cancellationToken=default);
}
```
Engine StartAsync validates, throws a meaningful exception on bind failure, replaces an existing rule's runtime safely; Stop idempotent. Statuses include running entries and meaningful asynchronous runtime errors. Byte counts are cumulative per runtime; BytesSent means local client -> target. No duplicate socket binding allowed. TCP must support half-close correctly. UDP must isolate clients, expire idle sessions, bound sessions; rule stop must release all ports and clients. No unbounded logs/tasks/connections. RuleStore atomic writes, safe concurrent writes, no silent reset on corrupt config.

## Control public API (namespace LanBridge.Control, target net10.0-windows)
Control project references Core. Shared DTOs and the client live here; Host references it; App references it. These method signatures MUST match.
```csharp
public sealed record BridgeState(List<ForwardRule> Rules, List<RuleStatus> Statuses, string Mode, string LogPath);
public sealed record VirtualMachineInfo(string Id, string Name, string State, string[] Addresses);
public sealed class BridgeClient {
 public BridgeClient();
 public Task<BridgeState> GetStateAsync(CancellationToken ct=default);
 public Task SaveRuleAsync(ForwardRule rule,CancellationToken ct=default);
 public Task DeleteRuleAsync(Guid id,CancellationToken ct=default);
 public Task SetEnabledAsync(Guid id,bool enabled,CancellationToken ct=default);
 public Task<string> ProbeAsync(ForwardRule rule,CancellationToken ct=default);
 public Task<IReadOnlyList<VirtualMachineInfo>> GetVirtualMachinesAsync(CancellationToken ct=default);
}
```
Named-pipe request/reply JSON, max payload and bounded connect/read timeout, robust errors; no HTTP port opened for management. The client default pipe uses current Windows SID. User-mode host runs under that SID; service can use --owner-sid to grant exact owner+SYSTEM+admins, never Everyone. Host supports --console (user-owned persistent process with config at LocalAppData/LanBridge), and Windows service mode (--service --owner-sid SID, config ProgramData/LanBridge). Host must avoid duplicate processes per pipe using an exclusive lease. UI must be able to start the sibling LanBridge.Host.exe --console on explicit startup if missing and retry; closing UI does NOT kill host. Persistence on every CRUD/toggle, restore enabled rules at host startup, one bad rule does not prevent others. Corrupt config should fail visibly, never overwrite it. File logging bounded, no credentials. Hyper-V list through structured PowerShell output (timeout, no user-supplied script injection); unsupported/access-denied clearly returned, manual IP remains supported. Firewall is NOT automatically modified; provide an explicit separate admin action or documented generated commands limited to local subnet/private profile.

## GUI
Real WinUI 3 (Microsoft.WindowsAppSDK 2.6.4 confirmed in NuGet; .NET 10 SDK and Visual Studio 2026 are installed). Unpackaged x64 desktop app, prefer self-contained Windows App SDK deployment. App project target net10.0-windows10.0.19041.0, TargetPlatformMinVersion 10.0.17763.0, WindowsPackageType None; Windows SDK buildtools via NuGet if needed. Directory.Build.props parent supplies nullable/implicit usings only. Native Fluent controls, no web-based replacement. Chinese copy, quiet professional Windows-native look, clear list of rules and a single editor dialog. Name, protocol, host listen address/port, target address/port, enabled. Add/edit/delete with confirmation, start/stop, copy LAN endpoint, test target reachability (UDP must not pretend TCP proves UDP). Refresh statuses regularly without overlapping calls. Show disconnected/permission errors, no fake status/seed rules. Display that listening and target reachability are different. Allow optional VM discovery, gracefully fall back to manual target. Host launcher uses path relative to app; launch shell=false hidden console, no privilege escalation unless explicitly requested. Dark/light use OS theme. Accessibility labels and tab order. No unnecessary dashboard charts.

## Quality gates
Strict vertical TDD for core and control behavior: write one failing test, run/record red, implement, run green, expand. All implementers report actual build/test command outputs, absolute paths, and unverified limitations. Parent will run full solution/build, real loopback socket integration, start host, read back CRUD/persistence, and launch GUI. Do not claim Hyper-V/LAN verified without actual remote device evidence.
