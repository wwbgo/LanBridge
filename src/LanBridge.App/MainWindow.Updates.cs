using System.Reflection;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LanBridge.App;

internal sealed partial class MainWindow
{
    private readonly GitHubUpdateClient _updateClient = new();

    private Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version is { } version
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
        : new Version(0, 1, 0);

    private async Task CheckUpdatesAsync()
    {
        if (_dialogOpen || _closed || _exitLifecycle.IsExitRequested) return;
        _dialogOpen = true;
        UpdateActions();
        try
        {
            var current = CurrentVersion;
            var release = await _updateClient.CheckAsync(current, _lifetime.Token);
            if (release is null)
            {
                ShowNotice("已是最新版本", $"当前版本 v{current}；GitHub 稳定版没有更新。", InfoBarSeverity.Success);
                return;
            }

            var packageChoice = new ComboBox
            {
                Header = "升级方式",
                ItemsSource = new[] { "安装向导（会创建卸载项、开始菜单及桌面快捷方式）", "便携 ZIP（失败自动恢复应用文件）" },
                SelectedIndex = Directory.GetFiles(AppContext.BaseDirectory, "unins*.exe").Length > 0 ? 0 : 1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var notes = new TextBox
            {
                Header = "发布说明",
                Text = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "（此 Release 没有发布说明）" : release.ReleaseNotes,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MaxHeight = 220,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(MainWindow.Text($"发现稳定版本 v{release.Version}（当前 v{current}）。", 15, true));
            content.Children.Add(MainWindow.Text("更新包只来自 wwbgo/LanBridge 的 HTTPS GitHub Release，并在运行前校验 SHA-256。升级不会自动执行；确认后会先协调后台退出，再启动一次性升级助手。", 13));
            content.Children.Add(packageChoice);
            content.Children.Add(notes);
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = $"发现 LanBridge v{release.Version}",
                Content = content,
                PrimaryButtonText = "下载更新",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            UpdatePackageKind kind = packageChoice.SelectedIndex == 1 ? UpdatePackageKind.Portable : UpdatePackageKind.Installer;
            string staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge", "updates", Guid.NewGuid().ToString("N"));
            var downloader = new UpdateDownloader(new HttpClient(), maxDuration: UpdateEndpoints.DefaultDownloadTimeout);
            StagedUpdate staged;
            try
            {
                staged = await downloader.DownloadAndStageAsync(release, kind, staging, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_closed) { return; }
            catch (Exception ex)
            {
                ShowNotice("更新下载失败", ex.Message + " 未安装任何文件。", InfoBarSeverity.Error);
                return;
            }

            string modeNotice = kind == UpdatePackageKind.Installer
                ? "将打开普通交互式安装向导，默认指向当前应用目录；可取消。安装会创建卸载项、开始菜单和桌面快捷方式（便携版也一样）。安装向导不是事务更新：失败可能需要重新运行安装程序修复，不承诺完整回滚。"
                : "便携包会先在更新暂存目录解压并备份应用文件，再覆盖当前目录；复制失败会恢复旧文件，恢复失败会保留备份和日志供修复。用户规则配置不会更改。";
            var confirm = new ContentDialog
            {
                XamlRoot = _root.XamlRoot,
                Title = "确认升级？",
                Content = MainWindow.Text($"已下载并校验 v{release.Version}。\n\n{modeNotice}\n\n应用会先请求当前用户后台正常退出；若后台退出失败，升级不会启动。确认后当前窗口将关闭，升级助手会在应用进程退出后运行。", 14),
                PrimaryButtonText = "确认升级并退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            string appPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前应用程序路径。");
            using var prepared = await UpgradeHelper.PrepareAsync(staged, Environment.ProcessId, appPath, _lifetime.Token);
            // Never trust the cached connection bit, and do not let the reconnect timer revive Host.
            _timer.Stop();
            try { await ShutdownBackendAsync(_lifetime.Token); }
            catch (Exception ex)
            {
                ShowNotice("后台未能安全退出", ex.Message + " 升级未获授权，也未安装更新。", InfoBarSeverity.Error);
                return;
            }

            prepared.Commit();
            _allowClose = true;
            Close();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            ShowNotice("检查更新失败", ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _dialogOpen = false;
            if (!_closed) { _timer.Start(); UpdateActions(); }
        }
    }
}