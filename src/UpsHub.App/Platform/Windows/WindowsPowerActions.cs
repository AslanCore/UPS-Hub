using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UpsHub.Core.Platform;
using UpsHub.Core.Settings;

namespace UpsHub.App.Platform.Windows;

/// <summary>Shutdown via shutdown.exe; hibernate / sleep via SetSuspendState (powrprof.dll).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerActions : IPowerActions
{
    public bool IsSupported => true;

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    public Task<string> ExecuteAsync(ShutdownAction action, bool force, CancellationToken ct = default)
    {
        switch (action)
        {
            case ShutdownAction.Shutdown:
            {
                var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
                var args = force ? "/s /f /t 0" : "/s /t 0";
                var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false };
                Process.Start(psi);
                return Task.FromResult($"shutdown.exe {args}");
            }
            case ShutdownAction.Hibernate:
                if (!SetSuspendState(true, force, false))
                    throw new InvalidOperationException($"Hibernate failed (error {Marshal.GetLastPInvokeError()}). Is hibernation enabled? (powercfg /hibernate on)");
                return Task.FromResult("Hibernate");
            case ShutdownAction.Sleep:
                if (!SetSuspendState(false, force, false))
                    throw new InvalidOperationException($"Sleep failed (error {Marshal.GetLastPInvokeError()}).");
                return Task.FromResult("Sleep");
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}
