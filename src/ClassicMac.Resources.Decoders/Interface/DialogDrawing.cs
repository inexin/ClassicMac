using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Graphics;

namespace ClassicMac.Resources.Decoders.Interface;

/// <summary>How a dialog or alert is put on the screen.</summary>
public enum DialogKind
{
    /// <summary>A dialog (<c>'DLOG'</c>), drawn as <c>GetNewDialog</c> and <c>DrawDialog</c> draw it.</summary>
    Dialog,

    /// <summary>An alert shown with <c>Alert</c>: no icon.</summary>
    Alert,

    /// <summary>An alert shown with <c>StopAlert</c>: the stop icon.</summary>
    StopAlert,

    /// <summary>An alert shown with <c>NoteAlert</c>: the note icon.</summary>
    NoteAlert,

    /// <summary>An alert shown with <c>CautionAlert</c>: the caution icon.</summary>
    CautionAlert,

    /// <summary>A lone item list (<c>'DITL'</c>), drawn in a plain box around its items.</summary>
    ItemList,
}

/// <summary>An image an item or an alert draws: an <c>'ICON'</c>, a <c>'cicn'</c> or a <c>'PICT'</c>, as stored.</summary>
/// <param name="Type">The resource type.</param>
/// <param name="Data">The resource's data.</param>
public sealed record DialogImage(FourCC Type, byte[] Data);

/// <summary>A dialog item with what it draws from other resources.</summary>
/// <param name="Item">The item as read.</param>
/// <param name="Control">A control item's template, when its <c>'CNTL'</c> exists.</param>
/// <param name="Image">An icon's or picture's resource, when it exists.</param>
public sealed record DialogDrawingItem(DialogItem Item, ControlTemplate? Control, DialogImage? Image);

/// <summary>
/// A dialog, alert or item list as <see cref="DialogRenderer"/> draws it: the window's definition and content size,
/// and its items in the content's coordinates.
/// </summary>
/// <param name="Kind">How it is shown.</param>
/// <param name="Title">The window's title (none for an alert or an item list).</param>
/// <param name="Definition">The window definition ID (1, a modal dialog box, for an alert; 2, a plain box, for an item list).</param>
/// <param name="GoAway">Whether the window has a close box.</param>
/// <param name="Width">The content's width.</param>
/// <param name="Height">The content's height.</param>
/// <param name="Items">The items.</param>
/// <param name="DefaultItem">The item drawn with the default ring (an alert's stage 1 default), or 0.</param>
/// <param name="Content">The content colour from the <c>'dctb'</c> or <c>'actb'</c> of the same ID, or null for white.</param>
/// <param name="ThemeBackground">The <c>'dlgx'</c> asks for the theme's background.</param>
/// <param name="AlertIcon">A stop, note or caution alert's icon, when the forks given supply it.</param>
public sealed record DialogDrawing(DialogKind Kind, string Title, int Definition, bool GoAway, int Width, int Height,
    IReadOnlyList<DialogDrawingItem> Items, int DefaultItem, RgbColor? Content, bool ThemeBackground, DialogImage? AlertIcon)
{
    /// <summary>Whether it is an alert of any kind.</summary>
    public bool IsAlert => Kind is DialogKind.Alert or DialogKind.StopAlert or DialogKind.NoteAlert or DialogKind.CautionAlert;
}

/// <summary>Builds <see cref="DialogDrawing"/>s from their resources.</summary>
public static class DialogDrawings
{
    private static readonly FourCC Ditl = FourCC.FromString("DITL"), Cntl = FourCC.FromString("CNTL"), Icon = FourCC.FromString("ICON"),
        Cicn = FourCC.FromString("cicn"), Pict = FourCC.FromString("PICT"), Dctb = FourCC.FromString("dctb"), Actb = FourCC.FromString("actb"),
        Dlgx = FourCC.FromString("dlgx");

    /// <summary>
    /// The drawing of a <c>'DLOG'</c>, <c>'ALRT'</c> or <c>'DITL'</c>, with its item list, controls, icons, pictures
    /// and colours from <paramref name="fork"/>; null for other types.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="data">Its data (decompressed).</param>
    /// <param name="fork">The fork it is in, where its other resources are looked up.</param>
    /// <param name="options">Decoding options.</param>
    /// <param name="readOptions">How compressed resources are read.</param>
    /// <param name="diagnostics">Receives problems.</param>
    /// <param name="alertKind">For an <c>'ALRT'</c>: which of the Alert routines shows it.</param>
    /// <param name="systemForks">Forks standing in for the System file: the stop, note and caution icons
    /// (<c>'cicn'</c>, else <c>'ICON'</c>, IDs 0, 1 and 2) are looked up there first.</param>
    public static DialogDrawing? Read(Resource resource, ReadOnlyMemory<byte> data, ResourceFork fork, DecodeOptions options,
        ReadOptions readOptions, ICollection<Diagnostic> diagnostics, DialogKind alertKind = DialogKind.Alert,
        IReadOnlyList<ResourceFork>? systemForks = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(fork);
        systemForks ??= [];
        var what = resource.ToString();
        switch (resource.Type.ToString())
        {
            case "DLOG":
                {
                    var dialog = InterfaceResources.ReadWindow(data, true, options, diagnostics, what);
                    var items = Items(dialog.ItemsId ?? 0, fork, options, readOptions, diagnostics, systemForks);
                    var theme = fork.Find(Dlgx, resource.Id) is { } x && Data(x, fork, readOptions, diagnostics) is { Length: >= 6 } flags
                        && (new BigEndianReader(flags).ReadUInt32At(2) & 1) != 0;
                    return new DialogDrawing(DialogKind.Dialog, dialog.Title, dialog.Definition, dialog.GoAway, Width(dialog.Bounds), Height(dialog.Bounds),
                        items, 0, Content(fork.Find(Dctb, resource.Id), fork, readOptions, diagnostics), theme, null);
                }
            case "ALRT":
                {
                    var alert = InterfaceResources.ReadAlert(data, diagnostics, what);
                    var items = Items(alert.ItemsId, fork, options, readOptions, diagnostics, systemForks);
                    if (alertKind is DialogKind.Dialog or DialogKind.ItemList)
                    {
                        alertKind = DialogKind.Alert;
                    }

                    var icon = alertKind switch
                    {
                        DialogKind.StopAlert => IconImage(0, fork, readOptions, diagnostics, systemForks),
                        DialogKind.NoteAlert => IconImage(1, fork, readOptions, diagnostics, systemForks),
                        DialogKind.CautionAlert => IconImage(2, fork, readOptions, diagnostics, systemForks),
                        _ => null,
                    };
                    return new DialogDrawing(alertKind, "", 1, false, Width(alert.Bounds), Height(alert.Bounds), items, alert.Stage(1).BoldItem,
                        Content(fork.Find(Actb, resource.Id), fork, readOptions, diagnostics), false, icon);
                }
            case "DITL":
                {
                    var items = Build(InterfaceResources.ReadDialogItems(data, options, diagnostics, what), fork, options, readOptions, diagnostics, systemForks);
                    // A lone item list: a plain box around its items, with a margin [ClassicMac].
                    var right = items.Select(i => (int)i.Item.Bounds.Right).DefaultIfEmpty(100).Max() + 10;
                    var bottom = items.Select(i => (int)i.Item.Bounds.Bottom).DefaultIfEmpty(40).Max() + 10;
                    return new DialogDrawing(DialogKind.ItemList, "", 2, false, Math.Clamp(right, 1, 4096), Math.Clamp(bottom, 1, 4096), items, 0,
                        null, false, null);
                }
            default:
                return null;
        }
    }

    // A window colour table's content colour (part 0), if it has one.
    private static RgbColor? Content(Resource? table, ResourceFork fork, ReadOptions readOptions, ICollection<Diagnostic> diagnostics)
    {
        if (table is null)
        {
            return null;
        }

        var data = Data(table, fork, readOptions, diagnostics);
        if (data.Length < 8)
        {
            return null;
        }

        var reader = new BigEndianReader(data);
        var count = reader.ReadInt16At(6) + 1;
        for (var i = 0; i < count && 16 + i * 8 <= data.Length; i++)
        {
            var e = 8 + i * 8;
            if (reader.ReadInt16At(e) == 0)
            {
                return new RgbColor(reader.ReadUInt16At(e + 2), reader.ReadUInt16At(e + 4), reader.ReadUInt16At(e + 6));
            }
        }
        return null;
    }

    private static int Width(MacRect r) => Math.Clamp(r.Right - r.Left, 1, 4096);

    private static int Height(MacRect r) => Math.Clamp(r.Bottom - r.Top, 1, 4096);

    private static IReadOnlyList<DialogDrawingItem> Items(short id, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
        ICollection<Diagnostic> diagnostics, IReadOnlyList<ResourceFork> systemForks) =>
        fork.Find(Ditl, id) is { } list
            ? Build(InterfaceResources.ReadDialogItems(Data(list, fork, readOptions, diagnostics), options, diagnostics, list.ToString()),
                fork, options, readOptions, diagnostics, systemForks)
            : [];

    private static List<DialogDrawingItem> Build(IReadOnlyList<DialogItem> items, ResourceFork fork, DecodeOptions options, ReadOptions readOptions,
        ICollection<Diagnostic> diagnostics, IReadOnlyList<ResourceFork> systemForks) =>
        items.Select(item =>
        {
            ControlTemplate? control = null;
            DialogImage? image = null;
            if (item.ResourceId is { } id)
            {
                switch (item.Type)
                {
                    case 7 when fork.Find(Cntl, id) is { } c:
                        control = InterfaceResources.ReadControl(Data(c, fork, readOptions, diagnostics), options, diagnostics, c.ToString());
                        break;
                    case 32:
                        image = IconImage(id, fork, readOptions, diagnostics, systemForks);
                        break;
                    case 64 when fork.Find(Pict, id) is { } p:
                        image = new DialogImage(Pict, Data(p, fork, readOptions, diagnostics).ToArray());
                        break;
                }
            }
            return new DialogDrawingItem(item, control, image);
        }).ToList();

    // An icon item's image: a colour icon of the ID first, then the 'ICON'. Mac OS 9 draws IDs 0-2 as the system's
    // stop, note and caution icons, so those come from the System's stand-ins first.
    private static DialogImage? IconImage(short id, ResourceFork fork, ReadOptions readOptions, ICollection<Diagnostic> diagnostics,
        IReadOnlyList<ResourceFork> systemForks)
    {
        IEnumerable<ResourceFork> forks = id is >= 0 and <= 2 ? [.. systemForks, fork] : [fork];
        foreach (var f in forks)
        {
            foreach (var type in new[] { Cicn, Icon })
            {
                if (f.Find(type, id) is { } r)
                {
                    return new DialogImage(type, Data(r, f, readOptions, diagnostics).ToArray());
                }
            }
        }

        return null;
    }

    private static ReadOnlyMemory<byte> Data(Resource resource, ResourceFork fork, ReadOptions readOptions, ICollection<Diagnostic> diagnostics) =>
        ResourceDecompression.Default.GetData(resource, fork, readOptions, diagnostics);
}
