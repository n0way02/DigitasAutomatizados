using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KeyboundTyper.Services;

public static class KeyboardSimulator
{
    private const uint WM_CHAR = 0x0102;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;
    private const int VK_SHIFT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Only here so the INPUT union has its real size (40 bytes on x64); SendInput rejects anything else.
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static IntPtr GameWindowHandle
    {
        get
        {
            try
            {
                return Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }

    // ---------- PostMessage (Fast & Zero Missed Characters) ----------

    public static void SendCharPostMessage(IntPtr hWnd, char c)
    {
        if (hWnd == IntPtr.Zero) hWnd = GameWindowHandle;
        if (hWnd == IntPtr.Zero) return;

        PostMessage(hWnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
    }

    public static void SendEnterPostMessage(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) hWnd = GameWindowHandle;
        if (hWnd == IntPtr.Zero) return;

        // lParam with scan code 0x1C for Enter keydown and keyup
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)0x001C0001);
        Thread.Sleep(15);
        PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_RETURN, unchecked((IntPtr)(long)0xC01C0001));
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const ushort SCAN_LEFT_CTRL = 0x1D;
    private const ushort SCAN_LEFT_SHIFT = 0x2A;
    private const ushort SCAN_RETURN = 0x1C;
    private const ushort SCAN_SPACE = 0x39;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uMilliseconds);

    // ---------- SendInput (Real Hardware Scan Codes: 100% DirectX / DirectInput Recognition) ----------

    private static INPUT HardwareKey(ushort scanCode, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wVk = 0; // MUST be 0 when KEYEVENTF_SCANCODE is specified
        input.ki.wScan = scanCode;
        input.ki.dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        return input;
    }

    private static INPUT UnicodeKey(char c, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wVk = 0;
        input.ki.wScan = c;
        input.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
        return input;
    }

    private static void Send(params INPUT[] inputs)
    {
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendCharSendInput(char c)
    {
        if (c == ' ')
        {
            Send(HardwareKey(SCAN_SPACE, false));
            Thread.Sleep(20);
            Send(HardwareKey(SCAN_SPACE, true));
            return;
        }

        var scanResult = VkKeyScan(c);
        if (scanResult == -1)
        {
            // Fallback for special unicode symbols not present on keyboard layout
            Send(UnicodeKey(c, false));
            Thread.Sleep(20);
            Send(UnicodeKey(c, true));
            return;
        }

        var vk = (byte)(scanResult & 0xFF);
        var scan = (ushort)MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC (hardware scancode)
        var needsShift = ((scanResult >> 8) & 1) != 0;

        if (needsShift)
        {
            Send(HardwareKey(SCAN_LEFT_SHIFT, false));
            Thread.Sleep(12);
            Send(HardwareKey(scan, false));
            Thread.Sleep(20);
            Send(HardwareKey(scan, true));
            Thread.Sleep(12);
            Send(HardwareKey(SCAN_LEFT_SHIFT, true));
        }
        else
        {
            Send(HardwareKey(scan, false));
            Thread.Sleep(20);
            Send(HardwareKey(scan, true));
        }
    }

    public static void SendEnterSendInput()
    {
        // Hardware scan code 0x1C for Enter is directly picked up by FFXIV DirectInput and resets activity
        Send(HardwareKey(SCAN_RETURN, false));
        Thread.Sleep(30);
        Send(HardwareKey(SCAN_RETURN, true));
    }

    private const uint MOUSEEVENTF_MOVE = 0x0001;

    /// <summary>
    /// Sends a physical hardware scan code (Left Control: 0x1D) via DirectInput + micro mouse move.
    /// In DirectX games (FFXIV), DirectInput polls the 256-byte scan code array for player activity.
    /// Left Control does NOT type text into text boxes and does NOT move the character,
    /// but completely resets FFXIV's duty inactivity watchdog!
    /// </summary>
    public static void SendKeepAlive()
    {
        try
        {
            // 1. Hardware scan code for Left Control (0x1D) - registered by DirectInput
            Send(HardwareKey(SCAN_LEFT_CTRL, false));
            Thread.Sleep(18);
            Send(HardwareKey(SCAN_LEFT_CTRL, true));

            // 2. Mouse move (+1px then -1px) to also refresh GetLastInputInfo
            var moveRight = new INPUT
            {
                type = 0, // INPUT_MOUSE
                mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = MOUSEEVENTF_MOVE }
            };

            var moveLeft = new INPUT
            {
                type = 0, // INPUT_MOUSE
                mi = new MOUSEINPUT { dx = -1, dy = 0, dwFlags = MOUSEEVENTF_MOVE }
            };

            SendInput(1, new[] { moveRight }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(10);
            SendInput(1, new[] { moveLeft }, Marshal.SizeOf<INPUT>());
        }
        catch { }
    }

    public static async Task TypeStringAsync(
        string text,
        int minDelayMs,
        int maxDelayMs,
        bool autoEnter,
        int delayBeforeEnterMs,
        int delayPostEnterMs,
        int initialDelayMs,
        int inputMethod,
        CancellationToken ct)
    {
        var hWnd = GameWindowHandle;
        var rng = new Random();

        // Safe initial pause before the first character so the game text box is completely focused and previous word is cleared
        var initial = Math.Max(70, initialDelayMs);
        await Task.Delay(initial, ct);

        foreach (var c in text)
        {
            if (ct.IsCancellationRequested) return;

            if (inputMethod == 0)
                SendCharPostMessage(hWnd, c);
            else
                SendCharSendInput(c);

            var delay = rng.Next(Math.Max(15, minDelayMs), Math.Max(minDelayMs, maxDelayMs) + 1);
            if (delay > 0)
            {
                await Task.Delay(delay, ct);
            }
        }

        if (autoEnter && !ct.IsCancellationRequested)
        {
            if (delayBeforeEnterMs > 0)
            {
                await Task.Delay(delayBeforeEnterMs, ct);
            }

            if (inputMethod == 0)
                SendEnterPostMessage(hWnd);
            else
                SendEnterSendInput();

            // Give the game UI time to process Enter and clear the input box so the first letter of the next word isn't eaten
            var postEnter = Math.Max(100, delayPostEnterMs);
            await Task.Delay(postEnter, ct);
        }
    }
}
