using System.Runtime.InteropServices;
using System.Windows.Interop;
using AIUsage.Core.Support;

namespace AIUsage.Tray;

/// <summary>
/// System-wide keyboard shortcut to toggle the dashboard popup, using the Win32
/// RegisterHotKey/UnregisterHotKey API. Direct behavioral port of the Swift edition's
/// KeyboardShortcuts-backed togglePopover shortcut, minus its custom shortcut-recorder UI (no
/// per-user rebinding surface exists yet — see PORTING_NOTES.md). The default combo is
/// Ctrl+Alt+U, chosen to avoid colliding with common system/app shortcuts; there is no
/// equivalent of the original's "no default combo, user records one" behavior since there is no
/// recorder UI to record it with yet.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // Win32 modifier flags (user32.dll RegisterHotKey).
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    // Default combo: Ctrl+Alt+U ('U' virtual-key code 0x55).
    private const uint DefaultModifiers = ModControl | ModAlt | ModNoRepeat;
    private const uint DefaultVirtualKey = 0x55;

    private readonly Action _onTriggered;
    private HwndSource? _source;
    private bool _registered;

    public GlobalHotkeyService(Action onTriggered)
    {
        _onTriggered = onTriggered;
    }

    /// <summary>Registers the hotkey against a message-only window. Safe to call once; logs and
    /// disables itself (never throws) if the combo is already taken by another app.</summary>
    public void Start()
    {
        if (_source is not null) return;

        var parameters = new HwndSourceParameters("AIUsageGlobalHotkeyWindow")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE: a message-only window, never shown.
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _registered = RegisterHotKey(_source.Handle, HotkeyId, DefaultModifiers, DefaultVirtualKey);
        if (_registered)
        {
            AppLog.Info(LogTag.Config, "global hotkey registered (Ctrl+Alt+U toggles dashboard)");
        }
        else
        {
            AppLog.Info(LogTag.Config, "global hotkey disabled: Ctrl+Alt+U is already registered by another app");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _onTriggered();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
