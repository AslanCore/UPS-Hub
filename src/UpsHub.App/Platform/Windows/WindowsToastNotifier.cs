using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using UpsHub.Core;
using UpsHub.Core.Platform;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace UpsHub.App.Platform.Windows;

/// <summary>
/// Windows toast notifications for an unpackaged app. Registers one stable AppUserModelID under
/// HKCU\Software\Classes\AppUserModelId so the app appears once in notification settings (nutdotnet #232).
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsToastNotifier : INotifier
{
    private readonly string _aumid = AppInfo.AppId;
    private ToastNotifier? _notifier;

    public WindowsToastNotifier(string? iconPath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{_aumid}");
            key.SetValue("DisplayName", AppInfo.DisplayName);
            if (iconPath is not null && File.Exists(iconPath)) key.SetValue("IconUri", iconPath);
            _notifier = ToastNotificationManager.CreateToastNotifier(_aumid);
        }
        catch { _notifier = null; }
    }

    public bool IsAvailable => _notifier is not null;

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (_notifier is null) return;
        try
        {
            var scenario = kind == NotificationKind.Critical ? " scenario=\"urgent\"" : "";
            var xml = $"<toast{scenario}><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text></binding></visual></toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            _notifier.Show(new ToastNotification(doc));
        }
        catch { /* notifications disabled or unsupported: ignore */ }
    }
}
