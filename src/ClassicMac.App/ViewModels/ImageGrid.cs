using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels;

/// <summary>A row of image cards, as many as fit the preview's width.</summary>
public sealed record ImageRow(IReadOnlyList<ImageItem> Items);

/// <summary>The Finder states strip after the cards.</summary>
/// <param name="MaxWidth">The width the strip wraps at: the viewport less the margins.</param>
public sealed record FinderStatesRow(IReadOnlyList<ImageItem> Items, double MaxWidth);

// The image preview's grid (design/boards/main-window.md, P1): the summary line, Show masks and Finder states, and the
// cards cut into rows for the viewport's width, so a list of hundreds of images is a virtualising list of rows.
public sealed partial class ImageGrid(IAppView appView) : ObservableObject
{
    private const double CardGap = 12;

    private const double CardPadding = 26;                    // 12 padding and the 1 px border each side

    private const double MinCardWidth = 112;

    /// <summary>Whether an icon family's masks show as cards of their own.</summary>
    [ObservableProperty]
    private bool showMasks;

    /// <summary>Whether an icon's Finder states strip shows.</summary>
    [ObservableProperty]
    private bool showFinderStates = true;

    /// <summary>The width the cards have to fit (the view sets it).</summary>
    [ObservableProperty]
    private double imageViewportWidth;

    /// <summary>The cards cut into rows, then the Finder states strip.</summary>
    [ObservableProperty]
    private IReadOnlyList<object> imageRows = [];

    public bool HasMasks => appView.Preview.Masks.Count > 0;

    public bool HasFinderStates => appView.Preview.FinderStates.Count > 0;

    /// <summary>The Finder states at 2× when the zoom is 2 or more, else 1×.</summary>
    public IReadOnlyList<ImageItem> FinderStateItems
    {
        get
        {
            var zoom = appView.Zoom >= 2 ? 2 : 1;
            return appView.Preview.FinderStates.Select(s => new ImageItem(s, s.Width * zoom, s.Height * zoom, zoom)).ToList();
        }
    }

    /// <summary>The sub-bar: "6 members · 2× · nearest neighbour · 8-bit screen".</summary>
    public string ImageSummary
    {
        get
        {
            var count = appView.Preview.Images.Count;
            var noun = appView.Preview.IsFamily ? count == 1 ? "member" : "members" : count == 1 ? "image" : "images";
            return string.Create(CultureInfo.InvariantCulture, $"{count} {noun} · {appView.Zoom}× · nearest neighbour · {appView.ScreenDepth}-bit screen");
        }
    }

    /// <summary>Every card's width: the widest image at the zoom with its padding, at least room for the caption.</summary>
    public double ImageCardWidth => Math.Max(MinCardWidth, appView.Images.Count == 0 ? 0 : appView.Images.Max(i => i.Width) + CardPadding);

    partial void OnShowMasksChanged(bool value) => appView.Images = appView.ItemsAt(appView.Preview, appView.Zoom);

    partial void OnShowFinderStatesChanged(bool value) => LayOutImages();

    partial void OnImageViewportWidthChanged(double value) => LayOutImages();

    // MainViewModel.Images changed: the grid follows.
    internal void ImagesChanged()
    {
        OnPropertyChanged(nameof(ImageCardWidth));
        OnPropertyChanged(nameof(ImageSummary));
        OnPropertyChanged(nameof(FinderStateItems));
        OnPropertyChanged(nameof(HasMasks));
        OnPropertyChanged(nameof(HasFinderStates));
        LayOutImages();
    }

    // The cards in rows of as many as fit, then the Finder states.
    private void LayOutImages()
    {
        // The list has a 12 margin on each side and every card a 12 gap after it.
        var columns = ImageViewportWidth <= 0 ? 4 : Math.Max(1, (int)((ImageViewportWidth - 2 * CardGap) / (ImageCardWidth + CardGap)));
        var rows = new List<object>();
        for (var i = 0; i < appView.Images.Count; i += columns)
        {
            rows.Add(new ImageRow(appView.Images.Skip(i).Take(columns).ToList()));
        }

        if (ShowFinderStates && HasFinderStates)
        {
            rows.Add(new FinderStatesRow(FinderStateItems, ImageViewportWidth <= 0 ? double.PositiveInfinity : Math.Max(0, ImageViewportWidth - 2 * CardGap)));
        }

        ImageRows = rows;
    }
}
