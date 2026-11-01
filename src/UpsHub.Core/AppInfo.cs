using System.Reflection;

namespace UpsHub.Core;

/// <summary>
/// Product identity. Values come from Directory.Build.props (AppDisplayName etc.),
/// so renaming the app only means editing that one file.
/// </summary>
public static class AppInfo
{
    private static readonly Dictionary<string, string> Meta =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Value is not null)
            .GroupBy(a => a.Key)
            .ToDictionary(g => g.Key, g => g.First().Value!);

    private static string Get(string key, string fallback) =>
        Meta.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static string DisplayName => Get("AppDisplayName", "UPS Hub");
    public static string Publisher => Get("AppPublisher", "AslanCore");
    public static string Tagline => Get("AppTagline", "by AslanCore");
    /// <summary>Stable id used for the notification identity, mutex and Run key.</summary>
    public static string AppId => Get("AppId", "AslanCore.UpsHub");
    public static string RepoUrl => Get("AppRepoUrl", "");
    public static string Version =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Folder name under %APPDATA% (or ~/.config on Linux).</summary>
    public static string SettingsFolderName => DisplayName;
}
