using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// A real Win32 popup menu (looks like every other Windows right-click menu),
    /// built from simple text/action items.
    /// </summary>
    internal sealed class NativeMenu
    {
        private sealed class Item
        {
            public string Text;
            public Action Action;
            public bool Enabled = true;
            public bool Checked;
            public bool IsSeparator;
            public NativeMenu Sub;
            public ImageSource Icon;
        }

        private readonly List<Item> _items = new List<Item>();

        public NativeMenu Add(string text, Action action, bool enabled = true, bool isChecked = false, ImageSource icon = null)
        {
            _items.Add(new Item { Text = text, Action = action, Enabled = enabled, Checked = isChecked, Icon = icon });
            return this;
        }

        public NativeMenu Separator()
        {
            if (_items.Count > 0 && !_items[_items.Count - 1].IsSeparator)
            {
                _items.Add(new Item { IsSeparator = true });
            }
            return this;
        }

        public NativeMenu Submenu(string text)
        {
            var sub = new NativeMenu();
            _items.Add(new Item { Text = text, Sub = sub });
            return sub;
        }

        public int Count => _items.Count;

        /// <summary>
        /// Shows the menu at the cursor, kept clear of the given screen rectangle (the bar), and runs
        /// the chosen item's action. Blocks until the menu closes.
        /// </summary>
        public void Show(IntPtr owner, NativeRect exclude, bool barAtTop)
        {
            var actions = new List<Action>();
            var bitmaps = new List<IntPtr>();
            IntPtr hMenu = build(actions, bitmaps);

            try
            {
                int cmd = Track(hMenu, owner, exclude, barAtTop);
                if (cmd > 0 && cmd <= actions.Count)
                {
                    actions[cmd - 1]?.Invoke();
                }
            }
            finally
            {
                DestroyMenu(hMenu);
                foreach (IntPtr bmp in bitmaps) DeleteObject(bmp);
            }
        }

        private IntPtr build(List<Action> actions, List<IntPtr> bitmaps)
        {
            IntPtr hMenu = CreatePopupMenu();

            foreach (Item item in _items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                    continue;
                }

                uint flags = MF_STRING;
                if (!item.Enabled) flags |= MF_GRAYED;
                if (item.Checked) flags |= MF_CHECKED;

                UIntPtr id;
                if (item.Sub != null)
                {
                    flags |= MF_POPUP;
                    id = (UIntPtr)(ulong)item.Sub.build(actions, bitmaps).ToInt64();
                }
                else
                {
                    actions.Add(item.Action);
                    id = (UIntPtr)(uint)actions.Count;
                }

                // & marks a mnemonic in Win32 menus, so double it in names like "Tom & Jerry"
                AppendMenu(hMenu, flags, id, item.Text?.Replace("&", "&&"));

                if (item.Icon != null)
                {
                    IntPtr bmp = toHBitmap(item.Icon, 16);
                    if (bmp != IntPtr.Zero)
                    {
                        bitmaps.Add(bmp);
                        var info = new MENUITEMINFO
                        {
                            cbSize = (uint)Marshal.SizeOf(typeof(MENUITEMINFO)),
                            fMask = MIIM_BITMAP,
                            hbmpItem = bmp
                        };
                        SetMenuItemInfo(hMenu, (uint)(GetMenuItemCount(hMenu) - 1), true, ref info);
                    }
                }
            }

            return hMenu;
        }

        /// <summary>Tracks any HMENU (also another window's system menu) and returns the chosen command id.</summary>
        public static int Track(IntPtr hMenu, IntPtr owner, NativeRect exclude, bool barAtTop)
        {
            GetCursorPos(out POINT pt);

            uint flags = TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_VERTICAL |
                         (barAtTop ? TPM_TOPALIGN : TPM_BOTTOMALIGN);
            int y = barAtTop ? exclude.Bottom : exclude.Top;

            var tpm = new TPMPARAMS { cbSize = (uint)Marshal.SizeOf(typeof(TPMPARAMS)), rcExclude = exclude };

            useLightMenus();

            // the owner must be foreground or the menu won't close when clicking elsewhere
            SetForegroundWindow(owner);
            int cmd = TrackPopupMenuEx(hMenu, flags, pt.X, y, owner, ref tpm);
            PostMessage(owner, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }

        private static bool _lightSet;

        /// <summary>
        /// Cairo's base theme switches the app to dark mode, which makes Win32 menus dark.
        /// Force light menus (Windows 10 1903+ uxtheme ordinals 135 / 136).
        /// </summary>
        private static void useLightMenus()
        {
            if (_lightSet) return;
            _lightSet = true;
            try
            {
                if (Environment.OSVersion.Version.Build < 18362) return;
                SetPreferredAppMode(PREFERRED_APP_MODE_FORCE_LIGHT);
                FlushMenuThemes();
            }
            catch
            {
                // ordinals missing on this build; keep whatever theme Windows gives
            }
        }

        private const int PREFERRED_APP_MODE_FORCE_LIGHT = 3;
        [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();

        private static IntPtr toHBitmap(ImageSource source, int size)
        {
            try
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawImage(source, new Rect(0, 0, size, size));
                }

                var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);

                int stride = size * 4;
                byte[] pixels = new byte[stride * size];
                rtb.CopyPixels(pixels, stride, 0);

                var bmi = new BITMAPINFO
                {
                    biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFO)),
                    biWidth = size,
                    biHeight = -size, // top-down
                    biPlanes = 1,
                    biBitCount = 32
                };

                IntPtr hBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

                Marshal.Copy(pixels, 0, bits, pixels.Length);
                return hBitmap;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        #region Win32
        private const uint MF_STRING = 0x0;
        private const uint MF_GRAYED = 0x1;
        private const uint MF_CHECKED = 0x8;
        private const uint MF_POPUP = 0x10;
        private const uint MF_SEPARATOR = 0x800;
        private const uint MIIM_BITMAP = 0x80;

        private const uint TPM_RIGHTBUTTON = 0x2;
        private const uint TPM_TOPALIGN = 0x0;
        private const uint TPM_BOTTOMALIGN = 0x20;
        private const uint TPM_VERTICAL = 0x40;
        private const uint TPM_RETURNCMD = 0x100;
        private const int WM_NULL = 0x0;

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TPMPARAMS
        {
            public uint cbSize;
            public NativeRect rcExclude;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MENUITEMINFO
        {
            public uint cbSize;
            public uint fMask;
            public uint fType;
            public uint fState;
            public uint wID;
            public IntPtr hSubMenu;
            public IntPtr hbmpChecked;
            public IntPtr hbmpUnchecked;
            public IntPtr dwItemData;
            public string dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
            public uint bmiColors;
        }

        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetMenuItemInfo(IntPtr hMenu, uint item, bool fByPosition, ref MENUITEMINFO lpmii);
        [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, ref TPMPARAMS lptpm);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
        #endregion
    }
}
