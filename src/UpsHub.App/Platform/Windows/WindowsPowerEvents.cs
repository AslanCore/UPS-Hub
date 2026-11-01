using System.Runtime.Versioning;
using Microsoft.Win32;
using UpsHub.Core.Platform;

namespace UpsHub.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPowerEvents : IPowerEvents, IDisposable
{
    public event EventHandler? Resumed;
    public event EventHandler? Suspending;

    public WindowsPowerEvents() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Resumed?.Invoke(this, EventArgs.Empty);
        else if (e.Mode == PowerModes.Suspend) Suspending?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;
}
