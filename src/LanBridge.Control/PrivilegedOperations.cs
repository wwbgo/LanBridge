using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LanBridge.Core;

[assembly: InternalsVisibleTo("LanBridge.Host.Tests")]

namespace LanBridge.Control;

/// <summary>Explicit, short-lived UAC operations; never elevates the UI or forwarding host.</summary>
public static class PrivilegedOperations
{
    internal sealed record Request(string Operation, string OwnerSid, string RuleId, string Address, int Port, string[] Protocols, bool Enabled);

    public static Task<List<VirtualMachineInfo>> GetHyperVAsync(CancellationToken ct = default) =>
        GetHyperVAsync(RunElevatedAsync, ct);

    internal static async Task<List<VirtualMachineInfo>> GetHyperVAsync(Func<Request, CancellationToken, Task<string>> runner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var response = ParseResponse(await runner(new("HyperV", PipeProtocol.CurrentOwnerSid, "", "", 0, [], false), ct), "Hyper-V");
        try
        {
            var rows = response.RootElement.GetProperty("Data").Deserialize<List<VirtualMachineInfo>>()
                ?? throw new InvalidDataException("Hyper-V returned no data.");
            if (rows.Count > 128 || rows.Any(v => v is null || !Guid.TryParse(v.Id, out _) || v.Name is null || v.Name.Length > 1024 ||
                v.State is null || v.State.Length > 128 || v.Addresses is null || v.Addresses.Length > 128 ||
                v.Addresses.Any(a => !IPAddress.TryParse(a, out _))))
                throw new InvalidDataException("Hyper-V returned invalid or oversized VM data.");
            return rows;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Hyper-V returned an invalid response.", ex); }
    }

    public static Task SetFirewallAsync(ForwardRule rule, bool enabled, CancellationToken ct = default) =>
        SetFirewallAsync(rule, enabled, RunElevatedAsync, ct);

    internal static async Task SetFirewallAsync(ForwardRule rule, bool enabled,
        Func<Request, CancellationToken, Task<string>> runner, CancellationToken ct)
    {
        var address = Validate(rule);
        ct.ThrowIfCancellationRequested();
        var protocols = rule.Protocol switch
        {
            TransportProtocol.Tcp => new[] { "TCP" }, TransportProtocol.Udp => new[] { "UDP" }, _ => new[] { "TCP", "UDP" }
        };
        var request = new Request("Firewall", PipeProtocol.CurrentOwnerSid, rule.Id.ToString("D"),
            address.ToString(), rule.ListenPort, protocols, enabled && !IPAddress.IsLoopback(address));
        using var response = ParseResponse(await runner(request, ct), "Firewall " + request.RuleId);
    }

    internal static JsonDocument ParseResponse(string json, string operation)
    {
        if (Encoding.UTF8.GetByteCount(json) > 128 * 1024) throw new InvalidDataException("Elevated response exceeds 128 KiB.");
        JsonDocument response;
        try { response = JsonDocument.Parse(json, new() { MaxDepth = 12 }); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid elevated response.", ex); }
        if (response.RootElement.ValueKind != JsonValueKind.Object || !response.RootElement.TryGetProperty("Ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { response.Dispose(); throw new InvalidDataException("Missing helper status."); }
        if (!ok.GetBoolean())
        {
            string error = response.RootElement.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "Unknown helper failure";
            response.Dispose();
            throw new InvalidOperationException(operation + ": " + error[..Math.Min(error.Length, 4096)]);
        }
        return response;
    }

    internal static ProcessStartInfo CreateStartInfo(Request request, string responsePath)
    {
        string data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { Request = request, Response = responsePath }));
        string script = "$ErrorActionPreference='Stop';$env:PSModulePath=Join-Path $PSHOME 'Modules'; " +
            "$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "'))|ConvertFrom-Json; " +
            "$ErrorActionPreference='Stop'; $j=$null; try {$j=Start-Job -ScriptBlock {" + WorkerScript + "} -ArgumentList $d.Request; " +
            "if(!(Wait-Job $j -Timeout 45)){throw 'Helper timed out; firewall state may be partial. Retry the same rule operation to reconcile.'}; " +
            "$r=Receive-Job $j -ErrorAction Stop; $text=ConvertTo-Json -InputObject $r -Depth 8 -Compress; " +
            "if([Text.Encoding]::UTF8.GetByteCount($text) -gt (128 * 1024)){throw 'Helper output exceeds 128 KiB'} " +
            "}catch{$text=ConvertTo-Json -InputObject @{Ok=$false;Error=$_.Exception.Message} -Compress} " +
            "finally{if($j){Stop-Job $j -ErrorAction SilentlyContinue;Remove-Job $j -Force -ErrorAction SilentlyContinue}}; " +
            "$f=[IO.File]::Open($d.Response,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None); " +
            "try{$b=[Text.Encoding]::UTF8.GetBytes($text);$f.Write($b,0,$b.Length)}finally{$f.Dispose()}";
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
        };
    }

    // This script is compiled into the app. Only the small, validated data record is embedded in the command;
    // no script, command name, executable, or elevated action is read from a user-writable file.
    internal const string WorkerScript = """
        param($d)
        $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'
        $env:PSModulePath=Join-Path $PSHOME 'Modules'; $mutex=$null; $locked=$false
        try {
          $sid=[Security.Principal.SecurityIdentifier]::new([string]$d.OwnerSid)
          if(!$sid.IsAccountSid()){throw 'Invalid originating account SID'}
          if($d.Operation -eq 'HyperV') {
            Import-Module (Join-Path $PSHOME 'Modules\Hyper-V') -ErrorAction Stop
            $v=@(); foreach($vm in Get-VM -ErrorAction Stop) {
              if($v.Count -ge 128){throw 'Hyper-V exceeds 128 VMs; enter the target IP manually'}
              $addresses=@($vm|Get-VMNetworkAdapter -ErrorAction Stop|ForEach-Object {$_.IPAddresses}|Where-Object {$_})
              if($addresses.Count -gt 128 -or $vm.Name.Length -gt 1024){throw 'Hyper-V VM data exceeds safety limit'}
              $v+=@{Id=$vm.Id.ToString();Name=$vm.Name;State=$vm.State.ToString();Addresses=$addresses}
            }
            return @{Ok=$true;Data=@($v)}
          }
          if($d.Operation -ne 'Firewall'){throw 'Unknown privileged operation'}
          $id=[guid]::ParseExact([string]$d.RuleId,'D')
          $ip=[Net.IPAddress]::Parse([string]$d.Address)
          if($id -eq [guid]::Empty -or $d.Port -lt 1 -or $d.Port -gt 65535 -or @($d.Protocols).Count -lt 1 -or @($d.Protocols).Count -gt 2 -or @($d.Protocols|Where-Object {$_ -cnotin @('TCP','UDP')}).Count){throw 'Invalid firewall data'}
          if($ip.IsIPv4MappedToIPv6){$ip=$ip.MapToIPv4()}
          $allow=[bool]$d.Enabled -and ![Net.IPAddress]::IsLoopback($ip)
          $address=$ip.ToString(); if($address -eq '0.0.0.0'){$address='0.0.0.0/0'};if($address -eq '::'){$address='::/0'}
          Import-Module (Join-Path $PSHOME 'Modules\NetSecurity') -ErrorAction Stop
          $group='LanBridge.Automatic.v1'; $prefix='LanBridge.Auto.'+$sid.Value+'.'+$id.ToString('D')+'.'
          $description='LanBridge.Automatic.v1;Owner='+$sid.Value+';Rule='+$id.ToString('D')
          $mutex=[Threading.Mutex]::new($false,('Global\'+$prefix))
          try{$locked=$mutex.WaitOne(5000)}catch [Threading.AbandonedMutexException]{$locked=$true}
          if(!$locked){throw 'Another operation owns this rule; retry after it completes'}
          function Read-Rule($protocol) {
            $name=$prefix+$protocol; $errors=@()
            $rows=@(Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue -ErrorVariable errors)
            foreach($e in $errors){if($e.FullyQualifiedErrorId -notlike 'CmdletizationQuery_NotFound_Name*'){throw $e}}
            if(!$rows.Count){return $null}
            if($rows.Count -ne 1 -or $rows[0].Name -cne $name -or $rows[0].Group -cne $group -or $rows[0].Description -cne $description){throw ('Foreign firewall name collision: '+$name+'; no foreign rule will be changed')}
            $r=$rows[0]; $p=$r|Get-NetFirewallPortFilter -ErrorAction Stop; $a=$r|Get-NetFirewallAddressFilter -ErrorAction Stop
            return @{Rule=$r;Protocol=[string]$p.Protocol;Port=[string]$p.LocalPort;Address=[string]$a.LocalAddress;Remote=[string]$a.RemoteAddress}
          }
          function Add-Rule($protocol,$port,$local) {
            $null=New-NetFirewallRule -Name ($prefix+$protocol) -DisplayName ('LanBridge '+$id+' '+$protocol) -Group $group -Description $description -PolicyStore PersistentStore -Direction Inbound -Action Allow -Profile Private -Enabled True -Protocol $protocol -LocalPort $port -LocalAddress $local -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block -ErrorAction Stop
          }
          function Verify-Rule($protocol,$port,$local) {
            $r=Read-Rule $protocol
            if(!$r -or $r.Protocol -ne $protocol -or $r.Port -ne [string]$port -or $r.Address -ne $local -or $r.Remote -ne 'LocalSubnet' -or [string]$r.Rule.Profile -ne 'Private' -or [string]$r.Rule.Direction -ne 'Inbound' -or [string]$r.Rule.Action -ne 'Allow' -or [string]$r.Rule.Enabled -ne 'True' -or [string]$r.Rule.EdgeTraversalPolicy -ne 'Block'){throw ('Firewall readback mismatch: '+$prefix+$protocol)}
          }
          if(!$allow) {
            foreach($protocol in @('TCP','UDP')){$null=Read-Rule $protocol}
            $failures=@()
            foreach($protocol in @('TCP','UDP')) {
              try{if(Read-Rule $protocol){$null=Remove-NetFirewallRule -Name ($prefix+$protocol) -PolicyStore PersistentStore -ErrorAction Stop};if(Read-Rule $protocol){throw 'Removal readback mismatch'}}
              catch{$failures+=($prefix+$protocol+': '+$_.Exception.Message)}
            }
            if($failures.Count){throw ('Removal may be partial; retry remove for this exact owned rule: '+($failures -join '; '))}
            return @{Ok=$true}
          }
          function Assert-Canonical($old) {
            $r=$old.Rule; $app=$r|Get-NetFirewallApplicationFilter -ErrorAction Stop; $svc=$r|Get-NetFirewallServiceFilter -ErrorAction Stop
            $alias=$r|Get-NetFirewallInterfaceFilter -ErrorAction Stop; $type=$r|Get-NetFirewallInterfaceTypeFilter -ErrorAction Stop; $sec=$r|Get-NetFirewallSecurityFilter -ErrorAction Stop
            $port=$r|Get-NetFirewallPortFilter -ErrorAction Stop
            if([string]$app.Program -ne 'Any' -or [string]$app.Package -ne 'Any' -or [string]$svc.Service -ne 'Any' -or [string]$alias.InterfaceAlias -ne 'Any' -or [string]$type.InterfaceType -ne 'Any' -or [string]$sec.Authentication -ne 'NotRequired' -or [string]$sec.Encryption -ne 'NotRequired' -or [string]$sec.LocalUser -ne 'Any' -or [string]$sec.RemoteUser -ne 'Any' -or [string]$sec.RemoteMachine -ne 'Any' -or [string]$sec.OverrideBlockRules -ne 'False' -or [string]$r.LooseSourceMapping -ne 'False' -or [string]$r.LocalOnlyMapping -ne 'False' -or [string]$port.RemotePort -ne 'Any') {
              throw ('Externally modified owned scope; remove this exact owned rule before enabling again: '+$r.Name)
            }
          }
          $before=@{}; foreach($protocol in @('TCP','UDP')) {
            $old=Read-Rule $protocol
            if($old){Assert-Canonical $old;Verify-Rule $protocol $old.Port $old.Address; $before[$protocol]=$old}
          }
          try {
            foreach($protocol in @('TCP','UDP')) {
              if(Read-Rule $protocol){$null=Remove-NetFirewallRule -Name ($prefix+$protocol) -PolicyStore PersistentStore -ErrorAction Stop}
              if($allow -and $protocol -cin $d.Protocols){Add-Rule $protocol $d.Port $address;Verify-Rule $protocol $d.Port $address}
              elseif(Read-Rule $protocol){throw ('Firewall removal readback mismatch: '+$prefix+$protocol)}
            }
          } catch {
            $failure=$_.Exception.Message; $recovery=@()
            foreach($protocol in @('TCP','UDP')) {
              try {
                if(Read-Rule $protocol){$null=Remove-NetFirewallRule -Name ($prefix+$protocol) -PolicyStore PersistentStore -ErrorAction Stop}
                if($before.ContainsKey($protocol)){$old=$before[$protocol];Add-Rule $protocol $old.Port $old.Address;Verify-Rule $protocol $old.Port $old.Address}
                elseif(Read-Rule $protocol){throw 'Rollback removal readback mismatch'}
              }catch{$recovery+=($protocol+': '+$_.Exception.Message)}
            }
            if($recovery.Count){throw ($failure+'; rollback failed: '+($recovery -join '; ')+'; retry this same rule operation to reconcile only owned rules: '+$prefix)}
            throw ($failure+'; previous owned rules restored and verified: '+$prefix)
          }
          return @{Ok=$true}
        }catch{return @{Ok=$false;Error=$_.Exception.Message}}
        finally{if($locked){$mutex.ReleaseMutex()};if($mutex){$mutex.Dispose()}}
        """;

    internal static DirectoryInfo CreatePrivateDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = new SecurityIdentifier(PipeProtocol.CurrentOwnerSid);
        security.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "LanBridge.Uac." + Guid.NewGuid().ToString("N")));
        directory.Create(security);
        return directory;
    }

    internal static string ReadResponse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unsafe helper response link.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 or > 128 * 1024) throw new InvalidDataException("Invalid helper response size.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    internal static Exception ExplainLaunchError(Win32Exception ex) => ex.NativeErrorCode == 1223
        ? new OperationCanceledException("UAC consent was cancelled; no privileged operation was started.", ex)
        : new InvalidOperationException("Could not start the UAC helper: " + ex.Message, ex);

    private static async Task<string> RunElevatedAsync(Request request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = CreatePrivateDirectory();
        var responsePath = Path.Combine(directory.FullName, "response.json");
        bool exited = true;
        try
        {
            // ShellExecute can block on the consent dialog. Keep it off the UI thread; cancellation is safe only before launch.
            using var process = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                try { return Process.Start(CreateStartInfo(request, responsePath)) ?? throw new InvalidOperationException("UAC helper did not start."); }
                catch (Win32Exception ex) { throw ExplainLaunchError(ex); }
            }, CancellationToken.None);
            exited = false;
            // Once launched do not abandon a mutation or claim cancellation. The helper has its own 45-second deadline.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(65)); exited = true; }
            catch (TimeoutException ex)
            { throw new TimeoutException($"{request.Operation} {request.RuleId}: UAC helper did not finish; state is unknown. Do not retry until it exits; then retry the same operation to reconcile. Recovery data: {directory.FullName}", ex); }
            if (process.ExitCode != 0 || !File.Exists(responsePath))
                throw new InvalidOperationException($"{request.Operation} {request.RuleId}: helper exited without a response (code {process.ExitCode}); firewall state is unknown. Retry the same rule operation to reconcile.");
            return ReadResponse(responsePath);
        }
        finally
        {
            // An uncompleted elevated helper cannot safely be killed by a medium-integrity caller. Leave its private response path intact.
            if (exited)
            {
                try { directory.Delete(true); }
                catch (IOException) { /* Random private directory may be cleaned by the OS later. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static IPAddress Validate(ForwardRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Id == Guid.Empty || rule.ListenPort is < 1 or > 65535 || !Enum.IsDefined(rule.Protocol) ||
            string.IsNullOrEmpty(rule.ListenAddress) || rule.ListenAddress.Contains('%') ||
            !IPAddress.TryParse(rule.ListenAddress, out var address))
            throw new ArgumentException("Firewall requires a nonempty rule ID, literal IP, port 1–65535 and a known protocol.", nameof(rule));
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
