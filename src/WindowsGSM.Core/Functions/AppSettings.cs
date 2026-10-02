using Microsoft.Win32;

namespace WindowsGSM.Functions
{
    internal static class AppSettings
    {
        private const string Root = @"SOFTWARE\WindowsGSM";

        public static bool GetBool(string key, bool defaultValue = false)
        {
            try
            {
                using var rk = Registry.CurrentUser.OpenSubKey(Root, false);
                if (rk == null) return defaultValue;

                var v = rk.GetValue(key);
                if (v == null) return defaultValue;

                if (v is int i) return i != 0;
                return bool.TryParse(v.ToString(), out var b) ? b : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            try
            {
                using var rk = Registry.CurrentUser.OpenSubKey(Root, false);
                if (rk == null) return defaultValue;

                var v = rk.GetValue(key);
                if (v == null) return defaultValue;

                if (v is int i) return i;
                return int.TryParse(v.ToString(), out var n) ? n : defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
