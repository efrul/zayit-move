using System;
using System.Text;
using Microsoft.Win32;

// Desktop Zayit's own settings: java.util.prefs, user root, which on Windows is the registry key
// HKCU\Software\JavaSoft\Prefs. Java stores each value encoded (WindowsPreferences.toWindowsValueString):
// a capital letter gets a '/' before it, '\' becomes "//", '/' becomes '\', and anything outside printable ASCII
// becomes "/u" and four hex digits. Zayit reads "database_path" (the full path of seforim.db, empty for its own
// folder), "theme_mode" and "accent_color" from there.
static class Prefs
{
    // ZAYITMOVE_PREFS_KEY points the tests at another key, so they never touch Zayit's settings.
    static string KeyPath
    {
        get
        {
            var k = Environment.GetEnvironmentVariable("ZAYITMOVE_PREFS_KEY");
            return string.IsNullOrEmpty(k) ? @"Software\JavaSoft\Prefs" : k;
        }
    }

    public static string Get(string name, string fallback)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                var v = key == null ? null : key.GetValue(name) as string;
                return v == null ? fallback : Decode(v);
            }
        }
        catch { return fallback; }
    }

    public static void Set(string name, string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
            key.SetValue(name, Encode(value), RegistryValueKind.String);
    }

    public static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            if (ch < 0x20 || ch > 0x7f) sb.Append("/u").Append(((int)ch).ToString("x4"));
            else if (ch == '\\') sb.Append("//");
            else if (ch == '/') sb.Append('\\');
            else if (ch >= 'A' && ch <= 'Z') sb.Append('/').Append(ch);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // WindowsPreferences.toJavaValueString, step for step.
    public static string Decode(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '/')
            {
                char next = i + 1 < s.Length ? s[i + 1] : ' ';
                if (next == 'u')
                {
                    if (s.Length < i + 6) break;
                    ch = (char)Convert.ToInt32(s.Substring(i + 2, 4), 16);
                    i += 5;
                }
                else if (next >= 'A' && next <= 'Z') { ch = next; i++; }
                else if (next == '/') { ch = '\\'; i++; }
            }
            else if (ch == '\\') ch = '/';
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
