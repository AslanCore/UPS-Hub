using System.Runtime.Versioning;
using Microsoft.Win32;
using UpsHub.Core;
using UpsHub.Core.Platform;

namespace UpsHub.App.Platform.Windows;

/// <summary>Start at logon via HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin needed).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupManager : IStartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ValueName => AppInfo.DisplayName;
    public bool IsSupported => true;

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
