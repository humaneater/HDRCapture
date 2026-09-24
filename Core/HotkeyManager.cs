using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using HdrCapture.Infrastructure;

namespace HdrCapture.Core;

internal sealed class HotkeyManager : IDisposable
{
    public const int AltModifier = 0x0001;
    public const int ControlModifier = 0x0002;
    public const int ShiftModifier = 0x0004;
    public const int WindowsModifier = 0x0008;
    public const int NoRepeatModifier = 0x4000;

    private const int HotkeyId = 0x4844;
    private const int WmHotkey = 0x0312;
    private HwndSource? _source;
    private bool _registered;
    private int _modifiers;
    private int _virtualKey;

    public event EventHandler? Pressed;

    public void Initialize()
    {
        var parameters = new HwndSourceParameters("HdrCapture.HotkeyWindow")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool TryRegister(int modifiers, int virtualKey, out string error)
    {
        if (_source is null)
        {
            throw new InvalidOperationException("HotkeyManager is not initialized.");
        }

        var oldModifiers = _modifiers;
        var oldVirtualKey = _virtualKey;
        var wasRegistered = _registered;
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        if (RegisterHotKey(_source.Handle, HotkeyId, modifiers | NoRepeatModifier, virtualKey))
        {
            _modifiers = modifiers;
            _virtualKey = virtualKey;
            _registered = true;
            error = string.Empty;
            return true;
        }

        var code = Marshal.GetLastWin32Error();
        error = new Win32Exception(code).Message;

        if (wasRegistered &&
            RegisterHotKey(_source.Handle, HotkeyId, oldModifiers | NoRepeatModifier, oldVirtualKey))
        {
            _modifiers = oldModifiers;
            _virtualKey = oldVirtualKey;
            _registered = true;
        }

        return false;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, int modifiers, int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}

internal static class HotkeyFormatter
{
    public static string Format(int modifiers, int virtualKey)
    {
        var parts = new List<string>(5);
        if ((modifiers & HotkeyManager.ControlModifier) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & HotkeyManager.AltModifier) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & HotkeyManager.ShiftModifier) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & HotkeyManager.WindowsModifier) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(FormatKey(KeyInterop.KeyFromVirtualKey(virtualKey)));
        return string.Join("+", parts);
    }

    public static bool IsAllowed(int modifiers, int virtualKey)
    {
        if (virtualKey <= 0 || modifiers < 0)
        {
            return false;
        }

        if (modifiers != 0)
        {
            return true;
        }

        return virtualKey is >= 0x70 and <= 0x87 // F1-F24
            or 0x2C // PrintScreen
            or 0x13 // Pause
            or 0x91; // ScrollLock
    }

    private static string FormatKey(Key key)
    {
        return key switch
        {
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            _ => key.ToString()
        };
    }
}
