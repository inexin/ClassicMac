using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>How a note under a field reads: nothing to say, information, a warning that blocks, or an error.</summary>
public enum NoteSeverity
{
    None,
    Info,
    Warning,
    Error,
}

/// <summary>A snap point on Resize's slider: a size, its label under the track (none for the small floppies) and its tooltip.</summary>
public sealed record SizeMark(long Size, string? Label, string Tip);

/// <summary>A choice of Resize's block size select: <see langword="null"/> for Automatic.</summary>
public sealed record BlockSizeChoice(uint? Size, string Label);

/// <summary>
/// Resize's dialog (design/boards/volume-tools.md §4, V4): the new size as a number and a unit (a typed suffix moves into
/// the unit), the slider's range and snap points, the block size (Automatic, or a valid larger one), and a note updated as
/// you type: an error for what is no size or out of range, a warning (and Defragment first…) below what the free space
/// allows now, information when every file is laid out again. Resize runs with the progress state.
/// </summary>
public sealed partial class ResizeViewModel : VolumeOperation
{
    private const long K = 1024, M = K * K, G = M * K, Block = 512;
    private const uint LargestBlockSize = 32768;

    private static readonly SizeMark[] Classic =
    [
        new(400 * K, null, "400K floppy disk"),
        new(800 * K, null, "800K floppy disk"),
        new(1440 * K, "1.4M", "1.4 MB floppy disk"),
        new(20 * M, "20M", "20 MB hard disk"),
        new(100 * M, "100M", "100 MB Zip disk"),
    ];

    private readonly Func<long, uint?, IProgress<VolumeProgress>, CancellationToken, Task> resize;
    private bool setting;

    public ResizeViewModel(string volume, long size, VolumeLayout layout, long largest,
        Func<long, uint?, IProgress<VolumeProgress>, CancellationToken, Task> resize)
    {
        Volume = volume;
        Current = size;
        Largest = largest;
        this.resize = resize;
        this.layout = layout;
        SetSize(size);
    }

    public string Volume { get; }

    public string Title => $"Resize “{Volume}”";

    /// <summary>The volume's size now.</summary>
    public long Current { get; }

    /// <summary>The largest volume the writer makes.</summary>
    public long Largest { get; }

    /// <summary>The volume's layout: what it can shrink to now and after a defragmentation, its block size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Smallest), nameof(SmallestNow), nameof(SmallestLabel), nameof(Marks))]
    private VolumeLayout layout;

    /// <summary>The least the files need (after a defragmentation): the slider's left end.</summary>
    public long Smallest => Layout.SmallestSizeDefragmented;

    /// <summary>The least the free space allows now, without defragmenting.</summary>
    public long SmallestNow => Layout.SmallestSize;

    public string SmallestLabel => "Smallest " + Compact(Smallest);

    public string NowLabel => "Now " + Compact(Current);

    public string LargestLabel => "Largest " + Compact(Largest);

    public IReadOnlyList<string> Units { get; } = ["KB", "MB", "GB", "bytes"];

    /// <summary>The number as typed.</summary>
    [ObservableProperty]
    private string text = "";

    /// <summary>The unit select's choice.</summary>
    [ObservableProperty]
    private string unit = "KB";

    /// <summary>The new size in bytes (whole 512-byte blocks), or null when the text is no size.</summary>
    [ObservableProperty]
    private long? size;

    [ObservableProperty]
    private IReadOnlyList<BlockSizeChoice> blockSizes = [];

    [ObservableProperty]
    private BlockSizeChoice? selectedBlockSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalid), nameof(HasNote))]
    private NoteSeverity noteSeverity;

    [ObservableProperty]
    private string noteText = "";

    public bool HasNote => NoteSeverity != NoteSeverity.None;

    /// <summary>Whether the size box shows its error border.</summary>
    public bool IsInvalid => NoteSeverity == NoteSeverity.Error;

    /// <summary>The slider's value as read out ("500 KB, needs Defragment").</summary>
    public string ValueText => Size is null ? Text
        : $"{Text} {Unit}" + (NoteSeverity == NoteSeverity.Warning ? ", needs Defragment" : "");

    /// <summary>Opens Defragment from the warning; the layout after it, or null when it did not run.</summary>
    public Func<Task<VolumeLayout?>>? DefragmentFirst { get; set; }

    /// <summary>The slider's snap points: the classic disk sizes in range and the size now.</summary>
    public IReadOnlyList<SizeMark> Marks
    {
        get
        {
            var marks = Classic.Where(m => m.Size >= Smallest && m.Size <= Largest).ToList();
            if (!marks.Any(m => m.Size == Current))
            {
                marks.Add(new SizeMark(Current, null, "The size now"));
            }

            return [.. marks.OrderBy(m => m.Size)];
        }
    }

    /// <summary>A size in the board's short form: 403K, 1.4M, 2G.</summary>
    public static string Compact(long bytes) => In(bytes, "K", "M", "G");

    // A size with a spaced unit: 403 KB, 32 MB, 2 GB.
    private static string Spaced(long bytes) => In(bytes, " KB", " MB", " GB");

    private static string In(long bytes, string k, string m, string g) => bytes switch
    {
        < M => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)K:0.#}{k}"),
        < G => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)M:0.#}{m}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)G:0.#}{g}"),
    };

    /// <summary>Sets the size from the slider, in a unit that reads well (KB below 2 MB, MB below 1 GB, else GB).</summary>
    public void SetSize(long bytes)
    {
        setting = true;
        (Unit, Text) = bytes switch
        {
            < 2 * M => ("KB", (bytes / (double)K).ToString("0.#", CultureInfo.InvariantCulture)),
            < G => ("MB", (bytes / (double)M).ToString("0.##", CultureInfo.InvariantCulture)),
            _ => ("GB", (bytes / (double)G).ToString("0.##", CultureInfo.InvariantCulture)),
        };
        setting = false;
        Size = bytes;
        Update();
    }

    [GeneratedRegex(@"^\s*(?<number>[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\s*(?<unit>[a-z]*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    partial void OnTextChanged(string value)
    {
        if (setting)
        {
            return;
        }

        var match = SizePattern().Match(value);
        if (match.Success && UnitOf(match.Groups["unit"].Value) is { } typed && match.Groups["unit"].Length > 0)
        {
            setting = true;
            Unit = typed;
            Text = match.Groups["number"].Value;
            setting = false;
        }

        Read();
    }

    partial void OnUnitChanged(string value)
    {
        if (!setting)
        {
            Read();
        }
    }

    partial void OnSelectedBlockSizeChanged(BlockSizeChoice? value)
    {
        if (!setting)
        {
            Note();
        }
    }

    partial void OnLayoutChanged(VolumeLayout value) => Update();

    protected override void OnStateChanged()
    {
        StartCommand.NotifyCanExecuteChanged();
        DefragmentFirstCommand.NotifyCanExecuteChanged();
    }

    private static string? UnitOf(string suffix) => suffix.ToUpperInvariant() switch
    {
        "K" or "KB" or "KIB" => "KB",
        "M" or "MB" or "MIB" => "MB",
        "G" or "GB" or "GIB" => "GB",
        "B" or "BYTE" or "BYTES" => "bytes",
        _ => null,
    };

    // The text in the unit, to whole 512-byte blocks: decimals only for MB and GB.
    private void Read()
    {
        var match = SizePattern().Match(Text);
        long? read = null;
        if (match.Success && match.Groups["unit"].Length == 0 && UnitOf(Unit) is { } unitName)
        {
            var number = match.Groups["number"].Value;
            if (!number.Contains('.', StringComparison.Ordinal) || unitName is "MB" or "GB")
            {
                long factor = unitName switch { "KB" => K, "MB" => M, "GB" => G, _ => 1 };
                decimal bytes = decimal.Parse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * factor;
                decimal blocks = Math.Round(bytes / Block, MidpointRounding.AwayFromZero);
                read = blocks <= 0 ? null : blocks > long.MaxValue / Block ? long.MaxValue / Block * Block : (long)blocks * Block;
            }
        }

        Size = read;
        Update();
    }

    // The block size the writer takes when none is chosen: the volume's own while it keeps within 65,535 blocks, else the
    // initializer's for the size (hfs.md §3.2). Fitted with the size over the block size, a little over the writer's count
    // (which leaves out the boot blocks, MDB and bitmap), so near the limit this may say the blocks grow when they need not.
    private uint Automatic(long bytes) =>
        bytes / Layout.BlockSize <= ushort.MaxValue ? (uint)Layout.BlockSize : HfsWriter.AutomaticBlockSize(Math.Min(bytes, Largest));

    private void Update()
    {
        long bytes = Size is { } s && s <= Largest ? s : Current;
        uint automatic = Automatic(bytes);
        uint least = (uint)Math.Max(Block, (bytes / ushort.MaxValue + Block - 1) / Block * Block);
        var choices = new List<BlockSizeChoice> { new(null, string.Create(CultureInfo.InvariantCulture, $"Automatic · {automatic:N0} bytes")) };
        for (uint blockSize = least; blockSize <= LargestBlockSize; blockSize += (uint)Block)
        {
            if (blockSize != automatic)
            {
                choices.Add(new(blockSize, string.Create(CultureInfo.InvariantCulture, $"{blockSize:N0} bytes")));
            }
        }

        var chosen = SelectedBlockSize?.Size;
        setting = true;
        if (!choices.SequenceEqual(BlockSizes))
        {
            BlockSizes = choices;
        }

        SelectedBlockSize = BlockSizes.FirstOrDefault(c => c.Size == chosen) ?? BlockSizes[0];
        setting = false;
        Note();
    }

    private void Note()
    {
        (NoteSeverity, NoteText) = Explain(Size, SelectedBlockSize?.Size);
        OnPropertyChanged(nameof(ValueText));
        StartCommand.NotifyCanExecuteChanged();
        DefragmentFirstCommand.NotifyCanExecuteChanged();
    }

    private (NoteSeverity, string) Explain(long? typed, uint? chosen)
    {
        if (typed is not { } bytes)
        {
            return (NoteSeverity.Error, "Type a size, such as 800K or 20M.");
        }

        if (bytes > Largest)
        {
            return (NoteSeverity.Error, $"Too large: the largest HFS volume here is {Spaced(Largest)}.");
        }

        if (bytes < Smallest)
        {
            return (NoteSeverity.Error, $"Too small: the files need at least {Compact(Smallest)}.");
        }

        if (chosen is { } blockSize && blockSize != Layout.BlockSize)
        {
            return (NoteSeverity.Info, "Changing the block size lays every file out again, as Defragment does.");
        }

        if (Automatic(bytes) is var grown && grown != Layout.BlockSize)
        {
            return (NoteSeverity.Info, string.Create(CultureInfo.InvariantCulture,
                $"Above {Spaced(ushort.MaxValue * Layout.BlockSize)} the blocks must grow to {grown:N0} bytes, so every file is laid out again, as Defragment does."));
        }

        if (bytes < SmallestNow)
        {
            return (NoteSeverity.Warning, string.Create(CultureInfo.InvariantCulture,
                $"The free space lies in {Layout.FreeRuns.Count:N0} runs, so this volume can shrink only to {Compact(SmallestNow)} now. Defragment first to reach {Compact(Smallest)}."));
        }

        return (NoteSeverity.None, "");
    }

    private bool CanStart() => IsReady && Size is { } s && NoteSeverity is NoteSeverity.None or NoteSeverity.Info
        && (s != Current || SelectedBlockSize?.Size is { } c && c != Layout.BlockSize);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        long bytes = Size!.Value;
        uint? blockSize = SelectedBlockSize?.Size;
        if (await RunAsync("Resizing…", (progress, token) => resize(bytes, blockSize, progress, token)))
        {
            State = VolumeOperationState.Done;
        }
    }

    private bool CanDefragmentFirst() => IsReady && DefragmentFirst is not null && NoteSeverity == NoteSeverity.Warning;

    [RelayCommand(CanExecute = nameof(CanDefragmentFirst))]
    private async Task DefragmentFirstAsync()
    {
        if (await DefragmentFirst!() is { } after)
        {
            Layout = after;
        }
    }
}
