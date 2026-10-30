using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace LanBridge.App;

/// <summary>Follows the Windows application palette, including changes while the window is open.</summary>
internal sealed class SystemTheme : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly UISettings _settings = new();
    private readonly IntPtr _hwnd;
    private bool _disposed;

    internal SystemTheme(FrameworkElement root, Window window)
    {
        _root = root;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _settings.ColorValuesChanged += ColorsChanged;
        Apply();
    }

    private void ColorsChanged(UISettings sender, object args) =>
        _root.DispatcherQueue.TryEnqueue(() => { if (!_disposed) Apply(); });

    private void Apply()
    {
        var background = _settings.GetColorValue(UIColorType.Background);
        bool dark = (299 * background.R + 587 * background.G + 114 * background.B) < 128000;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        int immersiveDark = dark ? 1 : 0;
        // Native non-client caption follows the same palette as the XAML content.
        _ = DwmSetWindowAttribute(_hwnd, 20, ref immersiveDark, sizeof(int));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.ColorValuesChanged -= ColorsChanged;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
