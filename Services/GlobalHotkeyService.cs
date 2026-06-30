using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Poe2PriceGui.Services;

/// <summary>
/// 全局热键服务：基于 Win32 RegisterHotKey，在 WPF 窗口上监听 WM_HOTKEY。
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private readonly Window _window;
    private readonly int _hotkeyId;
    private HwndSource? _hwndSource;
    private string? _registeredHotkey;

    public GlobalHotkeyService(Window window, int hotkeyId)
    {
        _window = window;
        _hotkeyId = hotkeyId;
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnWindowClosed;
    }

    /// <summary>
    /// 热键被触发时触发。
    /// </summary>
    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// 注册热键，例如 "Ctrl+D"。注册失败返回 false。
    /// </summary>
    public bool Register(string hotkeyText)
    {
        Unregister();

        if (_hwndSource == null || !TryParseHotkey(hotkeyText, out var modifiers, out var key))
        {
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return false;
        }

        if (RegisterHotKey(_hwndSource.Handle, _hotkeyId, modifiers, virtualKey))
        {
            _registeredHotkey = hotkeyText;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 注销当前注册的热键。
    /// </summary>
    public void Unregister()
    {
        if (_hwndSource != null && _registeredHotkey != null)
        {
            UnregisterHotKey(_hwndSource.Handle, _hotkeyId);
            _registeredHotkey = null;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(_window);
        _hwndSource?.AddHook(HwndHook);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _hotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool TryParseHotkey(string hotkeyText, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(hotkeyText))
        {
            return false;
        }

        var parts = hotkeyText.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToList();

        if (parts.Count == 0)
        {
            return false;
        }

        foreach (var part in parts.Take(parts.Count - 1))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        var keyPart = parts[^1];
        if (!Enum.TryParse<Key>(keyPart, true, out var parsedKey) || parsedKey == Key.None)
        {
            return false;
        }

        key = parsedKey;
        return true;
    }

    public void Dispose()
    {
        Unregister();
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource = null;
        }

        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
