using System.Runtime.InteropServices;
using System.Windows;

namespace Poe2PriceGui.Services;

/// <summary>
/// 剪贴板服务：向当前前台窗口发送 Ctrl+C，并读取装备文本。
/// </summary>
public static class ClipboardService
{
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfScancode = 0x0008;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const int MaxRetry = 3;
    private const int RetryDelayMs = 150;
    private const uint WmKeydown = 0x0100;
    private const uint WmKeyup = 0x0101;
    private const uint MapvkVkToVsc = 0;

    /// <summary>
    /// 向当前前台窗口发送 Ctrl+C，然后读取剪贴板文本。
    /// 优先使用 PostMessage（不卡游戏），失败后回退到 SendInput。
    /// </summary>
    public static string CopyItemTextFromGame()
    {
        // 先记录前台窗口标题，便于诊断焦点问题。
        var foregroundHandle = GetForegroundWindow();
        var foregroundTitle = GetWindowTitle(foregroundHandle);
        AppLogger.Instance.Info($"发送 Ctrl+C 时前台窗口：{foregroundTitle}");

        // 清空剪贴板，避免读到旧内容。
        try
        {
            Application.Current.Dispatcher.Invoke(() => Clipboard.Clear());
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"清空剪贴板失败：{ex.Message}");
        }

        // 第一轮：用 PostMessage（轻量，不经过硬件输入管道，不卡游戏）。
        SendCopyViaPostMessage(foregroundHandle);
        var text1 = PollClipboardForText(totalWaitMs: 500, intervalMs: 80);
        if (!string.IsNullOrWhiteSpace(text1))
        {
            AppLogger.Instance.Info($"PostMessage 读到剪贴板内容，长度={text1.Length}");
            return text1;
        }
        AppLogger.Instance.Warn("PostMessage 未读到剪贴板内容");

        // 第二轮：回退到 SendInput（只试 1 次，避免多次卡顿）。
        AppLogger.Instance.Info("回退到 SendInput");
        SendCopyKeyCombo();
        var text2 = PollClipboardForText(totalWaitMs: 500, intervalMs: 80);
        if (!string.IsNullOrWhiteSpace(text2))
        {
            AppLogger.Instance.Info($"SendInput 读到剪贴板内容，长度={text2.Length}");
            return text2;
        }
        AppLogger.Instance.Warn("SendInput 未读到剪贴板内容");

        AppLogger.Instance.Warn("发送 Ctrl+C 后剪贴板仍为空。可能原因：1) 游戏以管理员权限运行而本程序未提权；2) 鼠标未悬停在装备上");
        return "";
    }

    /// <summary>
    /// 轮询剪贴板，在 totalWaitMs 内尝试读取非空文本。
    /// </summary>
    private static string PollClipboardForText(int totalWaitMs, int intervalMs)
    {
        var elapsed = 0;
        while (elapsed < totalWaitMs)
        {
            Thread.Sleep(intervalMs);
            elapsed += intervalMs;

            try
            {
                string? text = null;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText();
                    }
                });

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text!;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warn($"读取剪贴板失败：{ex.Message}");
            }
        }

        return "";
    }

    /// <summary>
    /// 通过 PostMessage 向目标窗口发送 WM_KEYDOWN/WM_KEYUP，模拟 Ctrl+C。
    /// 直接发送到窗口消息队列，不经过硬件输入管道，不会导致游戏卡顿。
    /// lParam 中编码扫描码到高位字（bits 16-23），提升输入法兼容性。
    /// </summary>
    private static void SendCopyViaPostMessage(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            AppLogger.Instance.Warn("PostMessage 失败：前台窗口句柄为空");
            return;
        }

        // 计算扫描码，编码到 lParam 的高位字（bits 16-23）。
        var ctrlScan = (uint)MapVirtualKey(VkControl, MapvkVkToVsc);
        var cScan = (uint)MapVirtualKey(VkC, MapvkVkToVsc);

        // lParam: 低位字 repeat count=1，bits 16-23 = scan code，bit 24 = extended flag。
        var lparamCtrlDown = (ctrlScan << 16) | 0x00000001;
        var lparamCDown = (cScan << 16) | 0x00000001;
        // KeyUp: bit 30 (previous state) = 1, bit 31 (transition state) = 1。
        var lparamCtrlUp = (ctrlScan << 16) | 0xC0000001;
        var lparamCUp = (cScan << 16) | 0xC0000001;

        PostMessage(hWnd, WmKeydown, (IntPtr)VkControl, (IntPtr)lparamCtrlDown);
        PostMessage(hWnd, WmKeydown, (IntPtr)VkC, (IntPtr)lparamCDown);
        Thread.Sleep(30);
        PostMessage(hWnd, WmKeyup, (IntPtr)VkC, (IntPtr)lparamCUp);
        PostMessage(hWnd, WmKeyup, (IntPtr)VkControl, (IntPtr)lparamCtrlUp);

        AppLogger.Instance.Info($"PostMessage 已发送 Ctrl+C 消息（Ctrl scan={ctrlScan}, C scan={cScan}）");
    }

    /// <summary>
    /// 发送 Ctrl+C：用 SendInput 发送硬件扫描码（WVk=0），绕过键盘布局影响。
    /// 参考 xiletrade-master 的 Input.Send 实现，使用 KEYEVENTF_SCANCODE 标志。
    /// </summary>
    private static void SendCopyKeyCombo()
    {
        // 扫描码 KeyDown：Ctrl → C
        SendScanCodeKeyDown(VkControl);
        SendScanCodeKeyDown(VkC);
        Thread.Sleep(20);
        // 扫描码 KeyUp：C → Ctrl
        SendScanCodeKeyUp(VkC);
        SendScanCodeKeyUp(VkControl);

        AppLogger.Instance.Info("SendInput 已发送扫描码 Ctrl+C");
    }

    /// <summary>
    /// 使用扫描码发送 KeyDown。WVk 必须设为 0，否则 Windows 会忽略 WScan。
    /// </summary>
    private static void SendScanCodeKeyDown(ushort vk)
    {
        var scanCode = (ushort)MapVirtualKey(vk, MapvkVkToVsc);
        var flags = KeyeventfScancode;
        if (IsExtendedKey(vk)) flags |= KeyeventfExtendedkey;

        var input = new INPUT
        {
            Type = 1,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT { WVk = 0, WScan = scanCode, DwFlags = flags }
            }
        };

        var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            var err = Marshal.GetLastWin32Error();
            AppLogger.Instance.Warn($"SendInput KeyDown 失败：vk=0x{vk:X2}, scan=0x{scanCode:X2}, Win32Error={err}");
        }
    }

    /// <summary>
    /// 使用扫描码发送 KeyUp。
    /// </summary>
    private static void SendScanCodeKeyUp(ushort vk)
    {
        var scanCode = (ushort)MapVirtualKey(vk, MapvkVkToVsc);
        var flags = KeyeventfScancode | KeyeventfKeyup;
        if (IsExtendedKey(vk)) flags |= KeyeventfExtendedkey;

        var input = new INPUT
        {
            Type = 1,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT { WVk = 0, WScan = scanCode, DwFlags = flags }
            }
        };

        var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            var err = Marshal.GetLastWin32Error();
            AppLogger.Instance.Warn($"SendInput KeyUp 失败：vk=0x{vk:X2}, scan=0x{scanCode:X2}, Win32Error={err}");
        }
    }

    /// <summary>
    /// 判断是否为扩展键（需要 KEYEVENTF_EXTENDEDKEY 标志）。
    /// 参考 xiletrade-master/Input.cs 的 IsExtendedKey 实现。
    /// </summary>
    private static bool IsExtendedKey(ushort vk)
    {
        return vk is 0xA3   // VK_RCONTROL
            or 0xA5          // VK_RMENU
            or 0x2D          // VK_INSERT
            or 0x2E          // VK_DELETE
            or 0x24          // VK_HOME
            or 0x23          // VK_END
            or 0x21          // VK_PRIOR
            or 0x22          // VK_NEXT
            or 0x26          // VK_UP
            or 0x25          // VK_LEFT
            or 0x27          // VK_RIGHT
            or 0x28;         // VK_DOWN
    }

    /// <summary>
    /// 获取窗口标题，用于日志诊断。
    /// </summary>
    private static string GetWindowTitle(IntPtr handle)
    {
        try
        {
            if (handle == IntPtr.Zero) return "<未知>";

            var length = GetWindowTextLength(handle);
            if (length == 0) return "<无标题>";

            var builder = new System.Text.StringBuilder(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            return builder.ToString();
        }
        catch
        {
            return "<读取失败>";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    // Win32 INPUT 结构体的完整定义，必须包含 union 的全部三个成员，
    // 否则 Marshal.SizeOf<INPUT>() 返回的尺寸比 Windows 期望的小，SendInput 返回 ERROR_INVALID_PARAMETER (87)。
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public HARDWAREINPUT Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint UMsg;
        public ushort WParamL;
        public ushort WParamH;
    }
}
