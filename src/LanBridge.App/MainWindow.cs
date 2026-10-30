using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LanBridge.Control;
using LanBridge.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Windows.ApplicationModel.DataTransfer;

namespace LanBridge.App;

internal sealed partial class MainWindow : Window
{
    private readonly BridgeClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Grid _root = new() { Padding = new Thickness(24), RowSpacing = 12 };
    private readonly ListView _list = (ListView)XamlReader.Load("<ListView xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' SelectionMode='Single' IsMultiSelectCheckBoxEnabled='False' IsItemClickEnabled='False' />");
    // Native checkbox glyphs are centered; keep their labels centered too, rather than
    // relying on the default top-aligned content and its compensating top padding.
    private readonly CheckBox _batchMode = new()
    {
        Content = "批量选择", MinWidth = 0, Padding = new Thickness(8, 0, 0, 0),
        HorizontalContentAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly CheckBox _selectAll = new()
    {
        Content = "全选", IsThreeState = true, MinWidth = 0, Padding = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Grid _columnHeader = RuleColumns(new Thickness(28, 8, 28, 8));
    private readonly TextBlock _selectionCount = Text("已选 0 条", 13);
    private readonly TextBlock _connection = Text("正在连接后台…", 13);
    private readonly Grid _connectionRow = new() { ColumnSpacing = 12 };
    private readonly TextBlock _empty = Text("尚无转发规则\n添加一条规则，将本机端口连接到虚拟机、WSL 发行版或容器。", 16);
    private readonly TextBlock _summary = SecondaryText("转发规则", 14);
    private readonly SystemTheme _theme;
    private readonly InfoBar _notice = new() { IsClosable = true };
    private readonly ProgressRing _progress = new() { Width = 18, Height = 18, IsActive = false };
    private readonly Button _add = Button("添加规则", Symbol.Add);
    private readonly Button _edit = Button("编辑", Symbol.Edit);
    private readonly Button _toggle = Button("启动", Symbol.Play);
    private readonly MenuFlyoutItem _delete = new() { Text = "删除规则", Icon = new SymbolIcon(Symbol.Delete) };
    private readonly MenuFlyoutItem _copy = new() { Text = "复制访问地址", Icon = new SymbolIcon(Symbol.Copy) };
    private readonly MenuFlyoutItem _probe = new() { Text = "测试目标 TCP", Icon = new SymbolIcon(Symbol.Find) };
    private readonly Button _more = Button("更多", Symbol.More);
    private readonly Button _batch = Button("批量操作", Symbol.More);

    private enum BatchAction { Start, Stop, Delete }

    private readonly Dictionary<Guid, (ForwardRule Rule, TextBlock State, TextBlock Detail)> _rows = new();
    private BridgeState? _state;
    private string? _ruleSignature;
    private bool _connected;
    private bool _busy;
    private bool _dialogOpen;
    private bool _closed;
    private bool _allowClose;
    private DateTimeOffset _nextConnectAttempt;
    private readonly AppLifecycle _exitLifecycle = new();
    private ManagedRuleOperations Operations => new(_client.SaveRuleAsync, _client.DeleteRuleAsync, PrivilegedOperations.SetFirewallAsync, _client.GetStateAsync);

    public MainWindow()
    {
        Title = "LanBridge · 局域网端口转发";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "LanBridge.ico"));
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 600;
        }
        BuildLayout();
        Content = _root;
        _theme = new SystemTheme(_root, this);
        _list.SelectionChanged += (_, _) => UpdateActions();
        _batchMode.Checked += (_, _) => SetBatchMode(true);
        _batchMode.Unchecked += (_, _) => SetBatchMode(false);
        _selectAll.Click += (_, _) =>
        {
            if (_list.Items.Count == 0) return;
            if (_list.SelectedItems.Count == _list.Items.Count) _list.SelectedItems.Clear();
            else _list.SelectAll();
            UpdateActions();
        };
        _add.Click += async (_, _) => await EditRuleAsync(null);

        _edit.Click += async (_, _) => { if (Selected is { } rule) await EditRuleAsync(rule); };
        _toggle.Click += async (_, _) =>
        {
            if (Selected is not { } rule) return;
            await MutateAsync(() => Operations.SaveAsync(rule with { Enabled = !rule.Enabled }, rule, _lifetime.Token), rule.Enabled ? "规则已停止" : "已提交启动请求；请查看监听状态");
        };
        _delete.Click += async (_, _) => await DeleteSelectedAsync();
        _copy.Click += async (_, _) => await CopySelectedAsync();
        _probe.Click += async (_, _) => await ProbeSelectedAsync();
        var moreMenu = new MenuFlyout();
        moreMenu.Items.Add(_copy);
        moreMenu.Items.Add(_probe);
        moreMenu.Items.Add(new MenuFlyoutSeparator());
        moreMenu.Items.Add(_delete);
        _more.Flyout = moreMenu;
        var batchMenu = new MenuFlyout();
        var batchStart = new MenuFlyoutItem { Text = "批量启动" };
        var batchStop = new MenuFlyoutItem { Text = "批量停止" };
        var batchDelete = new MenuFlyoutItem { Text = "批量删除" };
        batchStart.Click += async (_, _) => await BatchSelectedAsync(BatchAction.Start);
        batchStop.Click += async (_, _) => await BatchSelectedAsync(BatchAction.Stop);
        batchDelete.Click += async (_, _) => await BatchSelectedAsync(BatchAction.Delete);
        batchMenu.Items.Add(batchStart); batchMenu.Items.Add(batchStop); batchMenu.Items.Add(batchDelete);
        _batch.Flyout = batchMenu;

        _timer.Tick += async (_, _) => await RefreshAsync();
        _root.Loaded += async (_, _) => { await ConnectAsync(true); if (!_closed) _timer.Start(); };
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => { _closed = true; _timer.Stop(); _lifetime.Cancel(); _theme.Dispose(); };
        UpdateActions();
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        sender.Hide();
    }

    internal void ShowFromTray()
    {
        if (_closed) return;
        AppWindow.Show();
        Activate();
    }

    internal Task ExitAsync() => _exitLifecycle.ShutdownAndExitAsync(
        ShutdownBackendAsync,
        () =>
        {
            _allowClose = true;
            _timer.Stop();
            Close();
        },
        message =>
        {
            ShowFromTray();
            ShowNotice("退出失败", message, InfoBarSeverity.Error);
        });

    private async Task ShutdownBackendAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ShutdownAsync(cancellationToken);
        }
        catch (TimeoutException original) when (IsBackendConnectionTimeout(original))
        {
            if (await IsBackendAbsentAsync(cancellationToken)) return;
            throw;
        }
        catch (ArgumentException)
        {
            // The exact PID may have exited between the state read and handle pinning.
            // Verify absence; do not swallow permission, protocol, or unknown-API failures.
            if (await IsBackendAbsentAsync(cancellationToken)) return;
            throw;
        }
    }

    private async Task<bool> IsBackendAbsentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetStateAsync(cancellationToken);
            return false;
        }
        catch (TimeoutException verification) when (IsBackendConnectionTimeout(verification))
        {
            return true;
        }
    }

    private static bool IsBackendConnectionTimeout(TimeoutException exception) =>
        exception.Message.Contains("无法连接 LanBridge 后端或请求超时", StringComparison.Ordinal);

    private ForwardRule? Selected => _list.SelectedItems.Count == 1
        ? (_list.SelectedItems[0] as ListViewItem)?.Tag as ForwardRule : null;

    private void BuildLayout()
    {
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            _root.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new Grid { ColumnSpacing = 20 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Text("LanBridge", 24, true));
        _summary.VerticalAlignment = VerticalAlignment.Center;

        title.Children.Add(_summary);
        header.Children.Add(title);
        _add.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _add.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_add, 1); header.Children.Add(_add);
        Place(header, 0);

        _connectionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _connectionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _connectionRow.Children.Add(_progress);
        _connection.VerticalAlignment = VerticalAlignment.Center;
        _connection.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_connection, 1); _connectionRow.Children.Add(_connection);

        Place(_connectionRow, 1);
        Place(_notice, 2);

        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var selection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_batchMode, "批量选择规则");
        ToolTipService.SetToolTip(_batchMode, "默认单选；开启后显示选择框，可同时选择多条规则进行批量操作。");

        AutomationProperties.SetName(_selectAll, "全选转发规则");
        ToolTipService.SetToolTip(_selectAll, "勾选全部规则；再次点击取消全部选择。部分选中时显示横线。");
        _selectAll.Visibility = Visibility.Collapsed;
        _selectionCount.Visibility = Visibility.Collapsed;
        _selectionCount.VerticalAlignment = VerticalAlignment.Center;
        selection.Children.Add(_selectAll); selection.Children.Add(_selectionCount); selection.Children.Add(_batch); selection.Children.Add(_batchMode);
        Grid.SetColumn(selection, 1); toolbar.Children.Add(selection);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { _toggle, _edit, _more }) commands.Children.Add(button);
        toolbar.Children.Add(commands);
        Place(toolbar, 3);

        var listContainer = new Grid { RowSpacing = 6 };
        listContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var columns = _columnHeader;
        string[] labels = new[] { "规则", "本机监听", "转发目标", "状态" };
        for (int index = 0; index < labels.Length; index++)
        {
            var label = SecondaryText(labels[index], 12);
            Grid.SetColumn(label, index); columns.Children.Add(label);
        }
        listContainer.Children.Add(columns);
        _list.BorderThickness = new Thickness(1);

        _list.CornerRadius = new CornerRadius(8);

        AutomationProperties.SetName(_list, "转发规则列表");
        AutomationProperties.SetHelpText(_list, "默认单选，点击编辑按钮修改。开启批量选择后，勾选规则或点击全选，无需 Ctrl 或 Shift。");
        Grid.SetRow(_list, 1);
        listContainer.Children.Add(_list);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.TextAlignment = TextAlignment.Center;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.Margin = new Thickness(24);
        _empty.IsHitTestVisible = false;
        Grid.SetRow(_empty, 1);
        listContainer.Children.Add(_empty);
        Place(listContainer, 4);
        ToolTipService.SetToolTip(title, "局域网端口转发。关闭窗口后继续在托盘运行；从托盘菜单退出可停止后台。");
        ToolTipService.SetToolTip(columns, "监听中表示本机端口已开启，不代表目标可达。防火墙授权在编辑规则时设置。");
    }

    private void Place(FrameworkElement element, int row) { Grid.SetRow(element, row); _root.Children.Add(element); }
    internal static TextBlock Text(string text, double size = 14, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false
    };
    internal static TextBlock SecondaryText(string text, double size = 12)
    {
        var block = (TextBlock)XamlReader.Load("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{ThemeResource TextFillColorSecondaryBrush}' TextWrapping='Wrap' />");
        block.Text = text; block.FontSize = size;
        return block;
    }
    internal static Button Button(string label, Symbol symbol)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        panel.Children.Add(new SymbolIcon(symbol) { Width = 16, Height = 16 });
        panel.Children.Add(Text(label));
        var button = new Button { Content = panel };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }

    private void UpdateActions()
    {
        var rule = Selected;
        bool ready = _connected && !_busy && !_dialogOpen;
        int selectedCount = _list.SelectedItems.Count;
        bool batchMode = _batchMode.IsChecked == true;
        _batchMode.IsEnabled = ready;
        _selectionCount.Visibility = batchMode ? Visibility.Visible : Visibility.Collapsed;
        _batch.Visibility = batchMode ? Visibility.Visible : Visibility.Collapsed;
        _list.IsEnabled = ready;
        _selectAll.IsEnabled = ready && _list.Items.Count > 0;
        _selectAll.IsChecked = selectedCount == 0 ? false : selectedCount == _list.Items.Count ? true : null;
        _selectionCount.Text = $"已选 {selectedCount} 条";
        _add.IsEnabled = ready;

        _edit.IsEnabled = _delete.IsEnabled = _toggle.IsEnabled = ready && rule is not null;
        _copy.IsEnabled = ready && rule is not null;
        _more.IsEnabled = ready && rule is not null;
        _probe.IsEnabled = ready && rule?.Protocol is (TransportProtocol.Tcp or TransportProtocol.TcpUdp);
        _batch.IsEnabled = ready && _list.SelectedItems.Count > 1;

        _toggle.Content = rule?.Enabled == true ? "停止" : "启动";
        AutomationProperties.SetName(_toggle, rule?.Enabled == true ? "停止选中规则" : "启动选中规则");
        _progress.IsActive = _busy;
    }

    private void SetBatchMode(bool enabled)
    {
        if (!enabled) _list.SelectedItems.Clear();
        _list.SelectionMode = enabled ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
        _list.IsMultiSelectCheckBoxEnabled = enabled;
        _columnHeader.Padding = new Thickness(enabled ? 56 : 28, 8, 28, 8);
        _selectAll.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    private async Task ConnectAsync(bool launchIfMissing)
    {
        if (_busy || _closed || _exitLifecycle.IsExitRequested || !await _requests.WaitAsync(0)) return;
        _nextConnectAttempt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        SetBusy(true);
        try
        {
            try { Apply(await _client.GetStateAsync(_lifetime.Token)); return; }
            catch (OperationCanceledException) when (_closed) { return; }
            catch (Exception first) when (launchIfMissing)
            {
                _connection.Text = "正在启动当前用户的后台进程…";
                string path = Path.Combine(AppContext.BaseDirectory, "LanBridge.Host.exe");
                if (!File.Exists(path)) throw new FileNotFoundException($"无法连接后台：{first.Message}\n未找到 {path}。请将后台程序与桌面程序放在同一目录。", path);
                using var process = Process.Start(HostLauncher.CreateStartInfo(AppContext.BaseDirectory));
                Exception last = first;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    await Task.Delay(350, _lifetime.Token);
                    try { Apply(await _client.GetStateAsync(_lifetime.Token)); return; }
                    catch (OperationCanceledException) when (_closed) { return; }
                    catch (Exception ex) { last = ex; }
                }
                throw new InvalidOperationException($"后台未能就绪：{last.Message}。请检查权限、配置文件和后台日志。", last);
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { Disconnected(ex.Message); ShowNotice("连接失败", ex.Message, InfoBarSeverity.Error); }
        finally { SetBusy(false); _requests.Release(); }
    }

    private async Task RefreshAsync()
    {
        if (_closed || _exitLifecycle.IsExitRequested || _dialogOpen || _busy) return;
        if (!_connected)
        {
            if (DateTimeOffset.UtcNow >= _nextConnectAttempt) await ConnectAsync(true);
            return;
        }
        if (_closed || _dialogOpen || !await _requests.WaitAsync(0)) return;
        try { Apply(await _client.GetStateAsync(_lifetime.Token)); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { Disconnected(ex.Message); }
        finally { _requests.Release(); }
    }

    private void Apply(BridgeState state)
    {
        if (_closed || _exitLifecycle.IsExitRequested) return;
        _state = state; _connected = true;
        _connectionRow.Visibility = Visibility.Collapsed;
        _empty.Text = "尚无转发规则\n添加一条规则，将本机端口连接到虚拟机、WSL 发行版或容器。";
        if (_notice.Title == "连接失败") _notice.IsOpen = false;
        ToolTipService.SetToolTip(_connection, $"后台日志：{state.LogPath}");
        string signature = System.Text.Json.JsonSerializer.Serialize(state.Rules);
        if (_ruleSignature != signature)
        {
            var selected = _list.SelectedItems.OfType<ListViewItem>()
                .Select(item => (item.Tag as ForwardRule)?.Id).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
            _list.Items.Clear(); _rows.Clear();
            foreach (var rule in state.Rules)
            {
                var row = CreateRow(rule);
                _list.Items.Add(row);
                if (selected.Contains(rule.Id)) _list.SelectedItems.Add(row);
            }
            _ruleSignature = signature;
        }
        foreach (var entry in _rows.Values)
        {
            var status = state.Statuses.FirstOrDefault(s => s.Id == entry.Rule.Id);
            entry.State.Text = UiPresentation.Status(true, entry.Rule.Enabled, status?.Running == true, status?.Error);
            entry.Detail.Text = status?.ActiveConnections > 0 ? $"{status.ActiveConnections} 个连接" : "";
            entry.Detail.Visibility = status?.ActiveConnections > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(entry.State, status is null ? "尚无运行数据" :
                $"{status.ActiveConnections} 个连接 / 会话   ↑ {Size(status.BytesSent)}   ↓ {Size(status.BytesReceived)}");
        }
        _empty.Visibility = state.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = $"转发规则（{state.Rules.Count}）";
        UpdateActions();
    }

    private static Grid RuleColumns(Thickness padding)
    {
        var grid = new Grid { Padding = padding, ColumnSpacing = 16 };
        foreach (double width in new[] { 1d, 1.35, 1.35, 0.8 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        return grid;
    }

    private ListViewItem CreateRow(ForwardRule rule)
    {
        var grid = RuleColumns(new Thickness(12, 12, 12, 12));
        var name = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Text(rule.Name, 14, true));
        var protocol = SecondaryText(UiPresentation.ProtocolLabel(rule.Protocol), 12);
        name.Children.Add(protocol);
        grid.Children.Add(name);
        var listen = Text(UiPresentation.Endpoint(rule.ListenAddress, rule.ListenPort));
        listen.VerticalAlignment = VerticalAlignment.Center;
        var target = Text(UiPresentation.Endpoint(rule.TargetHost, rule.TargetPort));
        target.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(listen, 1); grid.Children.Add(listen);
        Grid.SetColumn(target, 2); grid.Children.Add(target);
        var runtime = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var status = Text("等待状态", 14);
        var detail = SecondaryText("", 12); runtime.Children.Add(status); runtime.Children.Add(detail);
        Grid.SetColumn(runtime, 3); grid.Children.Add(runtime);
        _rows.Add(rule.Id, (rule, status, detail));
        var item = new ListViewItem { Content = grid, Tag = rule, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, $"{rule.Name}，{rule.Protocol}，本机 {rule.ListenAddress} {rule.ListenPort}，目标 {rule.TargetHost} {rule.TargetPort}");
        return item;
    }

    private static string Size(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:F1} MB" : bytes >= 1024 ? $"{bytes / 1024d:F1} KB" : $"{bytes} B";
    private void Disconnected(string reason)
    {
        if (_closed) return;
        _connected = false;
        _connectionRow.Visibility = Visibility.Visible;
        _connection.Text = $"后台已断开：{reason}";
        foreach (var row in _rows.Values) { row.State.Text = "已断开"; row.Detail.Text = "状态未知；重新连接后更新"; }
        if (_state is null) _empty.Text = "后台未连接\n正在自动重试；请确认后台程序与桌面程序位于同一目录。";
        UpdateActions();
    }
    private void SetBusy(bool busy) { _busy = busy; UpdateActions(); }
    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        if (_closed) return;
        _notice.Title = title; _notice.Message = message; _notice.Severity = severity; _notice.IsOpen = true;
    }
    private async Task MutateAsync(Func<Task> action, string message)
    {
        if (_busy || _closed) return;
        await _requests.WaitAsync(_lifetime.Token); SetBusy(true);
        bool committed = false;
        try
        {
            await action();
            committed = true;
            try
            {
                Apply(await _client.GetStateAsync(_lifetime.Token));
                ShowNotice(message, "更改已保存到后台。", InfoBarSeverity.Success);
            }
            catch (Exception refreshError)
            {
                ShowNotice("操作已提交，状态刷新失败", refreshError.Message + " 请稍后刷新确认；不要重复提交。", InfoBarSeverity.Warning);
                Disconnected(refreshError.Message);
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            ShowNotice(committed ? "操作已提交，状态刷新失败" : "操作失败", ex.Message + (committed ? " 请稍后刷新确认；不要重复提交。" : ""), committed ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
            if (!committed) try { Apply(await _client.GetStateAsync(_lifetime.Token)); } catch (Exception refreshError) { Disconnected(refreshError.Message); }
        }
        finally { SetBusy(false); _requests.Release(); }
    }

    private async Task EditRuleAsync(ForwardRule? rule)
    {
        if (_dialogOpen || !_connected || _busy) return;
        _dialogOpen = true; UpdateActions();
        try
        {
            var dialog = new RuleEditorDialog(_client, rule, _lifetime.Token) { XamlRoot = _root.XamlRoot };
            await dialog.ShowAsync();
            if (dialog.Saved) { await RefreshAfterDialogAsync(); ShowNotice("规则已保存", "查看列表中的监听状态，确认端口是否成功开启。", InfoBarSeverity.Success); }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { ShowNotice("无法打开编辑器", ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; UpdateActions(); }
    }
    private async Task RefreshAfterDialogAsync()
    {
        await _requests.WaitAsync(_lifetime.Token);
        try { Apply(await _client.GetStateAsync(_lifetime.Token)); }
        finally { _requests.Release(); }
    }

    private async Task BatchSelectedAsync(BatchAction action)
    {
        var rules = _list.SelectedItems.OfType<ListViewItem>()
            .Select(item => item.Tag as ForwardRule).Where(rule => rule is not null).Cast<ForwardRule>().ToArray();
        if (rules.Length < 2 || _dialogOpen || _busy) return;

        _dialogOpen = true; UpdateActions();
        try
        {
            if (action == BatchAction.Delete)
            {
                string names = string.Join("\n", rules.Select(rule => "• " + rule.Name));
                var confirm = new ContentDialog
                {
                    XamlRoot = _root.XamlRoot,
                    Title = $"批量删除 {rules.Length} 条规则？",
                    Content = $"选中规则将停止转发并从配置中删除。自动管理防火墙的规则会逐条请求 UAC 删除自建许可。\n\n{names}",
                    PrimaryButtonText = "删除全部", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }

            string successMessage = action switch
            {
                BatchAction.Start => $"已提交启动 {rules.Length} 条规则",
                BatchAction.Stop => $"已停止 {rules.Length} 条规则",
                _ => $"已删除 {rules.Length} 条规则"
            };
            await MutateAsync(async () =>
            {
                int completed = 0;
                foreach (var rule in rules)
                {
                    try
                    {
                        if (action == BatchAction.Delete) await Operations.DeleteAsync(rule, _lifetime.Token);
                        else await Operations.SaveAsync(rule with { Enabled = action == BatchAction.Start }, rule, _lifetime.Token);
                        completed++;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"已处理 {completed}/{rules.Length} 条；规则“{rule.Name}”操作失败。{ex.Message}", ex);
                    }
                }
            }, successMessage);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { ShowNotice("批量操作失败", ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; UpdateActions(); }
    }

    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } rule || _dialogOpen) return;
        _dialogOpen = true; UpdateActions();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot, Title = "删除规则？", Content = $"“{rule.Name}”将停止转发并从配置中删除。{(rule.AutoFirewall ? "同时请求 UAC 删除本规则自建的防火墙许可。" : "")}此操作无法撤销。",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await MutateAsync(() => Operations.DeleteAsync(rule, _lifetime.Token), "规则已删除");
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { ShowNotice("删除失败", ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; UpdateActions(); }
    }

    private async Task ProbeSelectedAsync()
    {
        if (Selected is not { Protocol: TransportProtocol.Tcp } rule || _busy) return;
        await _requests.WaitAsync(_lifetime.Token); SetBusy(true);
        try { ShowNotice("目标 TCP 测试", UiPresentation.ProbeOutcome(await _client.ProbeAsync(rule, _lifetime.Token)), InfoBarSeverity.Informational); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception) { ShowNotice("目标 TCP 测试未成功", "请确认目标服务已启动，且目标地址和端口正确。", InfoBarSeverity.Warning); }
        finally { SetBusy(false); _requests.Release(); }
    }

    private async Task CopySelectedAsync()
    {
        if (Selected is not { } rule || _dialogOpen) return;
        try
        {
            string address = rule.ListenAddress;
            if (IPAddress.TryParse(address, out var parsed) && (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any)))
            {
                var endpoints = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == parsed.AddressFamily && !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal)
                    .Select(a => UiPresentation.Endpoint(a.ToString(), rule.ListenPort)).Distinct().ToList();
                if (endpoints.Count == 0) { ShowNotice("没有可复制的局域网地址", "未发现当前网络接口地址。请连接网络后重试。", InfoBarSeverity.Warning); return; }
                _dialogOpen = true; UpdateActions();
                var choice = new ComboBox { ItemsSource = endpoints, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Header = "本机网络接口地址" };
                AutomationProperties.SetName(choice, "选择要复制的访问地址");
                var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(choice);
                panel.Children.Add(Text("请选择局域网设备能够访问的接口。虚拟网卡或 VPN 地址不一定可达；仍需允许防火墙端口。", 13));
                var dialog = new ContentDialog { XamlRoot = _root.XamlRoot, Title = "复制访问地址", Content = panel, PrimaryButtonText = "复制", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                address = (string)choice.SelectedItem;
            }
            else address = UiPresentation.Endpoint(address, rule.ListenPort);
            var data = new DataPackage(); data.SetText(address); Clipboard.SetContent(data); Clipboard.Flush();
            bool loopback = IPAddress.TryParse(rule.ListenAddress, out var ip) && IPAddress.IsLoopback(ip);
            ShowNotice("地址已复制", $"{address}{(loopback ? "。这是回环地址，仅本机可访问；如需局域网访问，请编辑监听地址。" : "。访问仍取决于网络路由与防火墙。")}", loopback ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("复制失败", ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; UpdateActions(); }
    }
}
