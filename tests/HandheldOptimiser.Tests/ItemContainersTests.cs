using System.Windows;
using System.Windows.Controls;
using HandheldOptimiser.HomeLauncher.Library;
using Xunit;

namespace HandheldOptimiser.Tests;

/// <summary>
/// Looking up the card at a place in a stack. Left on the first card of the Old School layout's settings
/// asked for the place before it, which closed the library: WPF's own lookup throws there.
/// </summary>
public class ItemContainersTests
{
    [Fact]
    public void ThereIsNoContainerBeforeTheFirstItemOrAfterTheLast()
    {
        OnAnInterfaceThread(() =>
        {
            var items = LaidOut(3);

            Assert.Null(ItemContainers.At(items, -1));
            Assert.Null(ItemContainers.At(items, -2));
            Assert.Null(ItemContainers.At(items, 3));
            Assert.Null(ItemContainers.At(items, 40));
        });
    }

    [Fact]
    public void EachItemHasItsOwnContainer()
    {
        OnAnInterfaceThread(() =>
        {
            var items = LaidOut(3);

            var first = ItemContainers.At(items, 0);
            var last = ItemContainers.At(items, 2);

            Assert.NotNull(first);
            Assert.NotNull(last);
            Assert.NotSame(first, last);
            Assert.Equal("card 0", first.Content);
            Assert.Equal("card 2", last.Content);
        });
    }

    [Fact]
    public void AnEmptyStackHasNoContainers()
    {
        OnAnInterfaceThread(() =>
        {
            var items = LaidOut(0);

            Assert.Null(ItemContainers.At(items, 0));
            Assert.Null(ItemContainers.At(items, -1));
        });
    }

    // With no application running there is no theme to give an items control its looks, and without a
    // template it lays nothing out and makes no containers. This is the least a template can be.
    private const string Template =
        "<ControlTemplate TargetType='ItemsControl' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"
        + "<ItemsPresenter /></ControlTemplate>";

    /// <summary>An items control that has made a container for each of its items, as one on screen has.</summary>
    private static ItemsControl LaidOut(int count)
    {
        var items = new ItemsControl
        {
            Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(Template),
            ItemsSource = Enumerable.Range(0, count).Select(i => $"card {i}").ToList()
        };

        items.Measure(new Size(400, 400));
        items.Arrange(new Rect(0, 0, 400, 400));
        items.UpdateLayout();
        return items;
    }

    /// <summary>Runs a test on a thread of the kind WPF's controls need, and passes on what it throws.</summary>
    private static void OnAnInterfaceThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
