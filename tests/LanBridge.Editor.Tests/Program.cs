using LanBridge.App;
using LanBridge.Core;

var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RuleEditorDialog.source.cs"));
var tests = new (string Name, Action Run)[]
{
    ("Editor groups identity, listener and target in a compact form", () =>
    {
        Check(source.Contains("form.Children.Add(Pair(_name, _protocol))", StringComparison.Ordinal), "Name and protocol should share the identity row.");
        Check(source.Contains("form.Children.Add(MainWindow.Text(\"本机监听\"", StringComparison.Ordinal), "Listener needs a section heading.");
        Check(source.Contains("form.Children.Add(MainWindow.Text(\"转发目标\"", StringComparison.Ordinal), "Target needs a section heading.");
        Check(source.Contains("form.Children.Add(_listenFeedback)", StringComparison.Ordinal), "Local port feedback must stay near the listener fields.");
    }),
    ("Firewall consent and elevated Hyper-V are wired into real editor actions", () =>
    {
        Check(source.Contains("AutoFirewall = _autoFirewall.IsChecked == true", StringComparison.Ordinal), "Persist explicit per-rule consent.");
        Check(source.Contains("_autoFirewall.IsChecked = original?.AutoFirewall ?? false", StringComparison.Ordinal), "No implicit consent for old/new rules.");
        Check(source.Contains("PrivilegedOperations.GetHyperVAsync(_ct)", StringComparison.Ordinal), "Hyper-V button requests short-lived UAC operation.");
        Check(source.Contains("ManagedRuleOperations", StringComparison.Ordinal), "Save must coordinate owned firewall and backend.");
        Check(source.Contains("_feedbackFlyout.ShowAt", StringComparison.Ordinal), "Validation/discovery hints must float instead of expanding the form.");
        var probeStart = source.IndexOf("private async Task ProbeAsync()", StringComparison.Ordinal);
        var probeEnd = source.IndexOf("private async Task DiscoverAsync()", probeStart, StringComparison.Ordinal);
        Check(!source[probeStart..probeEnd].Contains("ex.Message", StringComparison.Ordinal)
            && source[probeStart..probeEnd].Contains("目标 TCP 测试未成功", StringComparison.Ordinal), "Do not expose raw target connection errors in the editor.");
    }),
    ("Editor guidance uses hover help rather than standing paragraphs", () =>
    {
        Check(source.Contains("ToolTipService.SetToolTip(_source", StringComparison.Ordinal), "Source guidance must be available on hover.");
        Check(source.Contains("ToolTipService.SetToolTip(_protocol", StringComparison.Ordinal), "Protocol guidance must be available on hover.");
        Check(!source.Contains("form.Children.Add(_sourceHelp)", StringComparison.Ordinal)
            && !source.Contains("form.Children.Add(_protocolHelp)", StringComparison.Ordinal), "Do not occupy form rows with static help.");
        Check(source.Contains("AutomationProperties.SetHelpText", StringComparison.Ordinal), "Keep help available to accessibility clients.");
    }),
    ("Discovery source is immediately left of Discover in the same row", () =>
    {
        Check(source.Contains("form.Children.Add(Pair(_source, _discover));", StringComparison.Ordinal),
            "The discovery source and Discover must share a two-column row, in that order.");
        Check(!source.Contains("form.Children.Add(_source);", StringComparison.Ordinal), "Source must not occupy a separate row.");
        Check(!source.Contains("tools.Children.Add(_discover)", StringComparison.Ordinal), "Discover must not remain in the probe toolbar.");
    }),
    ("Discovery feedback is quiet only for nonempty warning-free results", () =>
    {
        bool Show(int count, IReadOnlyList<string>? warnings) => RuleEditorOptions.ShouldShowDiscoveryFeedback(count, warnings);
        Check(!Show(1, null) && !Show(3, Array.Empty<string>()), "Successful discovery must not open an InfoBar.");
        Check(Show(0, null), "Empty results still need guidance.");
        Check(Show(1, new[] { "A provider was unavailable." }), "Partial-result warnings must remain visible.");
        Check(Show(0, new[] { "Provider failed." }), "Empty-result warnings must remain visible.");
        Check(source.Contains("RuleEditorOptions.ShouldShowDiscoveryFeedback(_machines.Items.Count, warnings)", StringComparison.Ordinal),
            "Dialog must use the tested policy.");
        Check(source.Contains("_feedback.IsOpen = false;", StringComparison.Ordinal), "Quiet outcomes must close old feedback.");
    }),
    ("Both port fields offer labeled editable common ports", () =>
    {
        Check(source.Contains("private readonly ComboBox _listenPort = PortField(", StringComparison.Ordinal)
            && source.Contains("private readonly ComboBox _targetPort = PortField(", StringComparison.Ordinal),
            "Both port fields must be editable port dropdowns.");
        Check(source.Contains("IsEditable = true", StringComparison.Ordinal), "Dropdown must allow manual entry.");
        Check(source.Contains("RuleEditorOptions.CommonPorts", StringComparison.Ordinal), "Both dropdowns must use the tested catalog.");
        var items = RuleEditorOptions.CommonPorts;
        var ports = items.Select(item => item.Port).ToArray();
        Check(new[] { 22, 53, 80, 443, 3389, 5432, 6379, 8080 }.All(ports.Contains), "Offer familiar service ports.");
        Check(ports.Distinct().Count() == ports.Length && ports.All(port => port is >= 1 and <= 65535), "Catalog ports must be unique and valid.");
        Check(items.All(item => item.ToString()!.Contains(" · ", StringComparison.Ordinal)), "Each choice needs a human-readable service label.");
    }),
    ("Labeled port selections and custom numeric text resolve to numeric ports", () =>
    {
        bool Parse(string? text, out int port) => RuleEditorOptions.TryParsePort(text, out port);
        foreach (var choice in RuleEditorOptions.CommonPorts)
            Check(Parse(choice.ToString(), out var port) && port == choice.Port, $"Selected {choice} must resolve to {choice.Port}.");
        foreach (var text in new[] { "1", "65535", "49152", " 12345 ", "00080" })
            Check(Parse(text, out var port) && port is >= 1 and <= 65535, $"Custom numeric entry {text} must be accepted.");
        foreach (var text in new string?[] { null, "", " ", "0", "65536", "-1", "+80", "80.0", "1e3", "HTTP", "80 garbage", "80 · forged", "999999999999" })
            Check(!Parse(text, out _), $"Bad port {text ?? "<null>"} must be rejected.");
        Check(source.Contains("RuleEditorOptions.TryParsePort(PortValue(_targetPort)", StringComparison.Ordinal), "Target validation must use the editable port reader.");
        Check(source.Contains("RuleEditorOptions.TryParsePort(PortValue(_listenPort)", StringComparison.Ordinal), "Listen validation must use the editable port reader.");
    }),
    ("Editable listen IP choices include defaults and UP local addresses without losing original values", () =>
    {
        Check(source.Contains("private readonly ComboBox _listen =", StringComparison.Ordinal), "Listen IP must be an editable dropdown.");
        Check(source.Contains("NetworkInterface.GetAllNetworkInterfaces()", StringComparison.Ordinal)
            && source.Contains("OperationalStatus.Up", StringComparison.Ordinal)
            && source.Contains("UnicastAddresses", StringComparison.Ordinal), "Collect actual UP-interface unicast IPs.");
        Check(source.Contains("RuleEditorOptions.ListenAddresses(", StringComparison.Ordinal), "Dialog must use tested address options.");
        Check(source.Contains("_listen.SelectedItem = original?.ListenAddress ?? \"127.0.0.1\";", StringComparison.Ordinal), "Select the saved/default address so it is visibly populated in the editable ComboBox.");
        Check(source.Contains("SetPortField(_listenPort, original?.ListenPort);", StringComparison.Ordinal)
            && source.Contains("SetPortField(_targetPort, original?.TargetPort);", StringComparison.Ordinal), "Select or insert saved port values so edits never open blank.");
        Check(source.Contains("private static string PortValue(ComboBox field)", StringComparison.Ordinal)
            && source.Contains("RuleEditorOptions.TryParsePort(PortValue(_targetPort)", StringComparison.Ordinal)
            && source.Contains("RuleEditorOptions.TryParsePort(PortValue(_listenPort)", StringComparison.Ordinal),
            "Read the editable ComboBox text with a selected-item fallback instead of trusting stale selection state.");
        Check(source.Contains("field.Text = value;", StringComparison.Ordinal), "Populate the editable text value explicitly when restoring a saved port.");
        var local = new[] { "192.168.1.8", "2001:db8::8", "fe80::1%12", "127.0.0.1", "192.168.1.8", "", "not-an-ip" };
        var choices = RuleEditorOptions.ListenAddresses(local, "10.20.30.40").ToArray();
        Check(choices.Take(4).SequenceEqual(new[] { "127.0.0.1", "0.0.0.0", "::1", "::" }), "Offer IPv4/IPv6 loopback and wildcard defaults first.");
        Check(new[] { "192.168.1.8", "2001:db8::8", "fe80::1%12", "10.20.30.40" }.All(choices.Contains), "Include real local addresses and a saved address no longer present.");
        Check(choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() == choices.Length, "Address options must not be duplicated.");
        Check(!choices.Contains("") && !choices.Contains("not-an-ip"), "Ignore malformed interface entries.");
        var manual = RuleEditorOptions.ListenAddresses(Array.Empty<string>(), "manual-value");
        Check(manual.Contains("manual-value"), "Never silently replace manual/saved input; existing validation will report invalid addresses.");
    }),
    ("TCP, UDP and combined TCP/UDP round-trip through protocol selection", () =>
    {
        var indexFor = typeof(RuleEditorOptions).GetMethod("ProtocolIndex", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        var protocolAt = typeof(RuleEditorOptions).GetMethod("ProtocolAt", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Check(indexFor is not null && protocolAt is not null, "Protocol selection mapping is missing.");
        for (var index = 0; index < 3; index++)
        {
            var protocol = (TransportProtocol)index;
            Check((int)indexFor!.Invoke(null, new object[] { protocol })! == index, $"Original protocol {index} must select the matching option.");
            Check((TransportProtocol)protocolAt!.Invoke(null, new object[] { index })! == protocol, $"Selected option {index} must save the matching protocol.");
        }
        Check(source.Contains("_protocol.Items.Add(\"TCP/UDP\")", StringComparison.Ordinal), "Expose combined TCP/UDP as the third choice.");
        Check(source.Contains("RuleEditorOptions.ProtocolIndex(original?.Protocol ?? TransportProtocol.Tcp)", StringComparison.Ordinal), "Preserve combined protocol on edit.");
        Check(source.Contains("Protocol = RuleEditorOptions.ProtocolAt(_protocol.SelectedIndex)", StringComparison.Ordinal), "Save combined protocol without reducing to TCP/UDP separately.");
        Check(source.Contains("RuleEditorOptions.ProtocolIndex(endpoint.Protocol)", StringComparison.Ordinal), "Discovered protocols must use the same mapping.");
    }),
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 && tests.Length > 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
