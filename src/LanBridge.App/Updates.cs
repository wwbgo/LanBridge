using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanBridge.App;

internal static class UpdateEndpoints
{
    internal const string LatestReleaseApi = "https://api.github.com/repos/wwbgo/LanBridge/releases/latest";
    internal const string Repository = "wwbgo/LanBridge";
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    internal const long MaximumPortableBytes = 1024L * 1024 * 1024;
    internal const long MaximumChecksumBytes = 1024L * 1024;
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);
}

internal sealed record ReleaseAsset(string Name, Uri DownloadUri, long Size, string? Digest);

internal sealed record UpdateRelease(
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    Version Version,
    ReleaseAsset Installer,
    ReleaseAsset Portable,
    ReleaseAsset Checksums);

internal enum UpdatePackageKind
{
    Installer,
    Portable
}

internal sealed record StagedUpdate(UpdateRelease Release, UpdatePackageKind Kind, string PackagePath, string ExpectedSha256);

internal sealed class GitHubUpdateClient
{
    private static readonly Regex StableTag = new("^v(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    internal GitHubUpdateClient(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _timeout = timeout ?? UpdateEndpoints.DefaultRequestTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    internal async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateEndpoints.LatestReleaseApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("LanBridge-update-check/1.0");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub 更新查询失败：HTTP {(int)response.StatusCode}。", null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
        var root = document.RootElement;
        string tag = RequiredString(root, "tag_name");
        Match tagMatch = StableTag.Match(tag);
        if (!tagMatch.Success || root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub latest release 不是受支持的稳定 vX.Y.Z 版本。");

        var version = new Version(int.Parse(tagMatch.Groups["major"].Value), int.Parse(tagMatch.Groups["minor"].Value), int.Parse(tagMatch.Groups["patch"].Value));
        if (version <= currentVersion) return null;

        var assets = new List<ReleaseAsset>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = RequiredString(asset, "name");
            string url = RequiredString(asset, "browser_download_url");
            long size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.ValueKind == JsonValueKind.Number ? sizeElement.GetInt64() : -1;
            string? digest = asset.TryGetProperty("digest", out var digestElement) && digestElement.ValueKind != JsonValueKind.Null ? digestElement.GetString() : null;
            if (size < 0) throw new InvalidDataException($"GitHub 资产 {name} 缺少有效大小。");
            var uri = new Uri(url, UriKind.Absolute);
            if (!IsOfficialAssetUri(uri, tag, name)) throw new InvalidDataException($"GitHub 资产 {name} 的下载地址不是官方仓库路径。");
            if (assets.Any(existing => existing.Name.Equals(name, StringComparison.Ordinal))) throw new InvalidDataException($"GitHub Release 包含重复资产 {name}。");
            assets.Add(new ReleaseAsset(name, uri, size, digest));
        }

        ReleaseAsset installer = RequiredAsset(assets, "LanBridge-Setup.exe");
        ReleaseAsset portable = RequiredAsset(assets, "LanBridge-win-x64.zip");
        ReleaseAsset checksums = RequiredAsset(assets, "SHA256SUMS");
        if (installer.Size > UpdateEndpoints.MaximumInstallerBytes || portable.Size > UpdateEndpoints.MaximumPortableBytes || checksums.Size > UpdateEndpoints.MaximumChecksumBytes)
            throw new InvalidDataException("GitHub 更新资产超过安全大小上限。");

        return new UpdateRelease(
            tag,
            root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "",
            version,
            installer,
            portable,
            checksums);
    }

    internal static bool IsOfficialAssetUri(Uri uri, string tag, string assetName)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        string expectedPath = $"/wwbgo/LanBridge/releases/download/{tag}/{assetName}";
        return uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal);
    }

    private static HttpClient CreateDefaultClient() => new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static ReleaseAsset RequiredAsset(IEnumerable<ReleaseAsset> assets, string name) =>
        assets.SingleOrDefault(asset => asset.Name.Equals(name, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"GitHub Release 缺少必需资产 {name}。");

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"GitHub Release 缺少字段 {property}。");
        return value.GetString()!;
    }
}

internal sealed class UpdateDownloader
{
    private static readonly Regex ChecksumLine = new("^(?<hash>[0-9a-fA-F]{64})  (?<name>[^\\r\\n]+)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly long _maxPackageBytes;
    private readonly TimeSpan _maxDuration;

    internal UpdateDownloader(HttpClient httpClient, long maxPackageBytes = UpdateEndpoints.MaximumPortableBytes, TimeSpan? maxDuration = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (maxPackageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPackageBytes));
        _maxPackageBytes = maxPackageBytes;
        _maxDuration = maxDuration ?? UpdateEndpoints.DefaultDownloadTimeout;
        if (_maxDuration <= TimeSpan.Zero || _maxDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(maxDuration));
    }

    internal async Task<StagedUpdate> DownloadAndStageAsync(UpdateRelease release, UpdatePackageKind kind, string stagingDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        string packagePath = Path.Combine(stagingDirectory, kind == UpdatePackageKind.Installer ? release.Installer.Name : release.Portable.Name);
        bool complete = false;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_maxDuration);
            byte[] checksumBytes = await DownloadBytesAsync(release.Checksums, UpdateEndpoints.MaximumChecksumBytes, deadline.Token).ConfigureAwait(false);
            string expectedHash = ParseChecksum(checksumBytes, kind == UpdatePackageKind.Installer ? release.Installer.Name : release.Portable.Name);
            ReleaseAsset package = kind == UpdatePackageKind.Installer ? release.Installer : release.Portable;
            string partialPath = packagePath + ".partial";
            string actualHash = await DownloadFileAsync(package, partialPath, PackageLimit(kind), deadline.Token).ConfigureAwait(false);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"更新包 SHA-256 校验失败：{package.Name}。");
            VerifyGitHubDigest(package, actualHash);
            File.Move(partialPath, packagePath, true);
            complete = true;
            return new StagedUpdate(release, kind, packagePath, expectedHash);
        }
        finally
        {
            if (!complete) TryDeleteDirectory(stagingDirectory);
        }
    }

    private long PackageLimit(UpdatePackageKind kind) => Math.Min(_maxPackageBytes, kind == UpdatePackageKind.Installer ? UpdateEndpoints.MaximumInstallerBytes : UpdateEndpoints.MaximumPortableBytes);

    private async Task<byte[]> DownloadBytesAsync(ReleaseAsset asset, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long declared && declared > maximumBytes) throw new InvalidDataException($"更新校验文件超过 {maximumBytes} 字节上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        await CopyBoundedAsync(stream, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task<string> DownloadFileAsync(ReleaseAsset asset, string partialPath, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        if (asset.Size > maximumBytes || (response.Content.Headers.ContentLength is long declared && declared > maximumBytes)) throw new InvalidDataException($"更新包 {asset.Name} 超过安全大小上限。");
        if (asset.Size > 0 && response.Content.Headers.ContentLength is long actualLength && actualLength != asset.Size) throw new InvalidDataException($"更新包 {asset.Name} 大小与 GitHub 声明不一致。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException($"更新包 {asset.Name} 超过安全大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (asset.Size > 0 && total != asset.Size) throw new InvalidDataException($"更新包 {asset.Name} 实际大小与 GitHub 声明不一致。");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> SendAssetAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.ParseAdd("LanBridge-update/1.0");
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { response.Dispose(); throw new HttpRequestException($"下载更新资产失败：HTTP {(int)response.StatusCode}。", null, response.StatusCode); }
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is not null && !IsAllowedFinalUri(asset.DownloadUri, finalUri)) { response.Dispose(); throw new InvalidDataException("更新下载发生到非 GitHub 官方资产地址的重定向。"); }
        return response;
    }

    private static bool IsAllowedFinalUri(Uri original, Uri final)
    {
        if (!final.IsAbsoluteUri || !final.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (final.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return final.AbsoluteUri.Equals(original.AbsoluteUri, StringComparison.Ordinal);
        return final.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximumBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("下载内容超过安全大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ParseChecksum(byte[] bytes, string assetName)
    {
        string? found = null;
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Match match = ChecksumLine.Match(line);
            if (!match.Success) continue;
            if (!match.Groups["name"].Value.Equals(assetName, StringComparison.Ordinal)) continue;
            string hash = match.Groups["hash"].Value.ToLowerInvariant();
            if (found is not null && !found.Equals(hash, StringComparison.Ordinal)) throw new InvalidDataException($"SHA256SUMS 为 {assetName} 提供了冲突校验值。");
            found = hash;
        }
        return found ?? throw new InvalidDataException($"SHA256SUMS 缺少 {assetName} 的标准校验行。");
    }

    private static void VerifyGitHubDigest(ReleaseAsset asset, string actualHash)
    {
        if (string.IsNullOrWhiteSpace(asset.Digest)) return;
        if (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || asset.Digest.Length != 71 || !asset.Digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException($"GitHub 资产 {asset.Name} 的 digest 格式不受支持。");
        if (!actualHash.Equals(asset.Digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"GitHub 资产 {asset.Name} 的 digest 校验失败。");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}

internal static class UpgradeHelper
{
    // Parameters are passed with ArgumentList, never interpolated into executable script.
    internal static string CreatePowerShellScript(int targetPid, string targetDirectory, string packagePath, UpdatePackageKind kind, string logPath) => """
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][ValidateSet('Installer','Portable')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory = $true)][string]$ReadyPath,
    [Parameter(Mandatory = $true)][string]$CommitPath,
    [Parameter(Mandatory = $true)][string]$Nonce,
    [int]$TestFailAfterCopy = 0
)
$ErrorActionPreference = 'Stop'
$package = $null
$archive = $null
$mutated = New-Object 'System.Collections.Generic.List[string]'
$success = $false
function Write-UpdateLog([string]$Message) {
    try { Add-Content -LiteralPath $LogPath -Value ((Get-Date -Format o) + ' ' + $Message) } catch { }
}
function Assert-NoLinks([string]$Path) {
    $item = [IO.DirectoryInfo]::new($Path)
    while ($null -ne $item) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse point in update destination.' }
        $item = $item.Parent
    }
}
function Package-Hash {
    $package.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($package))).Replace('-', '') } finally { $sha.Dispose(); $package.Position = 0 }
}
try {
    $TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
    Assert-NoLinks $TargetDirectory
    if (-not [IO.Directory]::Exists($TargetDirectory)) { throw 'Application directory is missing.' }
    $stage = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PackagePath))
    if ($stage.StartsWith($TargetDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or $stage -eq $TargetDirectory) { throw 'Staging must be outside application directory.' }
    Assert-NoLinks $stage
    $work = Join-Path $stage ('work-' + [guid]::NewGuid().ToString('N'))
    $extract = Join-Path $work 'payload'
    $backup = Join-Path $work 'backup'
    New-Item -ItemType Directory -Path $extract, $backup | Out-Null
    # Deny mutation/deletion from the first hash until the last use of the package.
    $package = [IO.File]::Open($PackagePath, 'Open', 'Read', 'Read')
    if ((Package-Hash) -ne $ExpectedSha256) { throw 'Update package SHA-256 mismatch.' }
    $files = New-Object 'System.Collections.Generic.List[string]'
    if ($Mode -eq 'Portable') {
        Add-Type -AssemblyName System.IO.Compression
        $archive = [IO.Compression.ZipArchive]::new($package, [IO.Compression.ZipArchiveMode]::Read, $true)
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        [long]$expanded = 0
        if ($archive.Entries.Count -gt 20000) { throw 'Too many ZIP entries.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $parts = $name.TrimEnd('/').Split('/')
            if ($name.StartsWith('/') -or $parts.Count -eq 0) { throw 'Absolute ZIP path.' }
            foreach ($part in $parts) {
                if ([string]::IsNullOrEmpty($part) -or $part -eq '.' -or $part -eq '..' -or $part -match '[<>:"|?*\x00-\x1f]' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe ZIP path.' }
            }
            if (-not $seen.Add($name.TrimEnd('/'))) { throw 'Duplicate ZIP path.' }
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'ZIP links are not allowed.' }
            $expanded += $entry.Length
            if ($expanded -gt 2147483648) { throw 'ZIP expanded size exceeds 2 GiB.' }
            $dest = Join-Path $extract ($name.Replace('/', '\'))
            if ($name.EndsWith('/')) { New-Item -ItemType Directory -Path $dest -Force | Out-Null; continue }
            if ($name -match '(^|/)(rules\.json|settings\.json|config\.json|host\.log(?:\.\d+)?|unins[^/]*)(/|$)') { throw 'Package attempts to overwrite local configuration or uninstaller.' }
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($dest)) -Force | Out-Null
            $inputStream = $entry.Open()
            $output = [IO.File]::Open($dest, 'CreateNew', 'Write', 'None')
            try { $inputStream.CopyTo($output) } finally { $inputStream.Dispose(); $output.Dispose() }
            $files.Add($name.Replace('/', '\'))
        }
        $archive.Dispose(); $archive = $null
        if (-not $files.Contains('LanBridge.App.exe') -or -not $files.Contains('LanBridge.Host.exe')) { throw 'Portable ZIP must have App and Host at its root.' }
        foreach ($relative in $files) {
            $dest = Join-Path $TargetDirectory $relative
            Assert-NoLinks ([IO.Path]::GetDirectoryName($dest))
            if ([IO.File]::Exists($dest)) {
                if ((Get-Item -LiteralPath $dest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked destination file.' }
                $probe = [IO.File]::Open($dest, 'Open', 'Read', 'ReadWrite, Delete'); $probe.Dispose()
                $save = Join-Path $backup $relative
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($save)) -Force | Out-Null
                [IO.File]::Copy($dest, $save, $false)
            } elseif ([IO.Directory]::Exists($dest)) { throw 'Directory conflicts with update file.' }
        }
    }
    [IO.File]::WriteAllText($ReadyPath, $Nonce)
    # Readiness is not permission to install. Only the live UI may commit after Host shutdown.
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    while (-not [IO.File]::Exists($CommitPath)) {
        if (-not (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue)) { throw 'Application exited without update authorization.' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Update authorization timeout.' }
        Start-Sleep -Milliseconds 100
    }
    if ([IO.File]::ReadAllText($CommitPath) -ne $Nonce) { throw 'Invalid update authorization.' }
    Write-UpdateLog ('waiting for app PID ' + $TargetPid)
    if (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) { Wait-Process -Id $TargetPid -Timeout 180 }
    if (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) { throw 'Application did not exit.' }
    if ((Package-Hash) -ne $ExpectedSha256) { throw 'Update package SHA-256 mismatch before apply.' }
    if ($Mode -eq 'Installer') {
        # Normal interactive Inno wizard: NOT a transactional unattended install.
        # It may change shortcuts/uninstall registration; cancellation/failure is reported honestly.
        $package.Dispose(); $package = $null
        $arguments = @('/DIR=' + ('"' + $TargetDirectory + '"'), '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS')
        $installer = Start-Process -FilePath $PackagePath -ArgumentList $arguments -WorkingDirectory $TargetDirectory -PassThru -Wait
        if ($installer.ExitCode -ne 0) { throw ('Installer canceled/failed: ' + $installer.ExitCode + '; no transactional rollback is promised. Use the installer to repair if needed.') }
    } else {
        foreach ($relative in $files) {
            $dest = Join-Path $TargetDirectory $relative
            Assert-NoLinks ([IO.Path]::GetDirectoryName($dest))
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($dest)) -Force | Out-Null
            $mutated.Add($relative)
            [IO.File]::Copy((Join-Path $extract $relative), $dest, $true)
            if ($TestFailAfterCopy -gt 0 -and $mutated.Count -eq $TestFailAfterCopy) { throw 'Injected apply failure.' }
        }
    }
    $app = Join-Path $TargetDirectory 'LanBridge.App.exe'
    $alreadyRunning = Get-Process -Name 'LanBridge.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app }
    if (-not $alreadyRunning) { Start-Process -FilePath $app -WorkingDirectory $TargetDirectory | Out-Null }
    $success = $true
    Write-UpdateLog 'upgrade completed; application restart requested'
    exit 0
} catch {
    Write-UpdateLog ('upgrade failed: ' + $_.Exception.Message)
    $rollbackFailed = $false
    for ($i = $mutated.Count - 1; $i -ge 0; $i--) {
        $relative = $mutated[$i]
        try {
            $dest = Join-Path $TargetDirectory $relative
            $save = Join-Path $backup $relative
            if ([IO.File]::Exists($save)) { [IO.File]::Copy($save, $dest, $true) }
            elseif ([IO.File]::Exists($dest)) { [IO.File]::Delete($dest) }
        } catch { $rollbackFailed = $true; Write-UpdateLog ('ROLLBACK FAILED: ' + $relative + ': ' + $_.Exception.Message) }
    }
    if ($mutated.Count -gt 0 -and -not $rollbackFailed) { Write-UpdateLog 'portable rollback completed' }
    Write-UpdateLog ('Recovery files retained at ' + $work)
    exit 1
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $package) { $package.Dispose() }
    if ($success -and $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
""";

    internal sealed class PreparedUpgrade : IDisposable
    {
        private readonly Process _process;
        private readonly string _commitPath;
        private readonly string _nonce;
        private bool _committed;
        internal PreparedUpgrade(Process process, string commitPath, string nonce) => (_process, _commitPath, _nonce) = (process, commitPath, nonce);
        internal void Commit()
        {
            if (_process.HasExited) throw new IOException("升级助手已退出；未授权安装。");
            File.WriteAllText(_commitPath, _nonce);
            _committed = true;
        }
        public void Dispose()
        {
            if (!_committed) { try { if (!_process.HasExited) _process.Kill(); } catch { } }
            _process.Dispose();
        }
    }

    internal static async Task<PreparedUpgrade> PrepareAsync(StagedUpdate update, int targetPid, string targetExecutablePath,
        CancellationToken cancellationToken = default, TimeSpan? readinessTimeout = null, string? powershellPath = null)
    {
        if (targetPid <= 0) throw new ArgumentOutOfRangeException(nameof(targetPid));
        string targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetExecutablePath))!;
        string stage = Path.GetDirectoryName(Path.GetFullPath(update.PackagePath))!;
        string nonce = Guid.NewGuid().ToString("N");
        string scriptPath = Path.Combine(stage, "apply-" + nonce + ".ps1");
        string readyPath = Path.Combine(stage, nonce + ".ready");
        string commitPath = Path.Combine(stage, nonce + ".commit");
        if (update.ExpectedSha256.Length != 64 || !update.ExpectedSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("无效更新校验值。");
        // Verify again locally before even launching a helper. The helper pins and rechecks the bytes.
        await using (var package = new FileStream(update.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken));
            if (!hash.Equals(update.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包已更改，未启动升级。");
        }
        await File.WriteAllTextAsync(scriptPath, CreatePowerShellScript(targetPid, targetDirectory, update.PackagePath, update.Kind, ""), new UTF8Encoding(false), cancellationToken);
        string powershell = powershellPath ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new FileNotFoundException("找不到 Windows PowerShell，未启动升级。", powershell);
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-TargetPid", targetPid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-TargetDirectory", targetDirectory,
            "-PackagePath", update.PackagePath, "-Mode", update.Kind.ToString(), "-LogPath", Path.Combine(stage, "upgrade.log"),
            "-ExpectedSha256", update.ExpectedSha256, "-ReadyPath", readyPath, "-CommitPath", commitPath, "-Nonce", nonce }) start.ArgumentList.Add(argument);
        Process process = Process.Start(start) ?? throw new IOException("未能启动升级助手。");
        var prepared = new PreparedUpgrade(process, commitPath, nonce);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(readinessTimeout ?? TimeSpan.FromSeconds(60));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("升级助手校验或准备失败。请查看 " + Path.Combine(stage, "upgrade.log"));
                if (File.Exists(readyPath) && await File.ReadAllTextAsync(readyPath, deadline.Token) == nonce) return prepared;
                await Task.Delay(100, deadline.Token);
            }
        }
        catch { prepared.Dispose(); throw; }
    }
}
