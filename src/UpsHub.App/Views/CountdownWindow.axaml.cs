using Avalonia.Controls;
using Avalonia.Input;

namespace UpsHub.App.Views;

public partial class CountdownWindow : Window
{
    public CountdownWindow()
    {
        InitializeComponent();
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button) BeginMoveDrag(e); };
    }
}
