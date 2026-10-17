using System;
using Microsoft.Win32;

namespace NovaSonics.Services
{
    /// <summary>Registers/unregisters NovaSonics in the per-user Windows Run key.</summary>
    public static class StartupHelper
    {
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "NovaSonics";

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key == null) return;

                if (enabled)
                {
                    string? exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\" --minimized");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch { }
        }

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) != null;
            }
            catch { return false; }
        }
    }
}
