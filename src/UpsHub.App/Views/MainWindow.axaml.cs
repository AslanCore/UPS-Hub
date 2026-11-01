using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.VisualTree;
using UpsHub.App.ViewModels;
using UpsHub.Core.Platform;

namespace UpsHub.App.Views;

public partial class MainWindow : Window, INotifier
{
    private WindowNotificationManager? _toasts;
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            // Draw our own title bar but keep native resize borders, snap and shadow.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 52;
        }
        else
        {
            SystemDecorations = SystemDecorations.None;
        }
        TitleBar.PointerPressed += DragWindow;
        BrandBar.PointerPressed += DragWindow;
        TitleBar.DoubleTapped += (_, _) => ToggleMax();
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMax();
        CloseButton.Click += (_, _) => Close();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                UpdateCrumb(vm);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Page)) UpdateCrumb(vm); };
            }
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
            (DataContext as MainViewModel)?.SetWindowActive(IsVisible && WindowState != WindowState.Minimized);
    }

    /// <summary>Removes every visible in-app toast (used by the screenshot runner).</summary>
    internal void ClearToasts()
    {
        if (_toasts is null) return;
        var host = this.GetVisualDescendants().OfType<NotificationCard>().ToList();
        foreach (var c in host) c.Close();
    }

    private void UpdateCrumb(MainViewModel vm) => Crumb.Text = vm.Page switch
    {
        "servers" => "Servers",
        "shutdown" => "Shutdown rules",
        "settings" => "Settings",
        _ => "Dashboard",
    };

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _toasts = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3, Margin = new Thickness(0, 0, 8, 8) };
    }

    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && DataContext is MainViewModel vm && vm.Service.Settings.General.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (_toasts is null || !IsVisible) return;
        var type = kind switch
        {
            NotificationKind.Warning => NotificationType.Warning,
            NotificationKind.Critical => NotificationType.Error,
            NotificationKind.Success => NotificationType.Success,
            _ => NotificationType.Information,
        };
        _toasts.Show(new Notification(title, message, type, TimeSpan.FromSeconds(6)));
    }
}
