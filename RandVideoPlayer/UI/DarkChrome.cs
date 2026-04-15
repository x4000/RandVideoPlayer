using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RandVideoPlayer.UI;

// Windows-native dark-mode helpers: title bar via DWM (Win10 2004+) and
// dark scrollbars on specific child controls via UxTheme.
public static class DarkChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    public static void ApplyTitleBar(IntPtr hwnd, bool darkMode)
    {
        if (hwnd == IntPtr.Zero) return;
        int v = darkMode ? 1 : 0;
        // Newer attribute first, fall back to the older (Win10 pre-2004).
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref v, sizeof(int));
    }

    // Walks the control tree and applies the Explorer visual style — "DarkMode_Explorer"
    // when dark, plain "Explorer" when light. This themes scrollbars on ListView / ListBox
    // and similar common controls without rewriting them.
    public static void ApplyTreeTheme(Control root, bool darkMode)
    {
        Apply(root.Handle, darkMode);
        foreach (Control c in EnumerateDescendants(root))
        {
            if (c.IsHandleCreated) Apply(c.Handle, darkMode);
        }
    }

    private static void Apply(IntPtr hwnd, bool darkMode)
    {
        try { SetWindowTheme(hwnd, darkMode ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    private static System.Collections.Generic.IEnumerable<Control> EnumerateDescendants(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in EnumerateDescendants(c)) yield return d;
        }
    }
}
