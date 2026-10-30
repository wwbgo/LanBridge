using LanBridge.Control;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Host.Tests;

public class PrivilegedOperationsTests
{
    [Fact]
    public async Task Firewall_request_contains_only_validated_data_and_originating_owner()
    {
        var rule = new ForwardRule { ListenAddress = "0.0.0.0", ListenPort = 65535, Protocol = TransportProtocol.TcpUdp,
            Name = "'; Stop-Computer; #", TargetHost = "untrusted", Enabled = false };
        PrivilegedOperations.Request? captured = null;
        await PrivilegedOperations.SetFirewallAsync(rule, true, (request, _) =>
        {
            captured = request;
            return Task.FromResult("{\"Ok\":true}");
        }, default);
        Assert.NotNull(captured);
        Assert.Equal(PipeProtocol.CurrentOwnerSid, captured.OwnerSid);
        Assert.Equal(rule.Id.ToString("D"), captured.RuleId);
        Assert.Equal("0.0.0.0", captured.Address);
        Assert.Equal(65535, captured.Port);
        Assert.Equal(new[] { "TCP", "UDP" }, captured.Protocols);
        Assert.True(captured.Enabled);
        Assert.DoesNotContain("untrusted", System.Text.Json.JsonSerializer.Serialize(captured));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.15.8.9")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Loopback_reconciles_to_removal_never_LAN_allow(string address)
    {
        await PrivilegedOperations.SetFirewallAsync(new() { ListenAddress = address, ListenPort = 80 }, true,
            (request, _) => { Assert.False(request.Enabled); return Task.FromResult("{\"Ok\":true}"); }, default);
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_invoke_runner()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PrivilegedOperations.SetFirewallAsync(
            new() { ListenAddress = "0.0.0.0", ListenPort = 80 }, true,
            (_, _) => throw new Xunit.Sdk.XunitException("Must not start"), stop.Token));
    }

    [Fact]
    public async Task HyperV_uses_read_only_elevated_request_and_parses_bounded_data()
    {
        var id = Guid.NewGuid();
        var rows = await PrivilegedOperations.GetHyperVAsync((request, _) =>
        {
            Assert.Equal("HyperV", request.Operation);
            return Task.FromResult("{\"Ok\":true,\"Data\":[{\"Id\":\"" + id + "\",\"Name\":\"vm\",\"State\":\"Running\",\"Addresses\":[\"192.168.1.5\"]}]}");
        }, default);
        Assert.Equal("vm", Assert.Single(rows).Name);
        await Assert.ThrowsAsync<InvalidDataException>(() => PrivilegedOperations.GetHyperVAsync(
            (_, _) => Task.FromResult(new string('x', 128 * 1024 + 1)), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => PrivilegedOperations.GetHyperVAsync(
            (_, _) => Task.FromResult("{\"Ok\":true,\"Data\":[{}]}"), default));
    }

    [Fact]
    public async Task Helper_failure_reports_recovery_state_and_rule_ID()
    {
        var rule = new ForwardRule { ListenAddress = "0.0.0.0", ListenPort = 80 };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PrivilegedOperations.SetFirewallAsync(rule, true,
            (_, _) => Task.FromResult("{\"Ok\":false,\"Error\":\"UDP create failed; rollback failed; retry remove\"}"), default));
        Assert.Contains(rule.Id.ToString("D"), error.Message);
        Assert.Contains("rollback failed", error.Message);
    }

    [Fact]
    public void Launch_contract_is_fixed_system_PowerShell_with_UAC_not_a_script_file()
    {
        var request = new PrivilegedOperations.Request("HyperV", PipeProtocol.CurrentOwnerSid, "", "", 0, [], false);
        var start = PrivilegedOperations.CreateStartInfo(request, "C:\\safe\\response.json");
        Assert.True(start.UseShellExecute);
        Assert.Equal("runas", start.Verb);
        Assert.EndsWith("WindowsPowerShell\\v1.0\\powershell.exe", start.FileName);
        Assert.Contains("-NoProfile -NonInteractive -EncodedCommand ", start.Arguments);
        Assert.DoesNotContain("Bypass", start.Arguments);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(start.Arguments.Split(' ')[^1]));
        Assert.Contains("Wait-Job", script);
        Assert.Contains("-Timeout 45", script);
        Assert.Contains("128 * 1024", script);
        Assert.DoesNotContain("Invoke-Expression", script);
        Assert.True(start.Arguments.Length < 32000);
    }

    [Fact]
    public void Response_directory_is_private_and_response_is_bounded()
    {
        var directory = PrivilegedOperations.CreatePrivateDirectory();
        try
        {
            var acl = directory.GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            var allowed = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToHashSet();
            Assert.Equal(new[] { PipeProtocol.CurrentOwnerSid, "S-1-5-18", "S-1-5-32-544" }.Order(), allowed.Order());
            var path = Path.Combine(directory.FullName, "response.json");
            File.WriteAllText(path, new string('x', 128 * 1024 + 1));
            Assert.Throws<InvalidDataException>(() => PrivilegedOperations.ReadResponse(path));
            File.WriteAllText(path, "{\"Ok\":true}");
            Assert.Equal("{\"Ok\":true}", PrivilegedOperations.ReadResponse(path));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void UAC_cancel_is_distinct_from_access_failure()
    {
        Assert.IsAssignableFrom<OperationCanceledException>(PrivilegedOperations.ExplainLaunchError(new System.ComponentModel.Win32Exception(1223)));
        Assert.IsType<InvalidOperationException>(PrivilegedOperations.ExplainLaunchError(new System.ComponentModel.Win32Exception(5)));
    }

    [Fact]
    public async Task Cancellation_after_dispatch_does_not_claim_that_committed_firewall_was_cancelled()
    {
        using var stop = new CancellationTokenSource();
        await PrivilegedOperations.SetFirewallAsync(new() { ListenAddress = "0.0.0.0", ListenPort = 80 }, true,
            (_, _) => { stop.Cancel(); return Task.FromResult("{\"Ok\":true}"); }, stop.Token);
    }

    [Fact]
    public async Task Fixed_worker_creates_both_protocols_in_narrow_scope_with_verified_ownership()
    {
        using var result = await RunWorker("", true, ["TCP", "UDP"]);
        Assert.True(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean(), result.RootElement.ToString());
        var rows = result.RootElement.GetProperty("Rules").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            Assert.Equal("Private", row.GetProperty("Profile").GetString());
            Assert.Equal("LocalSubnet", row.GetProperty("RemoteAddress").GetString());
            Assert.Equal("Inbound", row.GetProperty("Direction").GetString());
            Assert.Equal("Allow", row.GetProperty("Action").GetString());
            Assert.Equal("192.168.1.20", row.GetProperty("LocalAddress").GetString());
            Assert.Equal("8080", row.GetProperty("LocalPort").GetString());
            Assert.Equal("LanBridge.Automatic.v1", row.GetProperty("Group").GetString());
            Assert.Contains(PipeProtocol.CurrentOwnerSid, row.GetProperty("Name").GetString());
            Assert.Contains("Owner=" + PipeProtocol.CurrentOwnerSid, row.GetProperty("Description").GetString());
        }
    }

    [Fact]
    public async Task Partial_creation_failure_restores_and_verifies_previous_owned_scope()
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; if(!$first.Ok){throw $first.Error}; $d.Port=9090; $script:fail='UDP'";
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        Assert.False(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean());
        Assert.Contains("previous owned rules restored", result.RootElement.GetProperty("Result").GetProperty("Error").GetString());
        var rows = result.RootElement.GetProperty("Rules").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal("8080", row.GetProperty("LocalPort").GetString()));
    }

    [Fact]
    public async Task Fixed_worker_queries_HyperV_without_any_firewall_or_VM_start_command()
    {
        using var result = await RunWorker("$d.Operation='HyperV'", false, ["TCP"]);
        var response = result.RootElement.GetProperty("Result");
        Assert.True(response.GetProperty("Ok").GetBoolean(), response.ToString());
        Assert.Equal("VM test", Assert.Single(response.GetProperty("Data").EnumerateArray()).GetProperty("Name").GetString());
        Assert.Empty(result.RootElement.GetProperty("Rules").EnumerateArray());
        Assert.DoesNotContain("Start-VM", PrivilegedOperations.WorkerScript);
        Assert.DoesNotContain("Start-Service", PrivilegedOperations.WorkerScript);
    }

    [Fact]
    public async Task Foreign_collision_is_untouched_and_preflight_prevents_partial_deletion()
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; $n=@($script:rules.Keys|Where-Object {$_ -like '*.UDP'})[0];$script:rules[$n].Description='foreign'";
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        Assert.False(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean());
        Assert.Contains("Foreign firewall name collision", result.RootElement.GetProperty("Result").GetProperty("Error").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("Rules").GetArrayLength());
        Assert.Contains(result.RootElement.GetProperty("Rules").EnumerateArray(), r => r.GetProperty("Description").GetString() == "foreign");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Edits_remove_stale_protocol_or_remove_all_owned_rules(bool enabled)
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; $d.Protocols=@('TCP');$d.Port=9090;$d.Enabled=" + (enabled ? "$true" : "$false");
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        Assert.True(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean(), result.RootElement.ToString());
        var rows = result.RootElement.GetProperty("Rules").EnumerateArray().ToArray();
        Assert.Equal(enabled ? 1 : 0, rows.Length);
        if (enabled) { Assert.Equal("TCP", rows[0].GetProperty("Protocol").GetString()); Assert.Equal("9090", rows[0].GetProperty("LocalPort").GetString()); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"Ok\":1}")]
    [InlineData("{}")]
    public void Malformed_envelope_is_invalid_data(string json) =>
        Assert.Throws<InvalidDataException>(() => PrivilegedOperations.ParseResponse(json, "test"));

    [Fact]
    public void Worker_serializes_same_rule_mutations_and_uses_trusted_modules()
    {
        Assert.Contains("[Threading.Mutex]", PrivilegedOperations.WorkerScript);
        Assert.Contains("WaitOne(5000)", PrivilegedOperations.WorkerScript);
        Assert.Contains("ReleaseMutex", PrivilegedOperations.WorkerScript);
        Assert.Contains("Modules\\NetSecurity", PrivilegedOperations.WorkerScript);
        Assert.DoesNotContain("-ExecutionPolicy", PrivilegedOperations.WorkerScript);
    }

    [Fact]
    public async Task Removal_can_delete_owned_rules_even_if_their_scope_was_edited_externally()
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; foreach($r in $script:rules.Values){$r.Profile='Public';$r.Enabled='False'};$d.Enabled=$false";
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        Assert.True(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean(), result.RootElement.ToString());
        Assert.Empty(result.RootElement.GetProperty("Rules").EnumerateArray());
    }

    [Fact]
    public async Task Update_refuses_to_broaden_externally_restricted_owned_rule_during_rollback()
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; foreach($r in $script:rules.Values){$r.Program='restricted.exe'}";
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        Assert.False(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean());
        Assert.Contains("Externally modified", result.RootElement.GetProperty("Result").GetProperty("Error").GetString());
        Assert.All(result.RootElement.GetProperty("Rules").EnumerateArray(), r => Assert.Equal("restricted.exe", r.GetProperty("Program").GetString()));
    }

    [Fact]
    public async Task Real_job_wrapper_returns_data_via_private_fixed_file_without_UAC_or_firewall()
    {
        var directory = PrivilegedOperations.CreatePrivateDirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "response.json");
            var request = new PrivilegedOperations.Request("NotAnAction", PipeProtocol.CurrentOwnerSid, "", "", 0, [], false);
            var start = PrivilegedOperations.CreateStartInfo(request, path);
            // Deliberately run an unknown operation without elevation: validates only the real job/file transport.
            start.UseShellExecute = false; start.Verb = ""; start.CreateNoWindow = true;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, await error);
            using var response = System.Text.Json.JsonDocument.Parse(PrivilegedOperations.ReadResponse(path));
            Assert.False(response.RootElement.GetProperty("Ok").GetBoolean());
            Assert.Contains("Unknown privileged operation", response.RootElement.GetProperty("Error").GetString());
            Assert.Equal("", await output);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("TCP")]
    [InlineData("UDP")]
    public async Task Initial_partial_create_is_removed_on_failure(string failingProtocol)
    {
        using var result = await RunWorker("$script:fail='" + failingProtocol + "'", true, ["TCP", "UDP"]);
        Assert.False(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean());
        Assert.Empty(result.RootElement.GetProperty("Rules").EnumerateArray());
    }

    [Fact]
    public void Test_harness_shadows_every_mutating_NetSecurity_command()
    {
        foreach (System.Text.RegularExpressions.Match command in System.Text.RegularExpressions.Regex.Matches(
            PrivilegedOperations.WorkerScript, @"\b(?:New|Remove|Set|Enable|Disable)-NetFirewall\w+\b"))
            Assert.Contains("function " + command.Value + " ", FakeFirewall);
    }

    [Fact]
    public async Task Rollback_failure_returns_exact_owned_name_and_retry_instruction()
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; $d.Port=9090; $script:fail='UDP'; $script:failAlways=$true";
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        var error = result.RootElement.GetProperty("Result").GetProperty("Error").GetString();
        Assert.Contains("rollback failed", error);
        Assert.Contains("11111111-1111-1111-1111-111111111111", error);
        Assert.Contains("retry this same rule operation", error);
    }

    [Fact]
    public async Task Removal_readback_failure_reports_partial_state_and_exact_retry_scope()
    {
        string setup = "$first=& {" + PrivilegedOperations.WorkerScript + "} $d; $d.Enabled=$false; $script:refuseRemove='UDP'";
        using var result = await RunWorker(setup, true, ["TCP", "UDP"]);
        var error = result.RootElement.GetProperty("Result").GetProperty("Error").GetString();
        Assert.Contains("Removal may be partial", error);
        Assert.Contains(".UDP", error);
        Assert.Contains("retry remove", error);
        Assert.Single(result.RootElement.GetProperty("Rules").EnumerateArray());
    }

    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0/0")]
    [InlineData("::", "::/0")]
    public async Task Wildcard_uses_only_listener_address_family(string address, string expected)
    {
        using var result = await RunWorker("$d.Address='" + address + "'", true, ["TCP"]);
        Assert.True(result.RootElement.GetProperty("Result").GetProperty("Ok").GetBoolean(), result.RootElement.ToString());
        Assert.Equal(expected, Assert.Single(result.RootElement.GetProperty("Rules").EnumerateArray()).GetProperty("LocalAddress").GetString());
    }

    private static async Task<System.Text.Json.JsonDocument> RunWorker(string setup, bool enabled, string[] protocols, string? after = null)
    {
        var request = new PrivilegedOperations.Request("Firewall", PipeProtocol.CurrentOwnerSid,
            "11111111-1111-1111-1111-111111111111", "192.168.1.20", 8080, protocols, enabled);
        string data = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request));
        // All commands used by the real worker are shadowed. Never import or invoke live NetSecurity in tests.
        string script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; " + FakeFirewall +
            "$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "'))|ConvertFrom-Json; " + setup +
            "; $result=& {" + PrivilegedOperations.WorkerScript + "} $d; " + after +
            "; ConvertTo-Json -Depth 12 -Compress -InputObject @{Result=$result;Rules=@($script:rules.Values)}";
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "& ([scriptblock]::Create([Console]::In.ReadToEnd()))" }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await error);
        return System.Text.Json.JsonDocument.Parse(await output);
    }

    private const string FakeFirewall = """
        $script:rules=@{}; $script:fail='';
        function Import-Module { param($Name) }
        function Get-VM { [pscustomobject]@{Id=[guid]'22222222-2222-2222-2222-222222222222';Name='VM test';State='Running'} }
        function Get-VMNetworkAdapter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { [pscustomobject]@{IPAddresses=@('192.168.1.30')} } }
        function Get-NetFirewallRule { param($Name,$PolicyStore,$ErrorAction) if($script:rules.ContainsKey($Name)){$script:rules[$Name]} }
        function Get-NetFirewallPortFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function Get-NetFirewallAddressFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function Get-NetFirewallApplicationFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function Get-NetFirewallServiceFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function Get-NetFirewallInterfaceFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function Get-NetFirewallInterfaceTypeFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function Get-NetFirewallSecurityFilter { param([Parameter(ValueFromPipeline=$true)]$InputObject) process { $InputObject } }
        function New-NetFirewallRule {
            param($Name,$DisplayName,$Group,$Description,$PolicyStore,$Direction,$Action,$Profile,$Enabled,$Protocol,$LocalPort,$LocalAddress,$RemoteAddress,$EdgeTraversalPolicy,$ErrorAction)
            if($script:fail -eq $Protocol){if(!$script:failAlways){$script:fail=''};throw 'Injected creation failure'}
            $script:rules[$Name]=[pscustomobject]@{Name=$Name;Group=$Group;Description=$Description;Direction=$Direction;Action=$Action;Profile=$Profile;Enabled=$Enabled;Protocol=$Protocol;LocalPort=[string]$LocalPort;RemotePort='Any';LocalAddress=$LocalAddress;RemoteAddress=$RemoteAddress;EdgeTraversalPolicy=$EdgeTraversalPolicy;Program='Any';Package='Any';Service='Any';InterfaceAlias='Any';InterfaceType='Any';Authentication='NotRequired';Encryption='NotRequired';LocalUser='Any';RemoteUser='Any';RemoteMachine='Any';OverrideBlockRules='False';LooseSourceMapping='False';LocalOnlyMapping='False'}
        }
        function Remove-NetFirewallRule { param($Name,$PolicyStore,$ErrorAction) if($script:refuseRemove -and $Name.EndsWith('.'+$script:refuseRemove)){return};$script:rules.Remove($Name) }
        """;

    [Fact]
    public async Task Invalid_firewall_input_is_rejected_before_elevation()
    {
        var valid = new ForwardRule { ListenAddress = "192.168.1.20", ListenPort = 8080 };
        await Assert.ThrowsAsync<ArgumentNullException>(() => PrivilegedOperations.SetFirewallAsync(null!, true));
        foreach (var rule in new[] {
            valid with { Id = Guid.Empty }, valid with { ListenPort = 0 }, valid with { ListenPort = 65536 },
            valid with { ListenAddress = "localhost" }, valid with { ListenAddress = "1.2.3.4;whoami" },
            valid with { ListenAddress = "192.168.1.1/24" }, valid with { Protocol = (TransportProtocol)99 }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => PrivilegedOperations.SetFirewallAsync(rule, true));
    }
}
