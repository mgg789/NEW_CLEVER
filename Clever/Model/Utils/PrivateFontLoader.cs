using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace Clever.Model.Utils
{
    internal static class PrivateFontLoader
    {
        private const uint FR_PRIVATE = 0x10;
        private static readonly HashSet<string> LoadedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> LoadedFontFiles = new List<string>();

        internal static void LoadBundledFonts()
        {
            var fontDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Fonts", "JetBrainsMono");
            if (!Directory.Exists(fontDirectory))
                return;

            foreach (var fontPath in Directory.GetFiles(fontDirectory, "*.ttf"))
            {
                RegisterFontFile(fontPath);
            }
        }

        internal static bool IsFontLoaded(string familyName)
        {
            return LoadedFamilies.Contains(familyName);
        }

        internal static void UnloadBundledFonts()
        {
            foreach (var fontPath in LoadedFontFiles)
            {
                RemoveFontResourceEx(fontPath, FR_PRIVATE, IntPtr.Zero);
            }

            LoadedFontFiles.Clear();
            LoadedFamilies.Clear();
        }

        private static void RegisterFontFile(string fontPath)
        {
            try
            {
                var familyNames = new List<string>();
                using (var privateFonts = new PrivateFontCollection())
                {
                    privateFonts.AddFontFile(fontPath);
                    foreach (var family in privateFonts.Families)
                    {
                        familyNames.Add(family.Name);
                    }
                }

                if (AddFontResourceEx(fontPath, FR_PRIVATE, IntPtr.Zero) > 0)
                {
                    LoadedFontFiles.Add(fontPath);
                    foreach (var familyName in familyNames)
                    {
                        LoadedFamilies.Add(familyName);
                    }
                }
            }
            catch
            {
                // Font loading is non-critical. The editor falls back to a system monospace font.
            }
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveFontResourceEx(string lpFileName, uint fl, IntPtr pdv);
    }
}
