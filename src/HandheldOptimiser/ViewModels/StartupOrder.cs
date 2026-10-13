namespace HandheldOptimiser.ViewModels;

/// <summary>
/// The two things the app does as it opens, and in what order: check for a newer version of itself,
/// and read everything it shows. Apart from the view model so the order can be tested.
/// </summary>
public static class StartupOrder
{
    /// <summary>
    /// Ordinarily the two run side by side, since neither needs the other. Opened only to install an
    /// update (the library's Update now), the read waits: the app is about to close, and a Store check
    /// cut off midway would leave the updates it found paused in the Store's queue. If the update then
    /// does not happen and the app stays open, the read runs after all. Without it every card on the
    /// Dashboard would go on saying "Checking…" with nothing being checked.
    /// </summary>
    /// <param name="checkForUpdates">Answers true when the app is closing to install an update.</param>
    public static async Task RunAsync(bool installUpdate, Func<Task<bool>> checkForUpdates, Func<Task> scan)
    {
        if (!installUpdate)
        {
            await Task.WhenAll(checkForUpdates(), scan());
            return;
        }

        if (!await checkForUpdates())
        {
            await scan();
        }
    }
}
