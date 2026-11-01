using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpsHub.App.Services;
using UpsHub.Core.Discovery;
using UpsHub.Core.Model;
using UpsHub.Core.Nut;
using UpsHub.Core.Settings;

namespace UpsHub.App.ViewModels;

public sealed record Badge(string Text, IBrush Foreground, IBrush Background);

public sealed partial class FoundUpsViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBrush = Palette.Brush(Palette.Subtle);
    [ObservableProperty] private IBrush _statusSoft = Palette.Soft(Palette.Subtle);
    [ObservableProperty] private string _chargeText = "";

    public void Update(DiscoveredUps u)
    {
        Name = u.Name;
        Model = new[] { u.DisplayModel, u.Description }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        if (!string.IsNullOrWhiteSpace(u.Description) && !string.IsNullOrWhiteSpace(u.DisplayModel) && u.Description != u.DisplayModel)
            Model = $"{u.DisplayModel} · {u.Description}";
        var h = u.Health;
        StatusText = h switch
        {
            UpsHealth.Online => "Online",
            UpsHealth.OnBattery => "On battery",
            UpsHealth.LowBattery => "Low battery",
            UpsHealth.ForcedShutdown => "Shutting down",
            UpsHealth.Alarm => "Alarm",
            _ => u.Status is null ? "No data" : u.Status,
        };
        var c = Palette.For(h);
        StatusBrush = Palette.Brush(c);
        StatusSoft = Palette.Soft(c);
        ChargeText = u.BatteryCharge is { } ch ? $"{ch:0}%" : "";
    }
}

public sealed partial class FoundServerViewModel : ViewModelBase
{
    private readonly DiscoveryViewModel _owner;
    public DiscoveredServer Model { get; private set; }
    public string Key { get; }

    public FoundServerViewModel(DiscoveryViewModel owner, DiscoveredServer s)
    {
        _owner = owner;
        Model = s;
        Key = KeyOf(s);
        Update(s);
    }

    public static string KeyOf(DiscoveredServer s) => $"{s.Addresses.FirstOrDefault()}|{s.Port}";

    public ObservableCollection<FoundUpsViewModel> Ups { get; } = new();
    public ObservableCollection<Badge> Badges { get; } = new();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _upsSummary = "";
    [ObservableProperty] private bool _authRequired;
    [ObservableProperty] private bool _hasUps;
    [ObservableProperty] private bool _isAdded;
    [ObservableProperty] private bool _canAdd = true;
    [ObservableProperty] private bool _showCredentials;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _busy;

    public void Update(DiscoveredServer s)
    {
        Model = s;
        Title = s.Title;
        Endpoint = s.Endpoint;
        var extra = s.Addresses.Count > 1 ? $"also {string.Join(", ", s.Addresses.Select(a => a.ToString()).Where(a => a != s.Host).Take(3))}" : null;
        Detail = string.Join("  ·  ", new[] { s.ShortVersion, $"{Math.Max(1, s.Latency.TotalMilliseconds):0} ms", extra }.Where(x => x is not null));
        AuthRequired = s.AuthRequired;

        Badges.Clear();
        if (s.IsLocalMachine) Badges.Add(Make("THIS PC", Palette.Primary));
        foreach (var (flag, text) in new[] { (DiscoverySource.Mdns, "MDNS"), (DiscoverySource.Saved, "SAVED"), (DiscoverySource.Neighbor, "ARP"),
                     (DiscoverySource.Subnet, "SCAN"), (DiscoverySource.Custom, "CUSTOM") })
            if (s.Sources.HasFlag(flag)) Badges.Add(Make(text, Palette.Muted, neutral: true));
        if (s.AuthRequired) Badges.Add(Make("LOGIN REQUIRED", Palette.Degraded));
        if (s.TlsSupported == true) Badges.Add(Make("TLS", Palette.Up));

        while (Ups.Count > s.Ups.Count) Ups.RemoveAt(Ups.Count - 1);
        for (int i = 0; i < s.Ups.Count; i++)
        {
            if (i >= Ups.Count) Ups.Add(new FoundUpsViewModel());
            Ups[i].Update(s.Ups[i]);
        }
        HasUps = Ups.Count > 0;
        UpsSummary = s.AuthRequired && !HasUps
            ? "upsd answered but refused to list its UPSes without a login (ACCESS-DENIED). Add it with a user from upsd.users."
            : HasUps ? "" : "upsd is running but lists no UPS. Check ups.conf and that the driver is started.";
        RefreshAdded();
    }

    private static Badge Make(string text, Avalonia.Media.Color c, bool neutral = false) =>
        new(text, Palette.Brush(c), neutral ? Palette.Soft(c, 0x1A) : Palette.Soft(c));

    public void RefreshAdded()
    {
        IsAdded = _owner.Service.FindConfigured(Model) is not null;
        CanAdd = !IsAdded && !Busy;
        if (IsAdded && AuthRequired && !HasUps)
            UpsSummary = "Added with your login. Its UPSes appear in the sidebar once it connects.";
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (IsAdded) return;
        if (AuthRequired && !ShowCredentials) { ShowCredentials = true; return; }
        await AddCoreAsync();
    }

    [RelayCommand] private void ToggleLogin() => ShowCredentials = !ShowCredentials;

    private async Task AddCoreAsync()
    {
        Error = null; HasError = false;
        var user = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        var pw = string.IsNullOrEmpty(Password) ? null : Password;
        if (ShowCredentials && (user is not null || pw is not null))
        {
            Busy = true; CanAdd = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await UpsHubService.ProbeAsync(new ServerSettings { Host = Model.Host, Port = Model.Port, Username = user }, pw, cts.Token);
            }
            catch (NutException ex) { Fail(NutErrorParser.Describe(ex.Error)); return; }
            catch (Exception ex) { Fail(ex.Message); return; }
            finally { Busy = false; }
        }
        else if (AuthRequired) { Fail("This server needs a username and password from upsd.users."); return; }
        _owner.Service.AddDiscovered(Model, user, pw);
        Password = "";
        ShowCredentials = false;
        RefreshAdded();
        _owner.OnServerAdded(this);
    }

    private void Fail(string message)
    {
        Error = message; HasError = true; RefreshAdded();
    }
}

public sealed partial class DiscoveryViewModel : ViewModelBase
{
    internal UpsHubService Service { get; }
    private readonly Action _onAdded;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _sw = new();

    public DiscoveryViewModel(UpsHubService svc, Action onAdded)
    {
        Service = svc;
        _onAdded = onAdded;
        var d = svc.Settings.Discovery;
        _useMdns = d.UseMdns;
        _includeVirtual = d.IncludeVirtualAdapters;
        _extraPorts = d.ExtraPorts;
        _customTargets = d.CustomTargets;
    }

    public ObservableCollection<FoundServerViewModel> Results { get; } = new();
    /// <summary>Two-column "masonry" split of <see cref="Results"/> so short cards don't stretch to the tallest one.</summary>
    public ObservableCollection<FoundServerViewModel> LeftColumn { get; } = new();
    public ObservableCollection<FoundServerViewModel> RightColumn { get; } = new();

    private void Relayout()
    {
        LeftColumn.Clear(); RightColumn.Clear();
        for (int i = 0; i < Results.Count; i++) (i % 2 == 0 ? LeftColumn : RightColumn).Add(Results[i]);
    }
    public ObservableCollection<string> Networks { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private bool _showEmpty;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _showOptions;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _hasNotes;

    [ObservableProperty] private bool _useMdns;
    [ObservableProperty] private bool _includeVirtual;
    [ObservableProperty] private string _extraPorts;
    [ObservableProperty] private string _customTargets;

    partial void OnUseMdnsChanged(bool value) { Service.Settings.Discovery.UseMdns = value; Service.Save(); }
    partial void OnIncludeVirtualChanged(bool value) { Service.Settings.Discovery.IncludeVirtualAdapters = value; Service.Save(); }
    partial void OnExtraPortsChanged(string value) { Service.Settings.Discovery.ExtraPorts = value ?? ""; Service.Save(); }
    partial void OnCustomTargetsChanged(string value) { Service.Settings.Discovery.CustomTargets = value ?? ""; Service.Save(); }

    [RelayCommand] private void ToggleOptions() => ShowOptions = !ShowOptions;

    [RelayCommand]
    public async Task StartAsync()
    {
        if (IsScanning) return;
        _cts = new CancellationTokenSource();
        IsScanning = true; HasRun = true; ShowEmpty = false;
        Results.Clear(); LeftColumn.Clear(); RightColumn.Clear(); Networks.Clear(); Notes.Clear(); HasNotes = false; HasResults = false;
        ProgressValue = 0; PhaseText = "Preparing"; ProgressText = "Reading network adapters and the ARP table";
        SummaryText = "";
        _sw.Restart();
        var options = Service.BuildDiscoveryOptions();
        var progress = new Progress<DiscoveryProgress>(OnProgress);
        DiscoveryResult? result = null;
        try
        {
            var disco = Service.DiscoveryFactory();
            result = await Task.Run(() => disco.RunAsync(options, progress, _cts.Token));
        }
        catch (Exception ex) { Notes.Add("Scan failed: " + ex.Message); HasNotes = true; }
        finally
        {
            IsScanning = false;
            _cts.Dispose(); _cts = null;
        }
        if (result is null) { ShowEmpty = Results.Count == 0; return; }

        Merge(result.Servers);
        // Final order: new servers first (what the user came for), then already-added ones.
        var ordered = Results.OrderBy(r => r.IsAdded).ThenBy(r => r.Model.IsLocalMachine).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var cur = Results.IndexOf(ordered[i]);
            if (cur != i) Results.Move(cur, i);
        }
        Relayout();
        foreach (var n in result.Networks) Networks.Add($"{n.Subnet} · {n.Label} · {n.HostCount:N0} hosts");
        foreach (var n in result.Notes) Notes.Add(n);
        if (result.SkippedAdapters.Count(a => !a.IsLoopback) is > 0 and var skipped && !options.IncludeVirtualAdapters)
            Notes.Add($"Skipped {skipped} virtual/VPN/inactive adapter{(skipped == 1 ? "" : "s")}: {string.Join(", ", result.SkippedAdapters.Where(a => !a.IsLoopback).Select(a => a.Name).Distinct().Take(4))}. Turn on \u201cInclude virtual adapters\u201d to scan them.");
        HasNotes = Notes.Count > 0;
        ProgressValue = 100;
        var n0 = result.Servers.Count;
        PhaseText = result.Cancelled ? "Stopped" : "Done";
        SummaryText = $"{(result.Cancelled ? "Stopped after" : "Checked")} {result.Probed:N0} addresses in {result.Elapsed.TotalSeconds:0.0} s"
                      + $"  ·  {result.NeighborCount} ARP neighbour{(result.NeighborCount == 1 ? "" : "s")}  ·  {result.MdnsCount} mDNS"
                      + $"  ·  {n0} server{(n0 == 1 ? "" : "s")}, {result.Servers.Sum(s => s.Ups.Count)} UPS";
        ProgressText = SummaryText;
        ShowEmpty = n0 == 0 && !result.Cancelled;
        if (n0 > 0) Service.RememberDiscovered(result.Servers);
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    private void OnProgress(DiscoveryProgress p)
    {
        if (!IsScanning) return;
        ProgressValue = p.Phase == DiscoveryPhase.Preparing ? 2 : Math.Max(2, p.Fraction * 100);
        PhaseText = p.Phase switch
        {
            DiscoveryPhase.Preparing => "Preparing",
            DiscoveryPhase.Done => "Done",
            DiscoveryPhase.Cancelled => "Stopping",
            _ => "Scanning",
        };
        var found = p.Servers.Count;
        ProgressText = $"{p.Probed:N0} / {p.Total:N0} addresses  ·  {found} found  ·  {_sw.Elapsed.TotalSeconds:0.0} s"
                       + (p.Current is { } c && p.Phase == DiscoveryPhase.Scanning ? $"  ·  {c}" : "");
        Merge(p.Servers);
    }

    private void Merge(IReadOnlyList<DiscoveredServer> servers)
    {
        foreach (var s in servers)
        {
            var key = FoundServerViewModel.KeyOf(s);
            var vm = Results.FirstOrDefault(r => r.Key == key);
            if (vm is null)
            {
                vm = new FoundServerViewModel(this, s);
                Results.Add(vm);
                (LeftColumn.Count <= RightColumn.Count ? LeftColumn : RightColumn).Add(vm);
            }
            else vm.Update(s);
        }
        HasResults = Results.Count > 0;
    }

    internal void OnServerAdded(FoundServerViewModel vm)
    {
        foreach (var r in Results) r.RefreshAdded();
        _onAdded();
    }

    public void RefreshAdded() { foreach (var r in Results) r.RefreshAdded(); }
}
