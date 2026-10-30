using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using LanBridge.Control;
using LanBridge.Core;

if (args.Length != 1) throw new ArgumentException("Pass exact published LanBridge.App.exe path.");
string appPath = Path.GetFullPath(args[0]);
string hostPath = Path.Combine(Path.GetDirectoryName(appPath)!, "LanBridge.Host.exe");
var client = new BridgeClient();
bool occupied;
try { await client.GetStateAsync(); occupied = true; }
catch (TimeoutException) { occupied = false; }
if (occupied || Process.GetProcessesByName("LanBridge.App").Length != 0)
    throw new InvalidOperationException("Existing user app/backend found; refusing to change it.");
string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "lanbridge-tray-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
Process? host = null, app = null;
int passed = 0;
void Pass(string text) { passed++; Console.WriteLine("PASS " + text); }
void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
async Task Until(Func<bool> condition, string text, int milliseconds = 12000)
{
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < milliseconds) { if (condition()) return; await Task.Delay(150); }
    throw new TimeoutException(text);
}
int FreePort()
{
    for (int i = 0; i < 128; i++)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        using var tcp = new TcpListener(IPAddress.Loopback, port);
        try { tcp.Start(); return port; } catch (SocketException) { }
    }
    throw new IOException("No combined port available.");
}
using var targetTcp = new TcpListener(IPAddress.Loopback, FreePort());
targetTcp.Start();
using var targetUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)targetTcp.LocalEndpoint).Port));
using var stop = new CancellationTokenSource();
var echoTcp = Task.Run(async () => {
    try {
        while (!stop.IsCancellationRequested) {
            using var peer = await targetTcp.AcceptTcpClientAsync(stop.Token);
            byte[] bytes = new byte[128];
            int count = await peer.GetStream().ReadAsync(bytes, stop.Token);
            await peer.GetStream().WriteAsync(bytes.AsMemory(0, count), stop.Token);
        }
    } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
});
var echoUdp = Task.Run(async () => {
    try { while (!stop.IsCancellationRequested) { var packet = await targetUdp.ReceiveAsync(stop.Token); await targetUdp.SendAsync(packet.Buffer, packet.RemoteEndPoint, stop.Token); } }
    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
});
var rule = new ForwardRule { Name = "isolated-tray", ListenAddress = "127.0.0.1", ListenPort = FreePort(), TargetHost = "127.0.0.1", TargetPort = ((IPEndPoint)targetTcp.LocalEndpoint).Port, Protocol = TransportProtocol.TcpUdp, Enabled = true };
async Task Echo()
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    byte[] payload = Encoding.UTF8.GetBytes("tray-real-sockets");
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, rule.ListenPort, deadline.Token);
    await tcp.GetStream().WriteAsync(payload, deadline.Token);
    byte[] received = new byte[payload.Length];
    await tcp.GetStream().ReadExactlyAsync(received, deadline.Token);
    Check(received.SequenceEqual(payload), "TCP payload differs.");
    using var udp = new UdpClient();
    await udp.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, rule.ListenPort), deadline.Token);
    Check((await udp.ReceiveAsync(deadline.Token)).Buffer.SequenceEqual(payload), "UDP payload differs.");
}
async Task StartHost()
{
    host?.Dispose();
    host = Process.Start(new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--console", "--data-dir", data } })!;
    for (int i = 0; i < 30; i++) {
        Check(!host.HasExited, "Owned Host exited early.");
        try { Check((await client.GetStateAsync()).ProcessId == host.Id, "IPC connected to a different Host."); return; }
        catch (TimeoutException) { await Task.Delay(150); }
    }
    throw new TimeoutException("Host not ready.");
}
IntPtr FindTray() => Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "LanBridge.NotificationAreaWindow", null) is var top && top != IntPtr.Zero
    ? top : Native.FindWindowEx(new IntPtr(-3), IntPtr.Zero, "LanBridge.NotificationAreaWindow", null);
async Task InvokeTrayMenu(IntPtr tray, string label)
{
    // NOTIFYICON_VERSION_4 shell right-click uses WM_CONTEXTMENU, not legacy WM_RBUTTONUP.
    Check(Native.PostMessage(tray, 0x8031, IntPtr.Zero, new IntPtr((1 << 16) | 0x007B)), "Cannot post tray context callback.");
    IntPtr menu = IntPtr.Zero;
    try {
        await Until(() => (menu = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "#32768", null)) != IntPtr.Zero, "Version-4 tray context callback did not show a native menu.", 4000);
        var root = AutomationElement.FromHandle(menu);
        var item = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label));
        Check(item is not null, "Native menu missing: " + label);
        Console.WriteLine($"MENU {item!.Current.Name} process={item.Current.ProcessId} hwnd={menu}");
        ((InvokePattern)item.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await Task.Delay(300);
        if (Native.IsWindowVisible(menu))
        {
            // Verified UIA no-op: use the current observed menu item's screen bounds.
            var bounds = item.Current.BoundingRectangle;
            Native.GetCursorPos(out var original);
            try
            {
                Native.SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                Native.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Native.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                await Until(() => !Native.IsWindowVisible(menu), "Observed native menu click had no effect.", 4000);
            }
            finally { Native.SetCursorPos(original.X, original.Y); }
        }
    } catch { if (menu != IntPtr.Zero) Native.PostMessage(menu, 0x0100, new IntPtr(27), IntPtr.Zero); throw; }
}
try
{
    await StartHost();
    await client.SaveRuleAsync(rule);
    Check((await client.GetStateAsync()).Rules.Single() == rule, "Test rule not persisted.");
    await Echo();
    app = Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(appPath)! })!;
    IntPtr main = IntPtr.Zero, tray = IntPtr.Zero;
    await Until(() => { app.Refresh(); return !app.HasExited && (main = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "LanBridge · 局域网端口转发")) != IntPtr.Zero && (tray = FindTray()) != IntPtr.Zero; }, "Published UI/tray did not appear.", 30000);
    var root = AutomationElement.FromHandle(main);
    await Until(() => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "添加规则"))?.Current.IsEnabled == true, "Published UI not connected.");
    Pass("published UI connects to exact isolated Host and owns a tray window");
    bool topLevel = false;
    Native.EnumWindows((window, _) => { if (window == tray) topLevel = true; return true; }, IntPtr.Zero);
    Check(topLevel, "Tray needs a hidden top-level HWND to receive TaskbarCreated broadcasts and own a foreground popup.");
    ((WindowPattern)root.GetCurrentPattern(WindowPattern.Pattern)).Close();
    await Until(() => !Native.IsWindowVisible(main), "Closing window did not hide it.");
    Check(!app.HasExited && !host!.HasExited && FindTray() == tray, "Close destroyed app/backend/tray.");
    await Echo();
    Pass("window close hides UI while tray and real TCP/UDP forwarding stay alive");
    await InvokeTrayMenu(tray, "打开主界面");
    await Until(() => Native.IsWindowVisible(main), "Tray menu did not restore same HWND.");
    Check(FindTray() == tray && !app.HasExited, "Restoring window replaced app/tray.");
    Pass("version-4 native tray popup opens and UIA Open restores same window");
    ((WindowPattern)root.GetCurrentPattern(WindowPattern.Pattern)).Close();
    await Until(() => !Native.IsWindowVisible(main), "Second close did not hide.");
    // Version-4 select event is NIN_SELECT (WM_USER), not legacy WM_LBUTTONUP.
    Native.PostMessage(tray, 0x8031, IntPtr.Zero, new IntPtr((1 << 16) | 0x0400));
    await Until(() => Native.IsWindowVisible(main), "Version-4 tray select did not restore window.");
    Pass("version-4 tray select restores hidden window");
    await InvokeTrayMenu(tray, "退出 LanBridge");
    await Until(() => app.HasExited && host!.HasExited, "Native tray exit left UI/backend alive.", 15000);
    Check(app.ExitCode == 0 && host!.ExitCode == 0, "Exit code must be zero.");
    Check(FindTray() == IntPtr.Zero, "Tray window remains after exit.");
    using (var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
    using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) {
        tcp.ExclusiveAddressUse = udp.ExclusiveAddressUse = true;
        tcp.Bind(new IPEndPoint(IPAddress.Loopback, rule.ListenPort));
        udp.Bind(new IPEndPoint(IPAddress.Loopback, rule.ListenPort));
    }
    Pass("native tray Exit closes exact UI/Host, removes tray and releases TCP/UDP ports");
    await StartHost();
    Check((await client.GetStateAsync()).Rules.Single() == rule, "Tray exit changed saved rule.");
    await Echo();
    await client.ShutdownAsync();
    Pass("restart preserves enabled configuration and restores both forwarding transports");
    Console.WriteLine($"RESULT {passed} tray acceptance checks passed. Isolated data: {data}");
}
finally
{
    // Only test-owned exact Process instances; never enumerate/kill user's backend.
    if (app is not null && !app.HasExited) { app.Kill(); await app.WaitForExitAsync(); }
    if (host is not null && !host.HasExited) { try { await client.ShutdownAsync(); } catch { host.Kill(); await host.WaitForExitAsync(); } }
    app?.Dispose(); host?.Dispose(); stop.Cancel(); targetTcp.Stop(); targetUdp.Dispose();
    await Task.WhenAll(echoTcp, echoUdp);
}

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    internal delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
