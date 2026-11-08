using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Low-level keyboard hook for Win+1..9 and Win+0. Explorer owns those shortcuts as hotkeys,
    /// so they are caught here before Explorer sees them and are swallowed.
    /// </summary>
    internal static class WinNumberHook
    {
        public static event EventHandler<int> Pressed;

        private static IntPtr _hook = IntPtr.Zero;
        private static LowLevelKeyboardProc _proc; // keep the delegate alive

        public static bool Enabled
        {
            get => _hook != IntPtr.Zero;
            set
            {
                if (value == Enabled) return;
                if (value) install();
                else uninstall();
            }
        }

        private static void install()
        {
            _proc = hookProc;
            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
            {
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
            }
        }

        private static void uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private static IntPtr hookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                bool keyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool keyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if ((keyDown || keyUp) && vk >= 0x30 && vk <= 0x39 && winDown() && !otherModifiersDown())
                {
                    if (keyDown)
                    {
                        // tap an unused key so letting go of Win doesn't open the Start menu
                        keybd_event(VK_UNASSIGNED, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                        int number = vk - 0x30;
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke(null, number)));
                    }

                    return (IntPtr)1; // swallow it so Explorer doesn't act on it too
                }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool winDown() => (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        // leave Win+Shift/Ctrl/Alt+N to whoever else wants them
        private static bool otherModifiersDown() =>
            (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
            (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

        #region Win32
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const byte VK_UNASSIGNED = 0xE8;
        private const uint KEYEVENTF_KEYUP = 0x2;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string lpModuleName);
        #endregion
    }
}
