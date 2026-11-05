using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UpsHub.Core.Nut;

public enum TlsMode { Off, Prefer, Require }

public sealed record NutConnectOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 3493;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TlsMode Tls { get; init; } = TlsMode.Off;
    /// <summary>NUT servers mostly use self-signed certificates; allow them unless a pin is given.</summary>
    public bool AcceptUntrustedCertificate { get; init; } = true;
    /// <summary>Optional SHA-256 thumbprint (hex) the server certificate must match.</summary>
    public string? PinnedCertificateSha256 { get; init; }
}

public sealed record NutUpsInfo(string Name, string Description);

/// <summary>
/// Minimal async client for the NUT network protocol (upsd, TCP 3493).
/// One command at a time; safe to call from several tasks.
/// </summary>
public sealed class NutClient : IAsyncDisposable
{
    private readonly NutConnectOptions _opt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public NutClient(NutConnectOptions options) => _opt = options;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;
    public bool IsTls { get; private set; }
    public string Host => _opt.Host;
    public int Port => _opt.Port;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisposeTransportAsync().ConfigureAwait(false);
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.ConnectTimeout);
        try
        {
            await tcp.ConnectAsync(_opt.Host, _opt.Port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new NutConnectionException($"Timed out connecting to {_opt.Host}:{_opt.Port}");
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new NutConnectionException($"Can't reach {_opt.Host}:{_opt.Port} ({ex.SocketErrorCode})", ex);
        }
        _tcp = tcp;
        AttachStream(tcp.GetStream());
        IsTls = false;

        if (_opt.Tls != TlsMode.Off)
        {
            try
            {
                await StartTlsAsync(ct).ConfigureAwait(false);
            }
            catch (NutException ex) when (_opt.Tls == TlsMode.Prefer &&
                (ex.Error is NutError.FeatureNotConfigured or NutError.FeatureNotSupported or NutError.UnknownCommand))
            {
                // Server can't do TLS; carry on in plain text as the user allowed.
            }
            catch (Exception ex) when (_opt.Tls == TlsMode.Prefer && !ct.IsCancellationRequested &&
                ex is System.Security.Authentication.AuthenticationException or IOException or NutConnectionException or OperationCanceledException)
            {
                // Server offered STARTTLS but the handshake failed: "Prefer" means fall back to a fresh plain connection.
                await DisposeTransportAsync().ConfigureAwait(false);
                var plain = new TcpClient { NoDelay = true };
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(_opt.ConnectTimeout);
                try { await plain.ConnectAsync(_opt.Host, _opt.Port, cts2.Token).ConfigureAwait(false); }
                catch (Exception inner) when (inner is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    plain.Dispose();
                    throw new NutConnectionException($"Can't reach {_opt.Host}:{_opt.Port} after TLS fallback", inner);
                }
                _tcp = plain;
                AttachStream(plain.GetStream());
                IsTls = false;
            }
        }
    }

    private void AttachStream(Stream s)
    {
        _stream = s;
        var enc = new UTF8Encoding(false);
        _reader = new StreamReader(s, enc, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(s, enc, 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
    }

    private async Task StartTlsAsync(CancellationToken ct)
    {
        var reply = await SendAsync("STARTTLS", ct).ConfigureAwait(false);
        if (!reply.StartsWith("OK", StringComparison.Ordinal))
            throw new NutConnectionException($"Unexpected STARTTLS reply: {reply}");
        var ssl = new SslStream(_tcp!.GetStream(), false, ValidateCert);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.ConnectTimeout);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _opt.Host }, cts.Token)
            .ConfigureAwait(false);
        AttachStream(ssl);
        IsTls = true;
    }

    private bool ValidateCert(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        if (!string.IsNullOrWhiteSpace(_opt.PinnedCertificateSha256))
        {
            var hash = Convert.ToHexString(cert.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
            return string.Equals(hash, _opt.PinnedCertificateSha256.Replace(":", ""), StringComparison.OrdinalIgnoreCase);
        }
        return errors == SslPolicyErrors.None || _opt.AcceptUntrustedCertificate;
    }

    /// <summary>Sends one command and returns the first reply line. Throws <see cref="NutException"/> on ERR.</summary>
    public async Task<string> SendAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SendCoreAsync(command, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<string> SendCoreAsync(string command, CancellationToken ct)
    {
        await WriteLineAsync(command, ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (line.StartsWith("ERR ", StringComparison.Ordinal) || line == "ERR")
            throw new NutException(line.Length > 4 ? line[4..] : "UNKNOWN", command);
        return line;
    }

    private async Task WriteLineAsync(string command, CancellationToken ct)
    {
        if (_writer is null) throw new NutConnectionException("Not connected");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.CommandTimeout);
            await _writer.WriteLineAsync(command.AsMemory(), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new NutConnectionException("Connection lost while sending", ex);
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        if (_reader is null) throw new NutConnectionException("Not connected");
        string? line;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.CommandTimeout);
            line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NutConnectionException("Server stopped answering (timeout)");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            throw new NutConnectionException("Connection lost while reading", ex);
        }
        if (line is null) throw new NutConnectionException("Server closed the connection");
        return line.TrimEnd('\r');
    }

    /// <summary>Runs a LIST command and returns the item lines between BEGIN and END.</summary>
    public async Task<List<List<string>>> ListAsync(string what, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var command = "LIST " + what;
            var first = await SendCoreAsync(command, ct).ConfigureAwait(false);
            if (!first.StartsWith("BEGIN LIST", StringComparison.Ordinal))
                throw new NutConnectionException($"Unexpected reply to {command}: {first}");
            var items = new List<List<string>>();
            for (int guard = 0; guard < 100_000; guard++)
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line.StartsWith("END LIST", StringComparison.Ordinal)) return items;
                if (line.StartsWith("ERR ", StringComparison.Ordinal)) throw new NutException(line[4..], command);
                items.Add(NutTokenizer.Split(line));
            }
            throw new NutConnectionException("LIST reply never ended");
        }
        finally { _gate.Release(); }
    }

    // ---- High-level commands -------------------------------------------------------------

    public Task<string> GetServerVersionAsync(CancellationToken ct = default) => SendAsync("VER", ct);

    public async Task<string?> GetProtocolVersionAsync(CancellationToken ct = default)
    {
        try { return await SendAsync("PROTVER", ct).ConfigureAwait(false); }
        catch (NutException)
        {
            try { return await SendAsync("NETVER", ct).ConfigureAwait(false); }
            catch (NutException) { return null; }
        }
    }

    public async Task<List<NutUpsInfo>> ListUpsAsync(CancellationToken ct = default)
    {
        var rows = await ListAsync("UPS", ct).ConfigureAwait(false);
        return rows.Where(r => r.Count >= 2 && r[0] == "UPS")
                   .Select(r => new NutUpsInfo(r[1], r.Count >= 3 ? r[2] : ""))
                   .ToList();
    }

    public async Task<Dictionary<string, string>> ListVarsAsync(string ups, CancellationToken ct = default)
    {
        var rows = await ListAsync("VAR " + ups, ct).ConfigureAwait(false);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            if (r.Count >= 4 && r[0] == "VAR") vars[r[2]] = r[3];
        return vars;
    }

    public async Task<string> GetVarAsync(string ups, string name, CancellationToken ct = default)
    {
        var line = await SendAsync($"GET VAR {ups} {name}", ct).ConfigureAwait(false);
        var t = NutTokenizer.Split(line);
        if (t.Count >= 4 && t[0] == "VAR") return t[3];
        throw new NutConnectionException($"Unexpected GET VAR reply: {line}");
    }

    public async Task AuthenticateAsync(string? username, string? password, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(username))
            await ExpectOkAsync($"USERNAME {username}", ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(password))
            await ExpectOkAsync($"PASSWORD {password}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers this client as an upsmon secondary for <paramref name="ups"/>. Optional: reading data needs no
    /// LOGIN, and some NUT builds (e.g. Synology) handle it badly, so only call it when the user asks.
    /// </summary>
    public async Task LoginAsync(string ups, CancellationToken ct = default)
    {
        try { await ExpectOkAsync($"LOGIN {ups}", ct).ConfigureAwait(false); }
        catch (NutException ex) when (ex.Error == NutError.AlreadyLoggedIn) { }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try { await SendAsync("LOGOUT", ct).ConfigureAwait(false); }
        // Also OperationCanceledException (caller's timeout) and InvalidOperationException (reader still busy).
        catch (Exception ex) when (ex is NutException or NutConnectionException or OperationCanceledException or InvalidOperationException or IOException or ObjectDisposedException) { }
    }

    private async Task ExpectOkAsync(string command, CancellationToken ct)
    {
        var reply = await SendAsync(command, ct).ConfigureAwait(false);
        if (!reply.StartsWith("OK", StringComparison.Ordinal))
            throw new NutConnectionException($"Unexpected reply to {NutException.RedactCommand(command)}: {reply}");
    }

    private async ValueTask DisposeTransportAsync()
    {
        try { if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        _reader?.Dispose(); _writer = null; _reader = null; _stream = null;
        _tcp?.Dispose(); _tcp = null;
        IsTls = false;
    }

    /// <summary>Sends LOGOUT (best effort) and closes the socket.</summary>
    public async Task DisconnectAsync()
    {
        if (IsConnected)
        {
            // Best effort only: on a hung or half-open socket LOGOUT times out (OperationCanceledException) or the
            // reader is still busy with the timed-out read (InvalidOperationException). Neither may escape.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await LogoutAsync(cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
        await DisposeTransportAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
