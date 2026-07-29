using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dyno.App.ViewModels;

namespace Dyno.App.Views;

/// <summary>The live console: connection toolbar, telemetry and task monitor.
///
/// Almost pure markup. The exception is the brake duty-cycle box, which needs three things XAML
/// bindings cannot express on their own: committing on Enter, committing on focus loss, and
/// turning wheel notches into adjustments. All three delegate straight to the view model.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OnDutyCycleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.CommitDutyCycleAsync();
        }
    }

    private async void OnDutyCycleKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await vm.CommitDutyCycleAsync();
                break;

            // Abandon the edit: put the brake's actual figure back in the box.
            case Key.Escape:
                e.Handled = true;
                vm.RevertDutyCycleInput();
                break;
        }
    }

    private async void OnDutyCycleWheel(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.CanCommandDutyCycle)
        {
            return;
        }

        // Handled unconditionally once the box can be commanded, so a notch adjusts the value
        // instead of scrolling the page out from under the pointer.
        e.Handled = true;

        int notches = Math.Sign(e.Delta.Y);
        if (notches != 0)
        {
            await vm.NudgeDutyCycleAsync(notches);
        }
    }
}
