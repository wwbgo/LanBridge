using System.Globalization;
using System.Net.NetworkInformation;
using LanBridge.Control;
using LanBridge.Core;
using LanBridge.Discovery;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LanBridge.App;

internal sealed class RuleEditorDialog : ContentDialog
{
    private readonly BridgeClient _client;
    private readonly ForwardRule? _original;
    private readonly CancellationToken _ct;
    private readonly TextBox _name = Field("规则名称", "例如：开发站点");
    private readonly ComboBox _protocol = new() { Header = "传输协议", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _listen = EditableField("本机监听地址", "输入或选择本机 IP");
    private readonly ComboBox _listenPort = PortField("本机端口");
    private readonly TextBlock _listenFeedback = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly TextBox _target = Field("目标地址 / 主机名", "虚拟机、WSL 或容器地址");
    private readonly ComboBox _targetPort = PortField("目标端口");
    private readonly ToggleSwitch _enabled = new() { Header = "保存后启用", OnContent = "启用", OffContent = "停用" };
    private readonly CheckBox _autoFirewall = new() { Content = "自动管理防火墙（专用网络 / 局域网）" };
    private readonly InfoBar _feedback = new() { IsClosable = true, MaxWidth = 440 };
    private readonly Flyout _feedbackFlyout = new();

    private readonly ComboBox _source = new() { Header = "目标来源", HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly ComboBox _machines = new() { Header = "发现的地址", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Button _discover = MainWindow.Button("发现目标", Symbol.Find);
    private readonly Button _probe = MainWindow.Button("测试目标 TCP", Symbol.Play);
    private bool _working;
    public bool Saved { get; private set; }

    public RuleEditorDialog(BridgeClient client, ForwardRule? original, CancellationToken ct)
    {
        _client = client; _original = original; _ct = ct;
        Title = original is null ? "添加转发规则" : "编辑转发规则";
        PrimaryButtonText = "保存规则"; CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        MinWidth = 520;
        Resources["ContentDialogMaxWidth"] = 620d;
        var form = new StackPanel { Spacing = 12, MinWidth = 440, MaxWidth = 540 };
        _protocol.Items.Add("TCP"); _protocol.Items.Add("UDP"); _protocol.Items.Add("TCP/UDP");
        _protocol.SelectedIndex = RuleEditorOptions.ProtocolIndex(original?.Protocol ?? TransportProtocol.Tcp);
        form.Children.Add(Pair(_name, _protocol));
        form.Children.Add(MainWindow.Text("本机监听", 14, true));
        form.Children.Add(Pair(_listen, _listenPort));
        AutomationProperties.SetName(_listenFeedback, "本机端口检查结果");
        form.Children.Add(_listenFeedback);
        form.Children.Add(MainWindow.Text("转发目标", 14, true));
        foreach (var source in Enum.GetValues<DiscoverySource>()) _source.Items.Add(UiPresentation.DiscoverySourceLabel(source));
        _source.SelectedIndex = 0;
        AutomationProperties.SetName(_source, "选择目标发现来源");
        _discover.VerticalAlignment = VerticalAlignment.Bottom;
        _discover.HorizontalAlignment = HorizontalAlignment.Stretch;
        form.Children.Add(Pair(_source, _discover));
        form.Children.Add(_machines);
        Help(_listen, "127.0.0.1 仅本机；0.0.0.0 允许 IPv4 网络接口访问。对局域网开放前，请确认目标服务可安全公开。");
        Help(_listenPort, "局域网设备访问的 Windows 本机端口。可选择常用端口或手动输入 1–65535。");
        Help(_target, "填写 Windows 能够访问的服务 IP 或主机名；不是局域网设备访问的本机地址。");
        Help(_targetPort, "目标服务实际监听的端口。可选择常用端口或手动输入 1–65535。");
        Help(_enabled, "启用时开始监听；停用仍保留规则供下次启动。");
        form.Children.Add(Pair(_target, _targetPort));
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        tools.Children.Add(_probe); form.Children.Add(tools);
        form.Children.Add(MainWindow.Text("启用选项", 14, true));
        _enabled.Header = null;
        _enabled.OnContent = "保存后启用";
        _enabled.OffContent = "仅保存，不启用";
        form.Children.Add(_enabled);
        form.Children.Add(_autoFirewall);
        Help(_autoFirewall, "保存、启停、删除时请求 UAC，自动同步本规则的防火墙入站许可。仅专用网络、LocalSubnet 来源；回环地址不开放。不会修改其他软件的规则。取消 UAC 不提交操作。");
        _feedbackFlyout.Content = _feedback;
        _feedback.CloseButtonClick += (_, _) => _feedbackFlyout.Hide();
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 630 };
        _name.Text = original?.Name ?? "";
        foreach (var address in RuleEditorOptions.ListenAddresses(LocalListenAddresses(), original?.ListenAddress)) _listen.Items.Add(address);
        _listen.SelectedItem = original?.ListenAddress ?? "127.0.0.1";
        _listen.Text = original?.ListenAddress ?? "127.0.0.1";
        SetPortField(_listenPort, original?.ListenPort);
        _target.Text = original?.TargetHost ?? "";
        SetPortField(_targetPort, original?.TargetPort);
        _enabled.IsOn = original?.Enabled ?? true;
        _autoFirewall.IsChecked = original?.AutoFirewall ?? false;
        AutomationProperties.SetName(_autoFirewall, "自动管理防火墙");
        AutomationProperties.SetName(_protocol, "传输协议");
        AutomationProperties.SetName(_enabled, "保存后启用规则");
        AutomationProperties.SetName(_machines, "选择发现的地址以填入目标");
        _protocol.SelectionChanged += (_, _) => UpdateProtocol();
        _source.SelectionChanged += (_, _) => SourceChanged();
        _discover.Click += async (_, _) => await DiscoverAsync();
        _probe.Click += async (_, _) => await ProbeAsync();
        _machines.SelectionChanged += (_, _) => ApplySelectedEndpoint();
        PrimaryButtonClick += SaveAsync;
        Closing += (_, args) => { if (_working) args.Cancel = true; };
        UpdateProtocol();
        SourceChanged();
    }

    private DiscoverySource SelectedSource => (DiscoverySource)Math.Clamp(_source.SelectedIndex, 0, Enum.GetValues<DiscoverySource>().Length - 1);
    private void SourceChanged()
    {
        _machines.Items.Clear();
        _machines.SelectedIndex = -1;
        _machines.Visibility = Visibility.Collapsed;
        _feedback.IsOpen = false;
        _feedbackFlyout.Hide();
        _feedback.Title = "";
        _feedback.Message = "";
        string help = UiPresentation.DiscoverySourceHelp(SelectedSource);
        ToolTipService.SetToolTip(_source, help);
        AutomationProperties.SetHelpText(_source, help);
        Help(_discover, help);
        _discover.IsEnabled = !_working && SelectedSource != DiscoverySource.Manual;
    }
    private void ApplySelectedEndpoint()
    {
        if (_machines.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string address) { _target.Text = address; return; }
        if (item.Tag is not DiscoveredEndpoint endpoint) return;
        _target.Text = endpoint.Host;
        if (endpoint.Port is int port) SetPortField(_targetPort, port);
        _protocol.SelectedIndex = RuleEditorOptions.ProtocolIndex(endpoint.Protocol);
    }
    private static TextBox Field(string name, string placeholder)
    {
        var input = new TextBox { Header = name, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(input, name);
        return input;
    }
    private static void Help(FrameworkElement control, string text)
    {
        ToolTipService.SetToolTip(control, new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } });
        AutomationProperties.SetHelpText(control, text);
    }
    private static IReadOnlyList<string> LocalListenAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address.ToString()).ToArray();
        }
        catch (NetworkInformationException)
        {
            // Adapter information can change while opening the editor. Manual entry remains available.
            return Array.Empty<string>();
        }
    }
    private static ComboBox EditableField(string name, string placeholder)
    {
        var input = new ComboBox
        {
            Header = name, PlaceholderText = placeholder, IsEditable = true,
            IsTextSearchEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(input, name);
        return input;
    }
    private static ComboBox PortField(string name)
    {
        var input = EditableField(name, "输入或选择端口");
        foreach (var choice in RuleEditorOptions.CommonPorts) input.Items.Add(choice);
        return input;
    }
    private static void SetPortField(ComboBox field, int? port)
    {
        if (port is null) return;
        string value = port.Value.ToString(CultureInfo.InvariantCulture);
        var choice = RuleEditorOptions.CommonPorts.FirstOrDefault(item => item.Port == port.Value);
        if (choice is not null)
        {
            field.SelectedItem = choice;
            field.Text = choice.ToString();
        }
        else
        {
            field.Items.Add(value);
            field.SelectedItem = value;
            field.Text = value;
        }
    }
    private static string PortValue(ComboBox field)
    {
        string text = field.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(text)) return text;
        return field.SelectedItem switch
        {
            RuleEditorOptions.PortChoice choice => choice.Port.ToString(CultureInfo.InvariantCulture),
            string value => value.Trim(),
            _ => ""
        };
    }
    private static Grid Pair(FrameworkElement address, FrameworkElement port)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.Children.Add(address); Grid.SetColumn(port, 1); row.Children.Add(port); return row;
    }
    private void UpdateProtocol()
    {
        _probe.IsEnabled = !_working && _protocol.SelectedIndex != 1;
        string help = _protocol.SelectedIndex == 0
            ? "TCP 测试只连接目标端口，不经过本机转发。监听成功与目标可达是两项独立检查。"
            : _protocol.SelectedIndex == 1
                ? "UDP 无通用握手，本应用不会用 TCP 测试冒充 UDP 可达。请使用实际 UDP 客户端验证。"
                : "TCP/UDP 测试只验证 TCP 目标；UDP 仍需使用实际 UDP 客户端验证。";
        ToolTipService.SetToolTip(_protocol, help);
        AutomationProperties.SetHelpText(_protocol, help);
        Help(_probe, help);
    }
    private void Feedback(string title, string message, InfoBarSeverity severity)
    {
        _feedback.Title = title; _feedback.Message = message; _feedback.Severity = severity; _feedback.IsOpen = true;
        _feedbackFlyout.ShowAt(_discover);
    }
    private ForwardRule? ReadRule(bool requireAll = true)
    {
        if (!RuleEditorOptions.TryParsePort(PortValue(_targetPort), out int targetPort))
        { Feedback("请检查目标端口", "端口必须为 1–65535 的整数。", InfoBarSeverity.Error); _targetPort.Focus(FocusState.Programmatic); return null; }
        if (string.IsNullOrWhiteSpace(_target.Text))
        { Feedback("请填写目标地址", "可以手动输入虚拟机 IP 或主机名，无需发现虚拟机。", InfoBarSeverity.Error); _target.Focus(FocusState.Programmatic); return null; }
        int listenPort = 1;
        if (requireAll && !RuleEditorOptions.TryParsePort(PortValue(_listenPort), out listenPort))
        { Feedback("请检查本机端口", "端口必须为 1–65535 的整数。", InfoBarSeverity.Error); _listenPort.Focus(FocusState.Programmatic); return null; }
        var rule = new ForwardRule
        {
            Id = _original?.Id ?? Guid.NewGuid(), Name = _name.Text.Trim(), Protocol = RuleEditorOptions.ProtocolAt(_protocol.SelectedIndex),
            ListenAddress = requireAll ? _listen.Text.Trim() : "127.0.0.1", ListenPort = listenPort,
            TargetHost = _target.Text.Trim(), TargetPort = targetPort, Enabled = _enabled.IsOn,
            AutoFirewall = _autoFirewall.IsChecked == true
        };
        if (requireAll)
        {
            var errors = RuleValidator.Validate(rule);
            if (errors.Count > 0) { Feedback("请检查规则", string.Join("\n", errors), InfoBarSeverity.Error); return null; }
            if (string.IsNullOrWhiteSpace(rule.Name)) { Feedback("请填写规则名称", "为规则设置易于识别的名称。", InfoBarSeverity.Error); _name.Focus(FocusState.Programmatic); return null; }
        }
        return rule;
    }
    private void Working(bool working)
    {
        _working = working; IsPrimaryButtonEnabled = !working; IsSecondaryButtonEnabled = !working;
        _discover.IsEnabled = !working && SelectedSource != DiscoverySource.Manual;
        _source.IsEnabled = !working; _machines.IsEnabled = !working;
        foreach (var field in new Microsoft.UI.Xaml.Controls.Control[] { _name, _protocol, _source, _listen, _listenPort, _target, _targetPort, _enabled, _autoFirewall }) field.IsEnabled = !working;
        UpdateProtocol();
    }
    private async void SaveAsync(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            if (ReadRule() is not { } rule) return;
            Working(true);
            var operations = new ManagedRuleOperations(_client.SaveRuleAsync, _client.DeleteRuleAsync, PrivilegedOperations.SetFirewallAsync, _client.GetStateAsync);
            await operations.SaveAsync(rule, _original, _ct);
            // A successful IPC acknowledgement is the commit point. A separate readback may time out
            // after commit and must not mislabel a saved rule as failed or invite a duplicate retry.
            Saved = true; args.Cancel = false;
        }
        catch (Exception ex) { Feedback("保存失败", ex.Message, InfoBarSeverity.Error); }
        finally { Working(false); deferral.Complete(); }
    }
    private async Task ProbeAsync()
    {
        if (_working || _protocol.SelectedIndex == 1 || ReadRule(false) is not { } rule) return;
        Working(true);
        try { Feedback("目标 TCP 测试", UiPresentation.ProbeOutcome(await _client.ProbeAsync(rule, _ct)), InfoBarSeverity.Informational); }
        catch (Exception) { Feedback("目标 TCP 测试未成功", "请确认目标服务已启动，且目标地址和端口正确。", InfoBarSeverity.Warning); }
        finally { Working(false); }
    }
    private async Task DiscoverAsync()
    {
        if (_working || SelectedSource == DiscoverySource.Manual) return;
        _machines.Items.Clear(); _machines.SelectedIndex = -1; _machines.Visibility = Visibility.Collapsed;
        _feedback.IsOpen = false;
        Working(true);
        try
        {
            if (SelectedSource == DiscoverySource.HyperV)
            {
                // Elevate only this bounded read-only query, never the UI or forwarding Host.
                var machines = await PrivilegedOperations.GetHyperVAsync(_ct);
                foreach (var machine in machines)
                foreach (var address in machine.Addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct())
                    _machines.Items.Add(new ComboBoxItem { Content = $"{machine.Name} · {address}（{machine.State}）", Tag = address });
                ShowDiscoveryOutcome("Hyper-V 虚拟机地址");
                return;
            }

            var platform = SelectedSource switch
            {
                DiscoverySource.Wsl => TargetPlatform.Wsl,
                DiscoverySource.Containers => TargetPlatform.Containers,
                _ => throw new InvalidOperationException("请选择有效的发现来源。")
            };
            var discovery = await new EndpointDiscovery().DiscoverAsync(platform, _ct);
            foreach (var endpoint in discovery.Endpoints)
                _machines.Items.Add(new ComboBoxItem
                {
                    Content = $"{endpoint.Name} · {(endpoint.Port is int endpointPort ? UiPresentation.Endpoint(endpoint.Host, endpointPort) : endpoint.Host + "（端口需手动填写）")} · {endpoint.Detail}",
                    Tag = endpoint
                });
            ShowDiscoveryOutcome(UiPresentation.DiscoverySourceLabel(SelectedSource), discovery.Warnings);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Feedback("无法发现目标", $"{ex.Message}\n仍可手动填写目标地址和端口。发现不会启动发行版或容器，也不会修改端口发布设置。", InfoBarSeverity.Warning);
        }
        finally { Working(false); }
    }
    private void ShowDiscoveryOutcome(string source, IReadOnlyList<string>? warnings = null)
    {
        if (_machines.Items.Count == 0)
        {
            string detail = warnings is { Count: > 0 } ? string.Join("\n", warnings) : "未返回可用地址。";
            Feedback($"未发现 {source}", $"{detail}\n请确认目标已运行、端口可从 Windows 访问，或手动填写目标地址和端口。", InfoBarSeverity.Warning);
            return;
        }
        _machines.Visibility = Visibility.Visible;
        if (!RuleEditorOptions.ShouldShowDiscoveryFeedback(_machines.Items.Count, warnings))
        {
            _feedback.IsOpen = false;
            _feedbackFlyout.Hide();
            return;
        }
        Feedback($"已发现 {source}，但有提示", string.Join("\n", warnings!), InfoBarSeverity.Warning);
    }
}
