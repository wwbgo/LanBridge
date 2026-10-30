using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LanBridge.App;

/// <summary>Small unpackaged notification-area icon with a hidden top-level HWND for shell broadcasts.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const string TrayWindowClassName = "LanBridge.NotificationAreaWindow";
    private const int WmApp = 0x8000;
    private const int WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmCommand = 0x0111;
    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;
    private const int NinKeySelect = 0x0401;
    private const int WmDestroy = 0x0002;
    private const int WmTaskbarCreatedFallback = 0x0400;
    private const int SwRestore = 9;
    private const int IdIcon = 1;
    private const int IdOpen = 1001;
    private const int IdExit = 1002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint Nmisetversion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint TpmLeftbutton = 0x0000;
    private const uint TpmReturncmd = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfEnabled = 0x00000000;
    private const uint WsExNoactivate = 0x08000000;
    private const uint WsExToolwindow = 0x00000080;
    private const uint CsHredraw = 0x0002;
    private const uint CsVredraw = 0x0001;
    private const uint ImageIcon = 1;
    private const uint LrLoadfromfile = 0x00000010;
    private const int SmCxsmicon = 49;
    private const int SmCysmicon = 50;

    private readonly WndProcDelegate _wndProc;
    private readonly string _className = TrayWindowClassName;
    private readonly uint _taskbarCreated;
    private readonly uint _callbackMessage = WmApp + 0x31;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconAdded;
    private bool _disposed;

    internal event EventHandler? OpenRequested;
    internal event EventHandler? ExitRequested;

    internal TrayIcon()
    {
        _wndProc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册任务栏恢复消息。");

        IntPtr instance = GetModuleHandle(null);
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = CsHredraw | CsVredraw,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册 LanBridge 托盘窗口。");

        _hwnd = CreateWindowEx(WsExToolwindow | WsExNoactivate, _className, "LanBridge", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            UnregisterClass(_className, instance);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 LanBridge 托盘窗口。");
        }

        try
        {
            // No LR_SHARED: this instance owns one HICON, reused after Explorer restarts.
            _hIcon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "LanBridge.ico"),
                ImageIcon, GetSystemMetrics(SmCxsmicon), GetSystemMetrics(SmCysmicon), LrLoadfromfile);
            if (_hIcon == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 LanBridge 托盘图标。");
            AddIcon();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void OpenMainWindow() => OpenRequested?.Invoke(this, EventArgs.Empty);

    internal void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void AddIcon()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        var data = CreateIconData();
        if (!Shell_NotifyIcon(NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 LanBridge 通知区域图标。");
        _iconAdded = true;
        data.uVersion = NotifyIconVersion4;
        _ = Shell_NotifyIcon(Nmisetversion, ref data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;
        var data = CreateIconData();
        _ = Shell_NotifyIcon(NimDelete, ref data);
        _iconAdded = false;
    }

    private NotifyIconData CreateIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = IdIcon,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = _callbackMessage,
        hIcon = _hIcon,
        szTip = "LanBridge · 点击打开主界面"
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreated)
        {
            _iconAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        if (message == _callbackMessage)
        {
            uint mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            // Version 4 sends semantic select/context-menu events, including keyboard activation.
            if (mouseMessage is WmLButtonUp or WmLButtonDblClk or NinSelect or NinKeySelect) OpenMainWindow();
            else if (mouseMessage is WmRButtonUp or WmContextMenu) ShowMenu();
            return IntPtr.Zero;
        }

        if (message == WmCommand)
        {
            switch (unchecked((int)(wParam.ToInt64() & 0xFFFF)))
            {
                case IdOpen: OpenMainWindow(); break;
                case IdExit: RequestExit(); break;
            }
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString | MfEnabled, (uint)IdOpen, "打开主界面");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString | MfEnabled, (uint)IdExit, "退出 LanBridge");
            if (!GetCursorPos(out var point)) return;
            SetForegroundWindow(_hwnd);
            uint selected = TrackPopupMenu(menu, TpmReturncmd | TpmNonotify | TpmLeftbutton,
                point.X, point.Y, 0, _hwnd, IntPtr.Zero);

            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (selected != 0) SendMessage(_hwnd, WmCommand, new IntPtr(selected), IntPtr.Zero);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        if (_hIcon != IntPtr.Zero)
        {
            _ = DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            IntPtr instance = GetModuleHandle(null);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            _ = UnregisterClass(_className, instance);
        }
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        internal uint cbSize;
        internal uint style;
        internal IntPtr lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal IntPtr hInstance;
        internal IntPtr hIcon;
        internal IntPtr hCursor;
        internal IntPtr hbrBackground;
        internal string? lpszMenuName;
        internal string lpszClassName;
        internal IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint cbSize;
        internal IntPtr hWnd;
        internal uint uID;
        internal uint uFlags;
        internal uint uCallbackMessage;
        internal IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string szTip;
        internal uint dwState;
        internal uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string szInfo;
        internal uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string szInfoTitle;
        internal uint dwInfoFlags;
        internal Guid guidItem;
        internal IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X; internal int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx([In] ref WndClassEx windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string? text);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
