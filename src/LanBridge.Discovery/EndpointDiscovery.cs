using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using LanBridge.Core;

namespace LanBridge.Discovery;

public enum TargetPlatform { Wsl, Docker, Podman, Containers }
public sealed record DiscoveredEndpoint(string Id, string Name, string Host, int? Port, TransportProtocol Protocol, string Detail);
public sealed record DiscoveryResult(IReadOnlyList<DiscoveredEndpoint> Endpoints, IReadOnlyList<string> Warnings);

public sealed class EndpointDiscovery
{
    private const int MaxDistros = 16;
    private const int MaxContainers = 32;
    private readonly ICommandRunner commands;

    public EndpointDiscovery() : this(new ProcessCommandRunner()) { }
    internal EndpointDiscovery(ICommandRunner commands) => this.commands = commands;

    public Task<DiscoveryResult> DiscoverAsync(TargetPlatform platform, CancellationToken ct = default) => platform switch
    {
        TargetPlatform.Wsl => DiscoverWslAsync(ct),
        TargetPlatform.Docker => DiscoverDockerAsync(ct),
        TargetPlatform.Podman => DiscoverPodmanAsync(ct),
        TargetPlatform.Containers => DiscoverContainersAsync(ct),
        _ => Task.FromResult(new DiscoveryResult([], [$"不支持的发现平台：{platform}"]))
    };

    private async Task<DiscoveryResult> DiscoverWslAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var endpoints = new List<DiscoveredEndpoint>();
        var result = await Run("wsl.exe", ["--list", "--verbose"], TimeSpan.FromSeconds(5), ct);
        if (!Succeeded(result)) return new([], [$"WSL 版本列表读取失败：{ShortDiagnostic(Failure(result))}"]);
        var running = await Run("wsl.exe", ["--list", "--running", "--quiet"], TimeSpan.FromSeconds(5), ct);
        if (!Succeeded(running)) return new([], ["WSL 运行状态无法确认；为避免启动发行版，已跳过查询。"]);
        var distros = ParseWslDistros(result.Stdout, warnings, ParseRunningNames(running.Stdout, warnings));

        if (distros.Count > MaxDistros) warnings.Add($"WSL 运行中发行版超过 {MaxDistros} 个，已省略其余项。");
        foreach (var distro in distros.Take(MaxDistros))
        {
            ct.ThrowIfCancellationRequested();
            var addresses = await Run("wsl.exe", ["--distribution", distro.Name, "--exec", "ip", "-j", "addr", "show"], TimeSpan.FromSeconds(5), ct);
            var hosts = new HashSet<string>(StringComparer.Ordinal);
            if (Succeeded(addresses) && TryParseJsonAddresses(addresses.Stdout, hosts))
            {
                // The structured read succeeded; no fallback command is needed.
            }
            else
            {
                var fallback = await Run("wsl.exe", ["--distribution", distro.Name, "--exec", "ip", "-o", "addr", "show"], TimeSpan.FromSeconds(5), ct);
                if (!Succeeded(fallback))
                {
                    warnings.Add($"WSL {distro.Name} 地址读取失败：{ShortDiagnostic(Failure(addresses))}；备用方式也失败：{ShortDiagnostic(Failure(fallback))}。");
                    continue;
                }
                ParseTextAddresses(fallback.Stdout, hosts);
            }
            foreach (var host in hosts)
                endpoints.Add(new($"wsl:{distro.Name}:{host}", $"WSL · {distro.Name}", host, null, TransportProtocol.Tcp, $"WSL{distro.Version} 地址候选；请填写服务端口并选择 TCP/UDP。"));
        }
        return new(endpoints, warnings);
    }

    private static HashSet<string> ParseRunningNames(string text, List<string> warnings)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in text.TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = rawLine.Trim().TrimStart('*').Trim();
            if (name.Length == 0 || name.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Length > 256 || name.Any(char.IsControl)) { warnings.Add("WSL 返回了无效的运行中发行版名称，已跳过。"); continue; }
            result.Add(name);
        }
        return result;
    }

    private static List<WslDistro> ParseWslDistros(string text, List<string> warnings, IReadOnlySet<string> runningNames)
    {
        var result = new List<WslDistro>();
        foreach (var rawLine in text.TrimStart('\uFEFF').Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart('*').Trim();
            if (runningNames is not null && !runningNames.Any(value => line.StartsWith(value, StringComparison.Ordinal) &&
                line.Length > value.Length && char.IsWhiteSpace(line[value.Length]))) continue;
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;
            var match = Regex.Match(line, @"(?<version>\d+)\s*$");
            if (!match.Success) { warnings.Add("WSL 列表中有无法识别的版本号，已跳过。"); continue; }
            var name = line[..match.Index].Trim();
            if (runningNames is not null)
            {
                var matchedName = runningNames.OrderByDescending(value => value.Length)
                    .FirstOrDefault(value => name.StartsWith(value, StringComparison.Ordinal) &&
                        (name.Length == value.Length || char.IsWhiteSpace(name[value.Length])));
                if (matchedName is null) continue;
                name = matchedName;
            }
            if (name.Length == 0 || name.Length > 256 || name.Any(char.IsControl)) { warnings.Add("WSL 返回了无效的发行版名称，已跳过。"); continue; }
            if (!int.TryParse(match.Groups["version"].Value, out var version)) { warnings.Add("WSL 返回了无效的版本号，已跳过。"); continue; }
            if (version != 2) continue;

            if (IsManagedContainerDistro(name)) continue;
            if (result.Any(d => d.Name.Equals(name, StringComparison.Ordinal))) continue;
            result.Add(new(name, version));
        }
        return result;
    }

    private static bool IsManagedContainerDistro(string name) =>
        name.StartsWith("podman-machine-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJsonAddresses(string text, HashSet<string> hosts)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var iface in doc.RootElement.EnumerateArray())
            {
                if (!TryGetProperty(iface, "addr_info", out var infos) || infos.ValueKind != JsonValueKind.Array) continue;
                foreach (var info in infos.EnumerateArray())
                {
                    var address = FindString(info, "local", "address");
                    if (address is not null && IsUsableAddress(address)) hosts.Add(address);
                }
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void ParseTextAddresses(string text, HashSet<string> hosts)
    {
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length - 1; i++)
            {
                if (tokens[i] is not ("inet" or "inet6")) continue;
                var address = tokens[i + 1].Split('/', 2)[0];
                if (IsUsableAddress(address)) hosts.Add(address);
            }
        }
    }

    private sealed record WslDistro(string Name, int Version);

    private async Task<DiscoveryResult> DiscoverContainersAsync(CancellationToken ct)
    {
        var docker = await DiscoverDockerAsync(ct);
        var podman = await DiscoverPodmanAsync(ct);
        var dockerSucceeded = docker.Endpoints.Count > 0 || !docker.Warnings.Any(IsUnavailableDockerWarning);
        var podmanSucceeded = podman.Endpoints.Count > 0 || !podman.Warnings.Any(IsUnavailablePodmanWarning);
        var warnings = new List<string>();
        warnings.AddRange(docker.Warnings.Where(w => !(podmanSucceeded && IsUnavailableDockerWarning(w))));
        warnings.AddRange(podman.Warnings.Where(w => !(dockerSucceeded && IsUnavailablePodmanWarning(w))));
        return new(DeduplicateEndpoints(docker.Endpoints.Concat(podman.Endpoints)), warnings);
    }

    private static bool IsUnavailableDockerWarning(string warning) =>
        warning.Contains("Docker 上下文读取失败", StringComparison.Ordinal) ||
        warning.Contains("Docker 上下文为空", StringComparison.Ordinal) ||
        warning.Contains("Docker CLI 不可用", StringComparison.Ordinal) ||
        warning.Contains("Docker 运行中容器列表失败", StringComparison.Ordinal);

    private static bool IsUnavailablePodmanWarning(string warning) =>
        warning.Contains("Podman 机器列表读取失败", StringComparison.Ordinal) ||
        warning.Contains("Podman 连接列表读取失败", StringComparison.Ordinal) ||
        warning.Contains("Podman 运行中容器列表失败", StringComparison.Ordinal) ||
        warning.Contains("没有验证到运行中的机器与匹配的默认本机 SSH 连接", StringComparison.Ordinal);

    private static IReadOnlyList<DiscoveredEndpoint> DeduplicateEndpoints(IEnumerable<DiscoveredEndpoint> source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredEndpoint>();
        foreach (var endpoint in source)
        {
            var host = IPAddress.TryParse(endpoint.Host, out var ip) ? ip.ToString() : endpoint.Host;
            var key = $"{host}|{endpoint.Port?.ToString() ?? ""}|{endpoint.Protocol}";
            if (seen.Add(key)) result.Add(endpoint);
        }
        return result;
    }

    private async Task<DiscoveryResult> DiscoverDockerAsync(CancellationToken ct)
    {
        var warnings = new List<string>(); var endpoints = new List<DiscoveredEndpoint>();
        var context = await Run("docker.exe", ["context", "inspect"], TimeSpan.FromSeconds(3), ct);
        if (!Succeeded(context)) return new([], [$"Docker 上下文读取失败，未创建映射：{ShortDiagnostic(Failure(context))}"]);
        if (!TryGetDockerHost(context.Stdout, out var host)) return new([], ["Docker 上下文为空或未提供可验证的本机端点，未创建映射；不会将兼容接口误判为 Docker。"]);
        if (!host.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase) || host.Length <= "npipe:////./pipe/".Length || host["npipe:////./pipe/".Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')) return new([], ["Docker 上下文属于远程或不支持的类型；仅信任 Windows 本机管道，已跳过端口。"]);
        // Pin all daemon commands to the inspected local transport; environment overrides cannot redirect them.
        var version = await Run("docker.exe", ["--host", host, "version", "--format", "json"], TimeSpan.FromSeconds(3), ct);
        if (!Succeeded(version)) return new([], [$"Docker CLI 不可用或版本查询失败：{ShortDiagnostic(Failure(version))}"]);
        if (!IsJson(version.Stdout)) return new([], ["Docker 版本结果格式无效，未创建映射。"]);
        var ps = await Run("docker.exe", ["--host", host, "ps", "--format", "{{json .}}"], TimeSpan.FromSeconds(3), ct);
        if (!Succeeded(ps)) return new([], [$"Docker 运行中容器列表失败：{ShortDiagnostic(Failure(ps))}"]);
        var containers = ParseContainerLines(ps.Stdout, warnings, "Docker");
        if (containers.Count > MaxContainers) warnings.Add($"Docker 容器超过 {MaxContainers} 个，已省略其余项。");
        foreach (var container in containers.Take(MaxContainers))
        {
            var inspect = await Run("docker.exe", ["--host", host, "inspect", "--format", "{{json .NetworkSettings.Ports}}", container.Id], TimeSpan.FromSeconds(3), ct);
            if (!Succeeded(inspect)) { warnings.Add($"Docker 容器 {container.Name} 检查失败：{ShortDiagnostic(Failure(inspect))}"); continue; }
            AddPublishedEndpoints(inspect.Stdout, "docker", container.Name, endpoints, warnings);
        }
        return new(endpoints, warnings);
    }

    private async Task<DiscoveryResult> DiscoverPodmanAsync(CancellationToken ct)
    {
        var warnings = new List<string>(); var endpoints = new List<DiscoveredEndpoint>();
        var machines = await Run("podman.exe", ["machine", "list", "--format", "json"], TimeSpan.FromSeconds(3), ct);
        if (!Succeeded(machines)) return new([], [$"Podman 机器列表读取失败：{ShortDiagnostic(Failure(machines))}"]);
        var connections = await Run("podman.exe", ["system", "connection", "list", "--format", "json"], TimeSpan.FromSeconds(3), ct);
        if (!Succeeded(connections)) return new([], [$"Podman 连接列表读取失败：{ShortDiagnostic(Failure(connections))}"]);
        if (!TryProvenLocalPodmanMachine(machines.Stdout, connections.Stdout, out var proof, out var connectionName)) return new([], [$"Podman 端口未验证为本机发布，已跳过：{proof}"]);
        var ps = await Run("podman.exe", ["--connection", connectionName, "ps", "--format", "{{json .}}"], TimeSpan.FromSeconds(3), ct);
        if (!Succeeded(ps)) return new([], [$"Podman 运行中容器列表失败：{ShortDiagnostic(Failure(ps))}"]);
        var containers = ParseContainerLines(ps.Stdout, warnings, "Podman");
        if (containers.Count > MaxContainers) warnings.Add($"Podman 容器超过 {MaxContainers} 个，已省略其余项。");
        foreach (var container in containers.Take(MaxContainers))
        {
            var inspect = await Run("podman.exe", ["--connection", connectionName, "inspect", "--format", "{{json .NetworkSettings.Ports}}", container.Id], TimeSpan.FromSeconds(3), ct);
            if (!Succeeded(inspect)) { warnings.Add($"Podman 容器 {container.Name} 检查失败：{ShortDiagnostic(Failure(inspect))}"); continue; }
            AddPublishedEndpoints(inspect.Stdout, "podman", container.Name, endpoints, warnings);
        }
        return new(endpoints, warnings);
    }

    private async Task<CommandResult> Run(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => await commands.RunAsync(new(file, args, timeout), ct);
    private static bool Succeeded(CommandResult result) => result.Error is null && result.ExitCode == 0;
    private static string Failure(CommandResult result) => result.Error ?? $"exit {result.ExitCode}: {result.Stderr.Trim()}";
    private static string ShortDiagnostic(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 160 ? compact : compact[..160] + "…";
    }

    private static bool IsJson(string text) { try { using var _ = JsonDocument.Parse(text); return true; } catch (JsonException) { return false; } }

    private static bool IsUsableAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var ip) || IPAddress.IsLoopback(ip)) return false;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return bytes[0] != 0 && bytes[0] != 127 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
        return ip.ScopeId == 0 && ((bytes[0] & 0xe0) == 0x20 || (bytes[0] & 0xfe) == 0xfc);
    }

    private static bool TryGetDockerHost(string text, out string host)
    {
        host = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1) return false;
            root = root[0];
            if (!TryGetProperty(root, "Endpoints", out var endpoints) || !TryGetProperty(endpoints, "docker", out var docker)) return false;
            host = FindString(docker, "Host") ?? "";
            return host.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryProvenLocalPodmanMachine(string machineText, string connectionText, out string proof, out string selectedConnection)
    {
        proof = "没有验证到运行中的机器与匹配的默认本机 SSH 连接";
        selectedConnection = "";
        try
        {
            using var machines = JsonDocument.Parse(machineText); using var connections = JsonDocument.Parse(connectionText);
            if (machines.RootElement.ValueKind != JsonValueKind.Array || connections.RootElement.ValueKind != JsonValueKind.Array) { proof = "机器或连接结果格式不符合预期"; return false; }
            foreach (var machine in machines.RootElement.EnumerateArray())
            {
                if (!GetBool(machine, "Running") || !GetString(machine, "Name", out var name) || !GetInt(machine, "Port", out var port)) continue;
                foreach (var connection in connections.RootElement.EnumerateArray())
                {
                    var connectionName = FindString(connection, "Name");
                    if (string.IsNullOrWhiteSpace(connectionName) || connectionName.Any(char.IsControl) || !GetBool(connection, "Default") || !GetBool(connection, "IsMachine") || (connectionName != name && connectionName != name + "-root")) continue;
                    var uriText = FindString(connection, "URI", "Uri", "uri");
                    if (uriText is null || !Uri.TryCreate(uriText, UriKind.Absolute, out var uri)) continue;
                    if (uri.Scheme == "ssh" && (uri.Host is "127.0.0.1" or "localhost" or "::1") && uri.Port == port) { selectedConnection = connectionName; proof = $"运行中的机器 {name} 与本机连接 127.0.0.1:{port} 匹配"; return true; }
                }
            }
            proof = "没有运行中的机器匹配默认本机连接（不映射远程 Podman 上下文）"; return false;
        }
        catch (JsonException) { proof = "机器或连接结果不是有效 JSON"; return false; }
    }

    private static List<ContainerRef> ParseContainerLines(string text, List<string> warnings, string engine)
    {
        var result = new List<ContainerRef>();
        foreach (var line in text.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var id = FindString(doc.RootElement, "ID", "Id", "id");
                if (string.IsNullOrWhiteSpace(id)) warnings.Add($"{engine} 返回的运行中容器缺少标识，已跳过。");
                else result.Add(new(id, FindContainerName(doc.RootElement, id)));
            }
            catch (JsonException) { warnings.Add($"{engine} 返回的运行中容器 JSON 无效，已跳过。"); }
        }
        return result;
    }

    private static string FindContainerName(JsonElement element, string id)
    {
        foreach (var propertyName in new[] { "Names", "Name", "name" })
        {
            if (!TryGetProperty(element, propertyName, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return ShortName(value.GetString(), id);
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) return ShortName(item.GetString(), id);
            }
        }
        return ShortName(null, id);
    }

    private static string ShortName(string? value, string fallback)
    {
        var name = ShortDiagnostic(value ?? "").Trim().TrimStart('/');
        if (name.Length == 0) name = fallback;
        return name.Length <= 48 ? name : name[..48] + "…";
    }

    private static void AddPublishedEndpoints(string text, string engine, string fallbackName, List<DiscoveredEndpoint> endpoints, List<string> warnings)
    {
        var label = engine.Equals("docker", StringComparison.OrdinalIgnoreCase) ? "Docker" : "Podman";
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().FirstOrDefault() : doc.RootElement;
            var id = FindString(root, "Id", "ID", "id") ?? fallbackName;
            var name = ShortName(FindString(root, "Name", "Names", "name") ?? fallbackName, fallbackName);
            if (!TryGetProperty(root, "NetworkSettings", out var network) || !TryGetProperty(network, "Ports", out var ports))
            {
                if (root.ValueKind != JsonValueKind.Object) { warnings.Add($"{label} 容器 {name} 没有结构化端口信息，未创建映射。"); return; }
                ports = root;
            }
            if (ports.ValueKind != JsonValueKind.Object) { warnings.Add($"{label} 容器 {name} 没有结构化端口信息，未创建映射。"); return; }
            var unpublished = new List<string>();
            foreach (var port in ports.EnumerateObject())
            {
                var slash = port.Name.LastIndexOf('/');
                if (slash <= 0 || !int.TryParse(port.Name[..slash], out var containerPort) || !TryProtocol(port.Name[(slash + 1)..], out var protocol))
                {
                    warnings.Add($"{label} 容器 {name} 返回了不支持的端口键，已跳过。");
                    continue;
                }
                if (port.Value.ValueKind is JsonValueKind.Null || port.Value.ValueKind != JsonValueKind.Array || port.Value.GetArrayLength() == 0)
                {
                    unpublished.Add($"{containerPort}/{protocol}");
                    continue;
                }
                foreach (var binding in port.Value.EnumerateArray())
                {
                    var hostPortText = FindString(binding, "HostPort", "host_port", "hostPort");
                    if (!int.TryParse(hostPortText, out var hostPort) || hostPort is < 1 or > 65535) { warnings.Add($"{label} 容器 {name} 的已发布端口无效，已跳过。"); continue; }
                    var hostIp = FindString(binding, "HostIp", "host_ip", "hostIP") ?? "";
                    hostIp = hostIp switch { "" or "0.0.0.0" => "127.0.0.1", "::" or "0:0:0:0:0:0:0:0" => "::1", _ => hostIp };
                    if (!IPAddress.TryParse(hostIp, out _)) { warnings.Add($"{label} 容器 {name} 的主机地址无效，已跳过。"); continue; }
                    endpoints.Add(new($"{engine}:{id}:{containerPort}/{protocol}:{hostPort}:{hostIp}", $"{label} · {name}", hostIp, hostPort, protocol, $"来自 {label} 容器 {name} 的已发布端口 {containerPort}/{protocol}。"));
                }
            }
            if (unpublished.Count > 0)
            {
                var shown = unpublished.Take(8).ToArray();
                var suffix = unpublished.Count > shown.Length ? $"等 {unpublished.Count} 项" : "";
                warnings.Add($"{label} 容器 {name} 存在未发布端口：{string.Join("、", shown)}{suffix}，未创建主机映射。");
            }
        }
        catch (JsonException) { warnings.Add($"{label} 容器 {ShortName(fallbackName, fallbackName)} 的检查结果不是有效 JSON，未创建映射。"); }
    }

    private static bool TryProtocol(string text, out TransportProtocol protocol) { protocol = text.Equals("udp", StringComparison.OrdinalIgnoreCase) ? TransportProtocol.Udp : TransportProtocol.Tcp; return text.Equals("tcp", StringComparison.OrdinalIgnoreCase) || text.Equals("udp", StringComparison.OrdinalIgnoreCase); }
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value) { if (element.ValueKind != JsonValueKind.Object) { value = default; return false; } foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } value = default; return false; }
    private static string? FindString(JsonElement element, params string[] names) { foreach (var name in names) if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static bool GetString(JsonElement element, string name, out string value) { value = FindString(element, name) ?? ""; return value.Length > 0; }
    private static bool GetBool(JsonElement element, string name) => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool GetInt(JsonElement element, string name, out int value) { value = 0; return TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value); }
    private sealed record ContainerRef(string Id, string Name);
}

internal sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout);
internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr, string? Error = null);
internal interface ICommandRunner { Task<CommandResult> RunAsync(CommandSpec command, CancellationToken ct); }

internal sealed class ProcessCommandRunner : ICommandRunner
{
    private const int MaxOutputBytes = 1024 * 1024;

    public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = command.FileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        try { if (!process.Start()) return new(-1, "", "", "process did not start"); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return new(-1, "", "", ex.Message); }
        using var timeout = new CancellationTokenSource(command.Timeout);
        using var limit = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, limit.Token);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, limit, linked.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, limit, linked.Token);
        try { await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return new(-1, "", "", limit.IsCancellationRequested ? "output limit exceeded (1 MiB per stream)" : "timed out");
        }
        return new(process.ExitCode, Decode(await stdout), Decode(await stderr));
    }

    private static string Decode(byte[] bytes)
    {
        // wsl.exe can redirect UTF-16LE without a BOM; preserve non-ASCII distro names.
        var utf16 = bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes.Length % 2 == 0 && Enumerable.Range(0, bytes.Length / 2).Count(i => bytes[i * 2 + 1] == 0) > bytes.Length / 4));
        return (utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8).GetString(bytes).TrimStart('\uFEFF');
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationTokenSource limit, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxOutputBytes + 1 - output.Length)), ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > MaxOutputBytes) { limit.Cancel(); ct.ThrowIfCancellationRequested(); }
        }
    }
}
