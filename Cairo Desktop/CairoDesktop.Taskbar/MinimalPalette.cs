using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Builds a full taskbar color set from one accent color: neutral surfaces tinted with the
    /// accent's hue, the accent itself for the active window and start button, and text colors
    /// picked for contrast.
    /// </summary>
    public static class MinimalPalette
    {
        public static bool TryParse(string text, out Color color)
        {
            color = Colors.Transparent;
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return false;
                color = (Color)ColorConverter.ConvertFromString(text.Trim());
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Setting name → "#RRGGBB" for every taskbar color.</summary>
        public static Dictionary<string, string> Generate(Color accent, bool dark)
        {
            toHsl(accent, out double h, out double s, out double l);

            // a usable accent: not washed out, not too dark or bright for the mode
            double accentS = Math.Max(s, 0.35);
            double accentL = dark ? clamp(l, 0.50, 0.65) : clamp(l, 0.38, 0.55);
            Color acc = fromHsl(h, accentS, accentL);

            // surfaces: same hue, very low saturation
            double tint = clamp(s * 0.25, 0.03, 0.14);
            Color bg, hover, border, fg, activeBg, flash;

            if (dark)
            {
                bg = fromHsl(h, tint, 0.12);
                hover = fromHsl(h, tint, 0.19);
                border = fromHsl(h, tint, 0.27);
                fg = fromHsl(h, Math.Min(tint, 0.08), 0.88);
                activeBg = mix(acc, bg, 0.40);
                flash = fromHsl(34, 0.70, 0.40);
            }
            else
            {
                bg = fromHsl(h, tint, 0.84);
                hover = fromHsl(h, tint, 0.90);
                border = fromHsl(h, tint, 0.64);
                fg = fromHsl(h, Math.Min(tint, 0.25), 0.10);
                activeBg = mix(acc, Colors.White, 0.25);
                flash = fromHsl(38, 0.90, 0.68);
            }

            return new Dictionary<string, string>
            {
                ["MinTaskbarBackground"] = ToHex(bg),
                ["MinTaskbarForeground"] = ToHex(fg),
                ["MinTaskbarHoverBackground"] = ToHex(hover),
                ["MinTaskbarBorder"] = ToHex(border),
                ["MinTaskbarActiveBackground"] = ToHex(activeBg),
                ["MinTaskbarActiveForeground"] = ToHex(readableOn(activeBg, fg)),
                ["MinTaskbarFlashBackground"] = ToHex(flash),
                ["MinTaskbarStartBackground"] = ToHex(acc),
                ["MinTaskbarStartForeground"] = ToHex(readableOn(acc, dark ? fg : Colors.White, 3.0)),
                // subtle zebra: every second window button slightly toward the text color
                ["MinTaskbarTaskBackground"] = "",
                ["MinTaskbarTaskForeground"] = "",
                ["MinTaskbarTaskAltBackground"] = ToHex(mix(fg, bg, dark ? 0.05 : 0.04)),
                ["MinTaskbarTaskAltForeground"] = "",
                ["MinTaskbarUserBackground"] = "",
                ["MinTaskbarUserForeground"] = "",
            };
        }

        /// <summary>The preferred text color if it reads well on the background, else black or white.</summary>
        private static Color readableOn(Color background, Color preferred, double minContrast = 4.5)
        {
            if (contrast(background, preferred) >= minContrast) return preferred;
            return contrast(background, Colors.White) >= contrast(background, Colors.Black) ? Colors.White : Colors.Black;
        }

        private static double luminance(Color c)
        {
            double ch(byte v)
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }

        private static double contrast(Color a, Color b)
        {
            double la = luminance(a), lb = luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>amount of a, rest b</summary>
        private static Color mix(Color a, Color b, double amount)
        {
            byte m(byte x, byte y) => (byte)Math.Round(x * amount + y * (1 - amount));
            return Color.FromRgb(m(a.R, b.R), m(a.G, b.G), m(a.B, b.B));
        }

        private static double clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        private static void toHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;

            if (max == min)
            {
                h = 0;
                s = 0;
                return;
            }

            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        private static Color fromHsl(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360 / 360.0;

            double hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }

            double r, g, b;
            if (s == 0)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = hue(p, q, h + 1.0 / 3);
                g = hue(p, q, h);
                b = hue(p, q, h - 1.0 / 3);
            }

            return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
    }
}
