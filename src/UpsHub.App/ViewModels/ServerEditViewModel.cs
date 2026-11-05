using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core.Local;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;

namespace UpsHub.App.ViewModels;

public sealed partial class UpsEditItemViewModel : ViewModelBase
{
    public required string Name { get; init; }
    public required ServerEditViewModel Owner { get; init; }
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _monitored = true;
    [ObservableProperty] private bool _notifications = true;
    [ObservableProperty] private bool _primary;
    partial void OnPrimaryChanged(bool value) { if (value) Owner.OnlyPrimary(this); }
}

/// <summary>Edit dialog for one NUT server or local USB UPS (Servers page "Edit").</summary>
public sealed partial class ServerEditViewModel : ViewModelBase
{
    private readonly UpsHubService _svc;
    private readonly Action _close;
    public string Id { get; }
    public bool IsLocal { get; }
    public bool IsNut => !IsLocal;
    public string Title { get; }
    public string Kicker => IsLocal ? "EDIT · THIS PC" : "EDIT · NUT SERVER";
    public string[] TlsModes { get; } = { "Off", "Prefer TLS", "Require TLS" };
    public string PasswordWatermark { get; }
    public ObservableCollection<UpsEditItemViewModel> Ups { get; } = new();
    public bool HasUps => Ups.Count > 0;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private decimal _port = 3493;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _username = "";
    /// <summary>Empty = keep the stored password (placeholder "(unchanged)").</summary>
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _clearPassword;
    [ObservableProperty] private int _tlsIndex;
    [ObservableProperty] private bool _loginAsSecondary;
    [ObservableProperty] private bool _usePollOverride;
    [ObservableProperty] private decimal _pollSeconds = 2;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _probeResult = "";
    [ObservableProperty] private bool _probeOk;
    [ObservableProperty] private bool _hasProbeResult;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _confirmRemove;
    public bool HasStoredPassword { get; }

    public ServerEditViewModel(UpsHubService svc, ServerSettings cfg, Action close)
    {
        _svc = svc; _close = close;
        Id = cfg.Id; IsLocal = cfg.IsLocal;
        Title = !string.IsNullOrWhiteSpace(cfg.DisplayName) ? cfg.DisplayName : IsLocal ? LocalNames.DisplayName(cfg.Local?.Vendor, cfg.Local?.Model) : cfg.Host;
        var mon = svc.Monitors.FirstOrDefault(m => m.Config.Id == cfg.Id);
        var e = ServerEdit.From(cfg, mon?.KnownUps.Select(u => u.Name) ?? Enumerable.Empty<string>(), svc.Settings.Shutdown.PrimaryUps);
        Host = e.Host; Port = e.Port; DisplayName = e.DisplayName; Username = e.Username ?? "";
        TlsIndex = (int)e.Tls; LoginAsSecondary = e.LoginAsSecondary;
        UsePollOverride = e.PollIntervalSeconds is not null;
        PollSeconds = e.PollIntervalSeconds ?? svc.Settings.General.PollIntervalSeconds;
        HasStoredPassword = cfg.ProtectedPassword is not null;
        PasswordWatermark = HasStoredPassword ? ServerEdit.UnchangedPlaceholder : "optional";
        foreach (var u in e.Ups)
            Ups.Add(new UpsEditItemViewModel { Name = u.Name, Owner = this, DisplayName = u.DisplayName, Monitored = u.Monitored, Notifications = u.Notifications, Primary = u.Primary });
    }

    public void OnlyPrimary(UpsEditItemViewModel keep)
    {
        foreach (var u in Ups) if (u != keep) u.Primary = false;
    }

    public ServerEdit Build() => new()
    {
        Id = Id, IsLocal = IsLocal, Host = Host.Trim(), Port = (int)Port, DisplayName = DisplayName.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        NewPassword = ClearPassword ? "" : Password.Length > 0 ? Password : null,
        Tls = (TlsMode)TlsIndex, LoginAsSecondary = LoginAsSecondary,
        PollIntervalSeconds = UsePollOverride ? (int)PollSeconds : null,
        Ups = Ups.Select(u => new UpsEdit(u.Name, u.DisplayName.Trim(), u.Monitored, u.Notifications, u.Primary)).ToList(),
    };

    private bool ShowErrors(IReadOnlyDictionary<string, string> errors)
    {
        ErrorText = string.Join("\n", errors.Values);
        HasError = errors.Count > 0;
        return !HasError;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var edit = Build();
        if (!ShowErrors(edit.Validate())) return;
        if (IsLocal) { ShowProbe(true, "A UPS on this PC is read directly; use Reopen device to test it."); return; }
        Busy = true;
        try
        {
            var draft = new ServerSettings { Host = edit.Host, Port = edit.Port, Username = edit.Username, Tls = edit.Tls };
            var pw = edit.NewPassword ?? _svc.StoredPassword(Id);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var (ver, ups) = await UpsHubService.ProbeAsync(draft, string.IsNullOrEmpty(pw) ? null : pw, cts.Token);
            ShowProbe(true, ups.Count == 0 ? $"Connected ({ver ?? "upsd"}), but the server lists no UPS."
                : $"Connected. Found {ups.Count} UPS: {string.Join(", ", ups.Select(u => u.Name))}.");
            foreach (var u in ups)
                if (!Ups.Any(x => string.Equals(x.Name, u.Name, StringComparison.OrdinalIgnoreCase)))
                    Ups.Add(new UpsEditItemViewModel { Name = u.Name, Owner = this });
            OnPropertyChanged(nameof(HasUps));
        }
        catch (NutException ex) { ShowProbe(false, NutErrorParser.Describe(ex.Error)); }
        catch (Exception ex) { ShowProbe(false, ex.Message); }
        finally { Busy = false; }
    }

    private void ShowProbe(bool ok, string text) { ProbeOk = ok; ProbeResult = text; HasProbeResult = true; }

    [RelayCommand]
    private void Save()
    {
        var errors = _svc.ApplyServerEdit(Build());
        if (ShowErrors(errors)) _close();
    }

    [RelayCommand] private void Cancel() => _close();
    [RelayCommand] private void AskRemove() => ConfirmRemove = true;
    [RelayCommand] private void KeepServer() => ConfirmRemove = false;

    [RelayCommand]
    private void Remove()
    {
        _svc.RemoveServer(Id);
        _close();
    }
}
