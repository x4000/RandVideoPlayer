using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RandVideoPlayer.Integrations;

public static class Bandicut
{
    private static string? _cachedPath;
    private static bool _checked;

    public static string? FindExe()
    {
        if (_checked) return _cachedPath;
        _checked = true;

        // Common install locations.
        string?[] candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Bandicut\bandicut.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Bandicut\bandicut.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Bandisoft\Bandicut\bandicut.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Bandisoft\Bandicut\bandicut.exe"),
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) { _cachedPath = c; return _cachedPath; }
        }

        // Registry uninstall entries.
        string[] regRoots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var root in regRoots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var s = key.OpenSubKey(sub);
                    var name = s?.GetValue("DisplayName") as string;
                    if (name != null && name.IndexOf("Bandicut", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var loc = s?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc))
                        {
                            var exe = Path.Combine(loc, "bandicut.exe");
                            if (File.Exists(exe)) { _cachedPath = exe; return _cachedPath; }
                        }
                        var icon = s?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrEmpty(icon))
                        {
                            var exe = icon.Split(',')[0].Trim('"');
                            if (File.Exists(exe)) { _cachedPath = exe; return _cachedPath; }
                        }
                    }
                }
            }
            catch { }
        }
        return _cachedPath;
    }

    public static bool IsInstalled => FindExe() != null;

    public static void Open(string fileFullPath)
    {
        var exe = FindExe();
        if (exe == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{fileFullPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
