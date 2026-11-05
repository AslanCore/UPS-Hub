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

    /// <summary>One slot per topic: "Power restored" replaces "On battery", "Reconnected" replaces "Connection lost".</summary>
    internal static string TagFor(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("battery") || t.Contains("power") || t.Contains("forced")) return "power";
        if (t.Contains("connect")) return "connection";
        if (t.Contains("shutdown") || t.Contains("hibernate") || t.Contains("sleep")) return "shutdown";
        var a = new string(title.Where(char.IsLetterOrDigit).ToArray());
        return a.Length > 16 ? a[..16] : a;   // Tag is limited to 16 characters
    }

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (_notifier is null) return;
        try
        {
            var scenario = kind == NotificationKind.Critical ? " scenario=\"urgent\"" : "";
            var xml = $"<toast{scenario}><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text></binding></visual></toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            // High priority: Windows shows it now instead of batching it behind other toasts or holding it while
            // the user is busy; Tag/Group make a newer power/connection toast replace the old one rather than queue
            // behind it; expiry drops a toast that is no longer true (e.g. "On battery" after power came back).
            var toast = new ToastNotification(doc)
            {
                Priority = ToastNotificationPriority.High,
                Tag = TagFor(title),
                Group = "upshub",
                ExpirationTime = DateTimeOffset.Now.AddMinutes(kind == NotificationKind.Critical ? 30 : 10),
            };
            _notifier.Show(toast);
        }
        catch { /* notifications disabled or unsupported: ignore */ }
    }
}
