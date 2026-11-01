using System.IO.Pipes;

namespace UpsHub.App;

/// <summary>One running copy per user. A second launch asks the first to show its window, then exits.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    public bool IsFirst { get; }
    public event Action? ActivationRequested;

    public SingleInstance(string scope)
    {
        var id = UpsHub.Core.AppInfo.AppId + "." + Environment.UserName + "." + scope;
        _pipeName = id + ".pipe";
        _mutex = new Mutex(true, id, out var created);
        IsFirst = created;
        if (IsFirst) Listen();
    }

    private void Listen()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    ActivationRequested?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(500, CancellationToken.None); }
            }
        }, ct);
    }

    public void SignalFirst()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(1500);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsFirst) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
