using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HandheldOptimiser.ViewModels;

namespace HandheldOptimiser;

/// <summary>
/// Where the keyboard is. A handheld's controller reaches this app as arrow keys, Enter and Escape,
/// and arrow keys move from whichever control has the keyboard. Opening a card replaces the whole
/// page, and a job disables it, and either would leave nothing with the keyboard: the D-pad would then
/// do nothing until the screen was touched. So the keyboard is put somewhere sensible each time.
///
/// None of this draws anything for someone using touch or a mouse. The ring around the control that
/// has the keyboard only shows once a key has been pressed.
/// </summary>
public partial class MainWindow
{
    /// <summary>The page on screen, so that going Back can return to the card it was opened from.</summary>
    private PageViewModelBase? _shown;

    /// <summary>The last control that had the keyboard, to give it back after a job.</summary>
    private IInputElement? _lastFocus;

    private void WatchFocus(MainViewModel vm)
    {
        _shown = vm.SelectedPage;
        vm.PropertyChanged += OnShellChanged;
    }

    private void UnwatchFocus(MainViewModel vm) => vm.PropertyChanged -= OnShellChanged;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        // The window itself gets the keyboard when the control that had it is disabled or removed.
        // That is the case this exists to recover from, so it is not remembered.
        if (!ReferenceEquals(e.NewFocus, this))
        {
            _lastFocus = e.NewFocus;
        }
    }

    /// <summary>
    /// With nothing holding the keyboard (a blank part of the page was touched, say), the first arrow
    /// key gives it back to a control and is spent doing so. Left alone it would do nothing at all.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var nothingHasIt = Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, this);

        if (nothingHasIt && Views.FocusKeys.Wakes(e.Key) && DataContext is MainViewModel vm)
        {
            GiveFocus(vm);
            e.Handled = true;
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm)
        {
            return;
        }

        // Both wait until the page has been built or enabled again, which bindings do first.
        if (e.PropertyName == nameof(MainViewModel.SelectedPage))
        {
            var cameFrom = _shown;
            _shown = vm.SelectedPage;
            Dispatcher.BeginInvoke(() => PlaceFocus(vm, cameFrom), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsBusy) && !vm.IsBusy)
        {
            Dispatcher.BeginInvoke(() => RestoreFocus(vm), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// On the Dashboard, the card that was just left, or the first card. On a goal's page, its first
    /// control, or Back when the page has none that can take the keyboard (it is disabled during a job).
    /// </summary>
    private void PlaceFocus(MainViewModel vm, PageViewModelBase? cameFrom)
    {
        if (vm.IsHome)
        {
            var cards = Descendants<Button>(PageHost).Where(b => b.DataContext is GoalPageViewModel).ToList();
            var card = cards.FirstOrDefault(b => ReferenceEquals(b.DataContext, cameFrom)) ?? cards.FirstOrDefault();
            card?.Focus();
            return;
        }

        if (!PageHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
        {
            BackButton.Focus();
        }
    }

    /// <summary>After a job, the keyboard goes back to the control that started it, such as the switch just flipped.</summary>
    private void RestoreFocus(MainViewModel vm)
    {
        // Something else has it already: Back was pressed during the job, say.
        if (Keyboard.FocusedElement is { } focused && !ReferenceEquals(focused, this))
        {
            return;
        }

        GiveFocus(vm);
    }

    /// <summary>The control that last had the keyboard, if it is still there to take it, or else the page's first.</summary>
    private void GiveFocus(MainViewModel vm)
    {
        if (_lastFocus is UIElement { IsVisible: true, IsEnabled: true } last && PresentationSource.FromVisual(last) is not null)
        {
            last.Focus();
        }
        else
        {
            PlaceFocus(vm, cameFrom: null);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
