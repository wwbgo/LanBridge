using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LanBridge.App;

internal static class UpdateTests
{
    internal static async Task RunAsync()
    {
        await Check_accepts_only_new_stable_release_with_required_assets();
        await Check_rejects_untrusted_asset_url();
        await Download_verifies_checksum_and_cleans_failed_stage();
        Upgrade_script_waits_for_exact_process_before_installing();
        await Real_helper_safety_fixtures();
        string source = File.ReadAllText(Path.Combine(FindRepository(), "src", "LanBridge.App", "MainWindow.Updates.cs"));
        Equal(false, source.Contains("if (_connected)", StringComparison.Ordinal));
        Equal(true, source.IndexOf("UpgradeHelper.PrepareAsync", StringComparison.Ordinal) < source.IndexOf("await ShutdownBackendAsync", StringComparison.Ordinal));
        Equal(true, source.Contains("prepared.Commit()", StringComparison.Ordinal));
        Console.WriteLine("PASS UI requires helper readiness then unconditional authoritative Host shutdown then commit");
    }

    private static string FindRepository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (Directory.Exists(Path.Combine(path.FullName, "src", "LanBridge.App"))) return path.FullName;
        throw new DirectoryNotFoundException("Repository fixture not found");
    }

    private static async Task Check_accepts_only_new_stable_release_with_required_assets()
    {
        const string tag = "v0.2.0";
        string body = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = "LanBridge 0.2.0",
            body = "修复转发稳定性。",
            draft = false,
            prerelease = false,
            assets = new[]
            {
                new { name = "LanBridge-Setup.exe", browser_download_url = AssetUrl(tag, "LanBridge-Setup.exe"), size = 12L, digest = (string?)null },
                new { name = "LanBridge-win-x64.zip", browser_download_url = AssetUrl(tag, "LanBridge-win-x64.zip"), size = 12L, digest = (string?)null },
                new { name = "SHA256SUMS", browser_download_url = AssetUrl(tag, "SHA256SUMS"), size = 200L, digest = (string?)null }
            }
        });
        var client = new GitHubUpdateClient(new HttpClient(new StubHttpMessageHandler((request, _) => Json(request, body))));

        var result = await client.CheckAsync(new Version(0, 1, 0));

        Equal(true, result is not null);
        Equal(tag, result!.TagName);
        Equal("修复转发稳定性。", result.ReleaseNotes);
        Equal("LanBridge-Setup.exe", result.Installer.Name);
        Equal("LanBridge-win-x64.zip", result.Portable.Name);
        Equal("SHA256SUMS", result.Checksums.Name);
        Console.WriteLine("PASS updater accepts only newer stable releases with the three required assets");
    }

    private static async Task Check_rejects_untrusted_asset_url()
    {
        string body = JsonSerializer.Serialize(new
        {
            tag_name = "v0.2.0", name = "x", body = "", draft = false, prerelease = false,
            assets = new[]
            {
                new { name = "LanBridge-Setup.exe", browser_download_url = "https://example.invalid/LanBridge-Setup.exe", size = 1L, digest = (string?)null },
                new { name = "LanBridge-win-x64.zip", browser_download_url = AssetUrl("v0.2.0", "LanBridge-win-x64.zip"), size = 1L, digest = (string?)null },
                new { name = "SHA256SUMS", browser_download_url = AssetUrl("v0.2.0", "SHA256SUMS"), size = 1L, digest = (string?)null }
            }
        });
        var client = new GitHubUpdateClient(new HttpClient(new StubHttpMessageHandler((request, _) => Json(request, body))));

        await ThrowsAsync<InvalidDataException>(() => client.CheckAsync(new Version(0, 1, 0)));
        Console.WriteLine("PASS updater rejects non-GitHub release asset URLs");
    }

    private static async Task Download_verifies_checksum_and_cleans_failed_stage()
    {
        byte[] package = Encoding.UTF8.GetBytes("verified package");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)).ToLowerInvariant();
        const string tag = "v0.2.0";
        var release = new UpdateRelease(
            tag, "release", "notes", new Version(0, 2, 0),
            new ReleaseAsset("LanBridge-Setup.exe", new Uri(AssetUrl(tag, "LanBridge-Setup.exe")), package.Length, null),
            new ReleaseAsset("LanBridge-win-x64.zip", new Uri(AssetUrl(tag, "LanBridge-win-x64.zip")), package.Length, null),
            new ReleaseAsset("SHA256SUMS", new Uri(AssetUrl(tag, "SHA256SUMS")), 0, null));
        string root = Path.Combine(Path.GetTempPath(), "LanBridge-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsoluteUri.EndsWith("SHA256SUMS", StringComparison.Ordinal)
                ? Text(request, $"{hash}  LanBridge-Setup.exe\n{hash}  LanBridge-win-x64.zip\n")
                : request.RequestUri.AbsoluteUri.EndsWith("LanBridge-Setup.exe", StringComparison.Ordinal)
                    ? Bytes(request, package)
                    : Bytes(request, Encoding.UTF8.GetBytes("wrong portable")));
        try
        {
            var downloader = new UpdateDownloader(new HttpClient(handler), maxPackageBytes: 1024 * 1024, maxDuration: TimeSpan.FromSeconds(10));
            var staged = await downloader.DownloadAndStageAsync(release, UpdatePackageKind.Installer, root);
            Equal(true, File.Exists(staged.PackagePath));
            Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(staged.PackagePath))).ToLowerInvariant());

            var bad = release with
            {
                Checksums = release.Checksums,
                Installer = release.Installer with { Size = 99 }
            };
            await ThrowsAsync<InvalidDataException>(() => downloader.DownloadAndStageAsync(bad, UpdatePackageKind.Installer, Path.Combine(root, "bad")));
            Equal(false, Directory.Exists(Path.Combine(root, "bad")));
            Console.WriteLine("PASS updater verifies SHA256SUMS and removes failed staging data");
        }
        finally { TryDelete(root); }
    }

    private static void Upgrade_script_waits_for_exact_process_before_installing()
    {
        string script = UpgradeHelper.CreatePowerShellScript(1234, "C:\\Program Files\\LanBridge", "C:\\Temp\\LanBridge-Setup.exe", UpdatePackageKind.Installer, "C:\\Temp\\update.log");
        Equal(true, script.Contains("Wait-Process -Id $TargetPid", StringComparison.Ordinal));
        Equal(true, script.Contains("Get-Process -Id $TargetPid", StringComparison.Ordinal));
        Equal(true, script.Contains("Start-Process -FilePath $PackagePath", StringComparison.Ordinal));
        Equal(true, script.Contains("/DIR=", StringComparison.Ordinal));
        Equal(true, script.Contains("$ExpectedSha256", StringComparison.Ordinal));
        Equal(true, script.Contains("$ReadyPath", StringComparison.Ordinal));
        Equal(true, script.Contains("$CommitPath", StringComparison.Ordinal));
        Equal(true, script.Contains("$backup", StringComparison.Ordinal));
        Equal(false, script.Contains("GetTempPath", StringComparison.Ordinal));
        Equal(false, script.Contains("Select-Object -First 1", StringComparison.Ordinal));
        Equal(true, script.Contains("exit 1", StringComparison.Ordinal));
        Console.WriteLine("PASS staged upgrade helper waits for exact app PID and logs failure");
    }


    private static async Task Real_helper_safety_fixtures()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Updater integration requires Windows PowerShell");
        string root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "LanBridge-helper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        try
        {
            foreach (string scenario in new[] { "rollback", "success", "traversal", "nested", "duplicate", "config", "expanded", "corrupt", "missing", "locked" })
            {
                string stage = Path.Combine(root, scenario);
                string target = Path.Combine(root, scenario + "-app");
                Directory.CreateDirectory(stage); Directory.CreateDirectory(target);
                string app = Path.Combine(target, "LanBridge.App.exe");
                await File.WriteAllTextAsync(app, "old-app");
                await File.WriteAllTextAsync(Path.Combine(target, "LanBridge.Host.exe"), "old-host");
                await File.WriteAllTextAsync(Path.Combine(target, "rules.json"), "user rules");
                string zip = Path.Combine(stage, "update.zip");
                using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                {
                    // New file first: rollback must remove it, not only restore overwritten files.
                    using (var writer = new StreamWriter(archive.CreateEntry("new.dll").Open())) writer.Write("new-file");
                    using (var input = File.OpenRead(Path.Combine(Environment.SystemDirectory, "whoami.exe")))
                    using (var output = archive.CreateEntry(scenario == "nested" ? "nested/LanBridge.App.exe" : "LanBridge.App.exe").Open()) input.CopyTo(output);
                    using (var writer = new StreamWriter(archive.CreateEntry("LanBridge.Host.exe").Open())) writer.Write("new-host");
                    string? extra = scenario switch { "traversal" => "../outside.txt", "duplicate" => "NEW.DLL", "config" => "rules.json", _ => null };
                    if (extra is not null) { using var writer = new StreamWriter(archive.CreateEntry(extra).Open()); writer.Write("unsafe"); }
                }
                if (scenario == "expanded")
                {
                    // Forge advertised expanded size in central directory without allocating GiBs.
                    byte[] bytes = await File.ReadAllBytesAsync(zip);
                    for (int i = 0; i < bytes.Length - 28; i++)
                        if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, i + 24); break; }
                    await File.WriteAllBytesAsync(zip, bytes);
                }
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(zip)));
                if (scenario == "corrupt") await File.AppendAllTextAsync(zip, "tampered");
                if (scenario == "missing") File.Delete(zip);
                using var locked = scenario == "locked" ? new FileStream(zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
                using var dummy = StartPowerShell(ps, "-Command", "Start-Sleep -Seconds 60");
                string ready = Path.Combine(stage, "ready"), commit = Path.Combine(stage, "commit"), log = Path.Combine(stage, "upgrade.log");
                string script = Path.Combine(stage, "apply.ps1");
                await File.WriteAllTextAsync(script, UpgradeHelper.CreatePowerShellScript(dummy.Id, target, zip, UpdatePackageKind.Portable, log));
                using var helper = StartPowerShell(ps, "-ExecutionPolicy", "Bypass", "-File", script, "-TargetPid", dummy.Id.ToString(), "-TargetDirectory", target,
                    "-PackagePath", zip, "-Mode", "Portable", "-LogPath", log, "-ExpectedSha256", hash, "-ReadyPath", ready, "-CommitPath", commit, "-Nonce", "fixture",
                    "-TestFailAfterCopy", scenario == "rollback" ? "2" : "0");
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (!helper.HasExited && !File.Exists(ready)) await Task.Delay(50, timeout.Token);
                    if (scenario is "rollback" or "success")
                    {
                        if (helper.HasExited) throw new Exception(scenario + " prepare failed: " + (File.Exists(log) ? File.ReadAllText(log) : "no log"));
                        Equal(true, File.Exists(ready));
                        Equal("old-app", await File.ReadAllTextAsync(app)); // ready alone cannot mutate
                        Equal(false, File.Exists(Path.Combine(target, "new.dll")));
                        await File.WriteAllTextAsync(commit, "fixture");
                        dummy.Kill(); await dummy.WaitForExitAsync(timeout.Token);
                    }
                    await helper.WaitForExitAsync(timeout.Token);
                    if (scenario == "success")
                    {
                        Equal(0, helper.ExitCode);
                        Equal("new-host", await File.ReadAllTextAsync(Path.Combine(target, "LanBridge.Host.exe")));
                        Equal(true, File.ReadAllText(log).Contains("application restart requested", StringComparison.Ordinal));
                    }
                    else
                    {
                        if (helper.ExitCode == 0) throw new Exception(scenario + " unexpectedly succeeded");
                        Equal("old-app", await File.ReadAllTextAsync(app));
                        Equal("old-host", await File.ReadAllTextAsync(Path.Combine(target, "LanBridge.Host.exe")));
                        Equal(false, File.Exists(Path.Combine(target, "new.dll")));
                        if (scenario == "rollback") Equal(true, File.ReadAllText(log).Contains("portable rollback completed", StringComparison.Ordinal));
                        else Equal(false, File.Exists(ready));
                    }
                    Equal("user rules", await File.ReadAllTextAsync(Path.Combine(target, "rules.json")));
                    Console.WriteLine("PASS real PowerShell updater fixture: " + scenario);
                }
                finally
                {
                    if (!helper.HasExited) helper.Kill();
                    if (!dummy.HasExited) dummy.Kill();
                    await helper.WaitForExitAsync(); await dummy.WaitForExitAsync();
                }
            }
            string stageReady = Path.Combine(root, "readiness");
            Directory.CreateDirectory(stageReady);
            string pkg = Path.Combine(stageReady, "package.exe");
            await File.WriteAllTextAsync(pkg, "fixture, never installed");
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(pkg)));
            var asset = new ReleaseAsset("fixture", new Uri("https://github.com"), 1, null);
            var release = new UpdateRelease("v0.2.0", "", "", new Version(0, 2, 0), asset, asset, asset);
            var update = new StagedUpdate(release, UpdatePackageKind.Installer, pkg, digest);
            using (await UpgradeHelper.PrepareAsync(update, Environment.ProcessId, Path.Combine(root, "success-app", "LanBridge.App.exe"), readinessTimeout: TimeSpan.FromSeconds(10)))
            {
                Equal(0, Directory.GetFiles(stageReady, "*.commit").Length);
                Equal(1, Directory.GetFiles(stageReady, "*.ready").Length);
            }
            await ThrowsAsync<FileNotFoundException>(() => UpgradeHelper.PrepareAsync(update, Environment.ProcessId, Path.Combine(root, "success-app", "LanBridge.App.exe"), powershellPath: Path.Combine(root, "missing-powershell.exe")));
            await ThrowsAsync<OperationCanceledException>(() => UpgradeHelper.PrepareAsync(update, Environment.ProcessId, Path.Combine(root, "success-app", "LanBridge.App.exe"), readinessTimeout: TimeSpan.FromMilliseconds(1)));
            Equal(0, Directory.GetFiles(stageReady, "*.commit").Length);
            Console.WriteLine("PASS real readiness ACK, missing-helper failure, bounded timeout and uncommitted disposal (no installer executed)");
        }
        finally { TryDelete(root); }
    }

    private static System.Diagnostics.Process StartPowerShell(string path, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start fixture PowerShell");
    }

    private static string AssetUrl(string tag, string name) => $"https://github.com/wwbgo/LanBridge/releases/download/{tag}/{name}";
    private static HttpResponseMessage Json(HttpRequestMessage request, string body) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Text(HttpRequestMessage request, string body) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    private static HttpResponseMessage Bytes(HttpRequestMessage request, byte[] body) => new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) };
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); throw new Exception($"Expected {typeof(T).Name}"); }
        catch (T) { }
    }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request, cancellationToken));
    }
}
