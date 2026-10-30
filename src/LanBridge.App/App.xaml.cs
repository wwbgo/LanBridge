using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace LanBridge.App;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\LanBridge.Desktop.SingleInstance";
    private const int SwRestore = 9;
    private const string WindowTitle = "LanBridge · 局域网端口转发";
    private Window? _window;
    private TrayIcon? _tray;
    private Mutex? _singleInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "ui-error.log"), $"{DateTimeOffset.Now:O}\n{args.Exception}");
            }
            catch { /* Preserve the original unhandled failure. */ }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!TryAcquireSingleInstance())
        {
            ActivateExistingWindow();
            return;
        }

        var window = new MainWindow();
        _window = window;
        _tray = new TrayIcon();
        _tray.OpenRequested += (_, _) => window.ShowFromTray();
        _tray.ExitRequested += async (_, _) => await window.ExitAsync();
        window.Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            _singleInstance?.Dispose();
            _singleInstance = null;
            _window = null;
        };
        window.Activate();
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(true, InstanceMutexName, out bool createdNew);
            if (createdNew) return true;
            _singleInstance.Dispose();
            _singleInstance = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
            return false;
        }
    }

    private static void ActivateExistingWindow()
    {
        IntPtr hwnd = FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero) return;
        _ = ShowWindow(hwnd, SwRestore);
        _ = SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
