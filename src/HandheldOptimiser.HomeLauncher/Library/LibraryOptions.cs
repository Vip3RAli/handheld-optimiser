namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// The player's display choices, as the library uses them while it is drawn. They are read from
/// <see cref="LibrarySettings"/> each time the library comes to the front, since the main app or another
/// session can change them. Setting one here saves it too.
/// </summary>
internal sealed class LibraryOptions
{
    private BackgroundKind _background = LibrarySettings.Background;
    private ColourStrength _strength = LibrarySettings.Strength;
    private ArtworkBlur _blur = LibrarySettings.Blur;
    private ArtworkPosition _position = LibrarySettings.Position;
    private bool _showStatus = LibrarySettings.ShowStatus;
    private bool _showFilter = LibrarySettings.ShowFilter;
    private bool _quickActions = LibrarySettings.QuickActions;
    private bool _showHidden = LibrarySettings.ShowHidden;
    private SortOrder _sort = LibrarySettings.Sort;
    private bool _continuePlaying = LibrarySettings.ContinuePlaying;
    private bool _continueInfo = LibrarySettings.ContinueInfo;
    private bool _storeDetails = LibrarySettings.StoreDetails;

    /// <summary>
    /// Reads the background, the tiles' and the menu's choices again. The Last played choices and the store
    /// tabs each change what is laid out, so they are read by their own methods, which say what changed.
    /// </summary>
    public void Reload()
    {
        _background = LibrarySettings.Background;
        _strength = LibrarySettings.Strength;
        _blur = LibrarySettings.Blur;
        _position = LibrarySettings.Position;
        _showStatus = LibrarySettings.ShowStatus;
        _quickActions = LibrarySettings.QuickActions;
        _showHidden = LibrarySettings.ShowHidden;
        _sort = LibrarySettings.Sort;
    }

    /// <summary>Reads the Last played choices again.</summary>
    /// <returns>Whether any of them changed, and whether the panel beside the card did.</returns>
    public (bool Changed, bool InfoChanged) ReloadContinue()
    {
        var infoChanged = _continueInfo != LibrarySettings.ContinueInfo || _storeDetails != LibrarySettings.StoreDetails;
        var changed = infoChanged || _continuePlaying != LibrarySettings.ContinuePlaying;
        _continuePlaying = LibrarySettings.ContinuePlaying;
        _continueInfo = LibrarySettings.ContinueInfo;
        _storeDetails = LibrarySettings.StoreDetails;
        return (changed, infoChanged);
    }

    /// <summary>Reads the store tabs choice again.</summary>
    /// <returns>Whether it changed, so the strip is drawn again.</returns>
    public bool ReloadShowFilter()
    {
        if (_showFilter == LibrarySettings.ShowFilter)
        {
            return false;
        }

        _showFilter = LibrarySettings.ShowFilter;
        return true;
    }

    public BackgroundKind Background
    {
        get => _background;
        set => LibrarySettings.Background = _background = value;
    }

    public ColourStrength Strength
    {
        get => _strength;
        set => LibrarySettings.Strength = _strength = value;
    }

    public ArtworkBlur Blur
    {
        get => _blur;
        set => LibrarySettings.Blur = _blur = value;
    }

    public ArtworkPosition Position
    {
        get => _position;
        set => LibrarySettings.Position = _position = value;
    }

    /// <summary>The battery and Wi-Fi in the top bar.</summary>
    public bool ShowStatus
    {
        get => _showStatus;
        set => LibrarySettings.ShowStatus = _showStatus = value;
    }

    /// <summary>A tab for the favourites and for each store.</summary>
    public bool ShowFilter
    {
        get => _showFilter;
        set => LibrarySettings.ShowFilter = _showFilter = value;
    }

    /// <summary>X, or a press and hold, opens a game's quick actions.</summary>
    public bool QuickActions
    {
        get => _quickActions;
        set => LibrarySettings.QuickActions = _quickActions = value;
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set => LibrarySettings.ShowHidden = _showHidden = value;
    }

    public SortOrder Sort
    {
        get => _sort;
        set => LibrarySettings.Sort = _sort = value;
    }

    /// <summary>The Last played card above the grid.</summary>
    public bool ContinuePlaying
    {
        get => _continuePlaying;
        set => LibrarySettings.ContinuePlaying = _continuePlaying = value;
    }

    /// <summary>The panel beside the Last played card.</summary>
    public bool ContinueInfo
    {
        get => _continueInfo;
        set => LibrarySettings.ContinueInfo = _continueInfo = value;
    }

    /// <summary>What the game's store says about it, in the panel beside the Last played card.</summary>
    public bool StoreDetails
    {
        get => _storeDetails;
        set => LibrarySettings.StoreDetails = _storeDetails = value;
    }
}
