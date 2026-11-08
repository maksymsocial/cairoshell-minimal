using CairoDesktop.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Saves and loads the minimal taskbar's look (colors, palette, fonts, sizes) as small
    /// "Name=Value" text files in %LOCALAPPDATA%\Cairo Desktop\MinTaskbarThemes\*.theme.
    /// </summary>
    internal static class MinimalThemes
    {
        public const string Extension = ".theme";

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cairo Desktop", "MinTaskbarThemes");

        /// <summary>The settings a theme carries. Behavior (fade, delays, tray, clock...) stays out.</summary>
        private static readonly string[] Keys =
        {
            "MinTaskbarBackground",
            "MinTaskbarForeground",
            "MinTaskbarBorder",
            "MinTaskbarHoverBackground",
            "MinTaskbarActiveBackground",
            "MinTaskbarActiveForeground",
            "MinTaskbarFlashBackground",
            "MinTaskbarTaskBackground",
            "MinTaskbarTaskForeground",
            "MinTaskbarTaskAltBackground",
            "MinTaskbarTaskAltForeground",
            "MinTaskbarUserBackground",
            "MinTaskbarUserForeground",
            "MinTaskbarStartBackground",
            "MinTaskbarStartForeground",
            "MinTaskbarPaletteAccent",
            "MinTaskbarPaletteMode",
            "MinTaskbarFontFamily",
            "MinTaskbarFontSize",
            "MinTaskbarHeight",
            "MinTaskbarIconSize",
            "MinTaskbarButtonMaxWidth",
        };

        public static List<string> List()
        {
            try
            {
                if (!Directory.Exists(Folder)) return new List<string>();
                return Directory.GetFiles(Folder, "*" + Extension)
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Turns a typed name into a safe file name, or null if nothing usable is left.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            char[] bad = Path.GetInvalidFileNameChars();
            string clean = new string(name.Trim().Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return clean.Length == 0 ? null : clean;
        }

        public static bool Exists(string name) => File.Exists(pathOf(name));

        public static void Save(Settings settings, string name)
        {
            Directory.CreateDirectory(Folder);

            var sb = new StringBuilder();
            sb.AppendLine("# Cairo minimal taskbar theme");
            foreach (string key in Keys)
            {
                object value = typeof(Settings).GetProperty(key)?.GetValue(settings);
                sb.Append(key).Append('=').AppendLine(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            }

            File.WriteAllText(pathOf(name), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Applies a saved theme. Unknown or broken lines are skipped; returns how many values were set.</summary>
        public static int Load(Settings settings, string name)
        {
            int applied = 0;

            foreach (string line in File.ReadAllLines(pathOf(name)))
            {
                if (line.TrimStart().StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string text = line.Substring(eq + 1).Trim();
                if (!Keys.Contains(key)) continue;

                var prop = typeof(Settings).GetProperty(key);
                if (prop == null || !prop.CanWrite) continue;

                try
                {
                    object value = prop.PropertyType == typeof(string)
                        ? text
                        : Convert.ChangeType(text, prop.PropertyType, CultureInfo.InvariantCulture);
                    prop.SetValue(settings, value);
                    applied++;
                }
                catch (Exception)
                {
                    // bad number etc. - keep the current value
                }
            }

            return applied;
        }

        public static void Delete(string name) => File.Delete(pathOf(name));

        private static string pathOf(string name) => Path.Combine(Folder, name + Extension);
    }
}
