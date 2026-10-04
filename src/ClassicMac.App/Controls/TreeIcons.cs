using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Controls;

/// <summary>
/// The tree's 16 × 16 pixel icons, one per kind of node (design/boards/browse-tree.md, T4): Finder-like, each with a
/// black outline, drawn for ClassicMac (not Apple's art).
/// </summary>
internal static class TreeIcons
{
    public const int Size = 16;

    private static readonly Dictionary<char, uint> Palette = new()
    {
        ['.'] = 0x00000000,
        ['K'] = 0xFF000000,
        ['W'] = 0xFFFFFFFF,
        ['G'] = 0xFFBBBBBB,
        ['D'] = 0xFF555555,
        ['g'] = 0xFF22B14C,
        ['T'] = 0xFFD9B98C,
        ['S'] = 0xFF7A4A1E,
        ['L'] = 0xFFC9C0F2,
        ['P'] = 0xFF8A62C8,
        ['B'] = 0xFF2E5CD6,
        ['b'] = 0xFF9DB6F2,
    };

    /// <summary>The art of each kind, row by row; '.' is transparent (see <see cref="Palette"/>).</summary>
    public static IReadOnlyDictionary<TreeIconKind, string[]> Art { get; } = new Dictionary<TreeIconKind, string[]>
    {
        // A hard disk: grey body, a green light.
        [TreeIconKind.HardDisk] =
        [
            "................",
            "................",
            "................",
            "................",
            ".KKKKKKKKKKKKKK.",
            "KWWWWWWWWWWWWWWK",
            "KWGGGGGGGGGGGGDK",
            "KWGGGGGGGGGGGGDK",
            "KWGGGGGGGGGggGDK",
            "KWGGGGGGGGGGGGDK",
            "KWGGGGGGGGGGGGDK",
            "KDDDDDDDDDDDDDDK",
            ".KKKKKKKKKKKKKK.",
            "..KK........KK..",
            "................",
            "................",
        ],
        // A floppy disk: dark body, the metal shutter, a white label.
        [TreeIconKind.Floppy] =
        [
            "................",
            "KKKKKKKKKKKKKKK.",
            "KDDDGGGGGGGDDDDK",
            "KDDDGGGDDGGDDDDK",
            "KDDDGGGDDGGDDDDK",
            "KDDDGGGGGGGDDDDK",
            "KDDDDDDDDDDDDDDK",
            "KDDWWWWWWWWWWDDK",
            "KDDWDDDDDDDDWDDK",
            "KDDWWWWWWWWWWDDK",
            "KDDWDDDDDDWWWDDK",
            "KDDWWWWWWWWWWDDK",
            "KDDWWWWWWWWWWDDK",
            "KDDWWWWWWWWWWDDK",
            "KKKKKKKKKKKKKKKK",
            "................",
        ],
        // A parcel: a tan box tied with string.
        [TreeIconKind.Parcel] =
        [
            "................",
            ".....SS..SS.....",
            "......SSSS......",
            ".KKKKKKSSKKKKKK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KSSSSSSSSSSSSK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KTTTTTSSTTTTTK.",
            ".KKKKKKKKKKKKKK.",
            "................",
        ],
        // A folder with its tab, lavender.
        [TreeIconKind.Folder] =
        [
            "................",
            "................",
            "..KKKKK.........",
            ".KLLLLLK........",
            "KKKKKKKKKKKKKKK.",
            "KWWWWWWWWWWWWLK.",
            "KWLLLLLLLLLLLLK.",
            "KWLLLLLLLLLLLLK.",
            "KWLLLLLLLLLLLLK.",
            "KWLLLLLLLLLLLLK.",
            "KWLLLLLLLLLLLLK.",
            "KWLLLLLLLLLLLLK.",
            "KLLLLLLLLLLLLLK.",
            "KKKKKKKKKKKKKKK.",
            "................",
            "................",
        ],
        // An application: a purple diamond.
        [TreeIconKind.Application] =
        [
            ".......KK.......",
            "......KWPK......",
            ".....KWPPPK.....",
            "....KWPPPPPK....",
            "...KWPPPPPPPK...",
            "..KWPPPPPPPPPK..",
            ".KWPPPPPPPPPPPK.",
            "KWPPPPPPPPPPPPPK",
            "KPPPPPPPPPPPPPPK",
            ".KPPPPPPPPPPPPK.",
            "..KPPPPPPPPPPK..",
            "...KPPPPPPPPK...",
            "....KPPPPPPK....",
            ".....KPPPPK.....",
            "......KPPK......",
            ".......KK.......",
        ],
        // A document: a page with a folded corner and lines of text.
        [TreeIconKind.Document] =
        [
            "..KKKKKKKK......",
            "..KWWWWWWKK.....",
            "..KWWWWWWKWK....",
            "..KWWWWWWKWWK...",
            "..KWWWWWWKKKKK..",
            "..KWWWWWWWWWWK..",
            "..KWDDDDDDDWWK..",
            "..KWWWWWWWWWWK..",
            "..KWDDDDDDDDWK..",
            "..KWWWWWWWWWWK..",
            "..KWDDDDDDDDWK..",
            "..KWWWWWWWWWWK..",
            "..KWDDDDDWWWWK..",
            "..KWWWWWWWWWWK..",
            "..KWWWWWWWWWWK..",
            "..KKKKKKKKKKKK..",
        ],
        // A "No name" group: two pages with folded corners, the back one up and to the right.
        [TreeIconKind.NoNameGroup] =
        [
            ".....KKKKKK.....",
            ".....KWWWWKK....",
            ".....KWWWWKWK...",
            ".....KWWWWKKKK..",
            "..KKKKKKWWWWWK..",
            "..KWWWWKKWWWWK..",
            "..KWWWWKWKWWWK..",
            "..KWWWWKKKKWWK..",
            "..KWWWWWWWKWWK..",
            "..KWDDDDDWKWWK..",
            "..KWWWWWWWKWWK..",
            "..KWDDDDDWKKKK..",
            "..KWWWWWWWK.....",
            "..KWDDDWWWK.....",
            "..KWWWWWWWK.....",
            "..KKKKKKKKK.....",
        ],
        // A resource type: two stacked cards with blue lines.
        [TreeIconKind.ResourceType] =
        [
            "................",
            "....KKKKKKKKKKK.",
            "....KWWWWWWWWWK.",
            "....KWbbbbbbbWK.",
            "..KKKKKKKKKKKWK.",
            "..KWWWWWWWWWKWK.",
            "..KWBBBBBBBWKWK.",
            "..KWWWWWWWWWKKK.",
            "..KWBBBBBWWWK...",
            "..KWWWWWWWWWK...",
            "..KWBBBBBBBWK...",
            "..KWWWWWWWWWK...",
            "..KWBBBBWWWWK...",
            "..KWWWWWWWWWK...",
            "..KKKKKKKKKKK...",
            "................",
        ],
        // A resource: a card with a blue edge and a 2 × 3 grid of black bytes.
        [TreeIconKind.Resource] =
        [
            "................",
            "................",
            "...KKKKKKKKKK...",
            "...KBWWWWWWWK...",
            "...KBWKKWKKWK...",
            "...KBWKKWKKWK...",
            "...KBWWWWWWWK...",
            "...KBWKKWKKWK...",
            "...KBWKKWKKWK...",
            "...KBWWWWWWWK...",
            "...KBWKKWKKWK...",
            "...KBWKKWKKWK...",
            "...KBWWWWWWWK...",
            "...KKKKKKKKKK...",
            "................",
            "................",
        ],
    };

    public const int LargeSize = 32;

    /// <summary>
    /// The same kinds drawn at 32 × 32 for the inspector header's tile (never the 16-pixel art scaled up), in the same
    /// palette and style.
    /// </summary>
    public static IReadOnlyDictionary<TreeIconKind, string[]> LargeArt { get; } = new Dictionary<TreeIconKind, string[]>
    {
        // A hard disk: grey body with a highlight, vents, a green light and feet.
        [TreeIconKind.HardDisk] =
        [
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "..KKKKKKKKKKKKKKKKKKKKKKKKKKKK..",
            ".KWWWWWWWWWWWWWWWWWWWWWWWWWWWWK.",
            ".KWGGGGGGGGGGGGGGGGGGGGGGGGGGDK.",
            ".KWGGGGGGGGGGGGGGGGGGGGGGGGGGDK.",
            ".KWGGGGGGGGGGGGGGGGGGGGGGGGGGDK.",
            ".KWGGGGGGGGGGGGGGGGGGGGGGGGGGDK.",
            ".KWGGDGGDGGDGGDGGDGGGGGGGGGGGDK.",
            ".KWGGDGGDGGDGGDGGDGGGGGGgggGGDK.",
            ".KWGGDGGDGGDGGDGGDGGGGGGgggGGDK.",
            ".KWGGDGGDGGDGGDGGDGGGGGGGGGGGDK.",
            ".KWGGGGGGGGGGGGGGGGGGGGGGGGGGDK.",
            ".KWGGGGGGGGGGGGGGGGGGGGGGGGGGDK.",
            ".KDDDDDDDDDDDDDDDDDDDDDDDDDDDDK.",
            ".KDDDDDDDDDDDDDDDDDDDDDDDDDDDDK.",
            "..KKKKKKKKKKKKKKKKKKKKKKKKKKKK..",
            "....KKKK................KKKK....",
            "....KKKK................KKKK....",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
        ],
        // A floppy disk: dark body with a bevelled corner, the metal shutter, a white label.
        [TreeIconKind.Floppy] =
        [
            "................................",
            "................................",
            "..KKKKKKKKKKKKKKKKKKKKKKKKKK....",
            "..KDDDDDKGGGGGGGGGGGGGKDDDDDK...",
            "..KDDDDDKGGGGGGGDDDDGGKDDDDDDK..",
            "..KDDDDDKGGGGGGGDDDDGGKDDDDDDK..",
            "..KDDDDDKGGGGGGGDDDDGGKDDDDDDK..",
            "..KDDDDDKGGGGGGGDDDDGGKDDDDDDK..",
            "..KDDDDDKGGGGGGGDDDDGGKDDDDDDK..",
            "..KDDDDDKGGGGGGGDDDDGGKDDDDDDK..",
            "..KDDDDDKGGGGGGGGGGGGGKDDDDDDK..",
            "..KDDDDDKKKKKKKKKKKKKKKDDDDDDK..",
            "..KDDDDDDDDDDDDDDDDDDDDDDDDDDK..",
            "..KDDDDDDDDDDDDDDDDDDDDDDDDDDK..",
            "..KDDDDDDDDDDDDDDDDDDDDDDDDDDK..",
            "..KDDDKKKKKKKKKKKKKKKKKKKKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWDDDDDDDDDDDDDDWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWDDDDDDDDDDDDDDWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWDDDDDDDDDWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KDDDKWWWWWWWWWWWWWWWWWWKDDDK..",
            "..KKKKKKKKKKKKKKKKKKKKKKKKKKKK..",
            "................................",
            "................................",
        ],
        // A parcel: a tan box tied with string and a bow.
        [TreeIconKind.Parcel] =
        [
            "................................",
            "................................",
            "........SSSSSSS..SSSSSSS........",
            "........S.....S..S.....S........",
            "........S.....S..S.....S........",
            "........S.....SSSS.....S........",
            "........S.....SSSS.....S........",
            "........SSSSSSSSSSSSSSSS........",
            "..KKKKKKKKKKKKSSSSKKKKKKKKKKKK..",
            "..KWWWWWWWWWWWWSSWWWWWWWWWWWWK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KSSSSSSSSSSSSSSSSSSSSSSSSSSK..",
            "..KSSSSSSSSSSSSSSSSSSSSSSSSSSK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KTTTTTTTTTTTTSSTTTTTTTTTTTTK..",
            "..KKKKKKKKKKKKKKKKKKKKKKKKKKKK..",
            "................................",
            "................................",
        ],
        // A folder with its tab, lavender.
        [TreeIconKind.Folder] =
        [
            "................................",
            "................................",
            "................................",
            "................................",
            "................................",
            "...KKKKKKKKKK...................",
            "..KWWWWWWWWWWK..................",
            "..KLLLLLLLLLLK..................",
            "..KLLLLLLLLLLK..................",
            ".KKLLLLLLLLLLLKKKKKKKKKKKKKKKKK.",
            ".KKKKKKKKKKKKKKKKKKKKKKKKKKKKKK.",
            ".KWWWWWWWWWWWWWWWWWWWWWWWWWWWLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KWLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KLLLLLLLLLLLLLLLLLLLLLLLLLLLLK.",
            ".KKKKKKKKKKKKKKKKKKKKKKKKKKKKKK.",
            "................................",
            "................................",
            "................................",
            "................................",
        ],
        // An application: a purple diamond with a light core.
        [TreeIconKind.Application] =
        [
            "................................",
            "...............KK...............",
            "..............KWPK..............",
            ".............KWPPPK.............",
            "............KWPPPPPK............",
            "...........KWPPPPPPPK...........",
            "..........KWPPPPPPPPPK..........",
            ".........KWPPPPPPPPPPPK.........",
            "........KWPPPPPPPPPPPPPK........",
            ".......KWPPPPPPPPPPPPPPPK.......",
            "......KWPPPPPPPPPPPPPPPPPK......",
            ".....KWPPPPPPPPPPPPPPPPPPPK.....",
            "....KWPPPPPPPPPLLPPPPPPPPPPK....",
            "...KWPPPPPPPPPLLLLPPPPPPPPPPK...",
            "..KWPPPPPPPPPLLLLLLPPPPPPPPPPK..",
            ".KWPPPPPPPPPLLLLLLLLPPPPPPPPPPK.",
            ".KPPPPPPPPPPLLLLLLLLPPPPPPPPPPK.",
            "..KPPPPPPPPPPLLLLLLPPPPPPPPPPK..",
            "...KPPPPPPPPPPLLLLPPPPPPPPPPK...",
            "....KPPPPPPPPPPLLPPPPPPPPPPK....",
            ".....KPPPPPPPPPPPPPPPPPPPPK.....",
            "......KPPPPPPPPPPPPPPPPPPK......",
            ".......KPPPPPPPPPPPPPPPPK.......",
            "........KPPPPPPPPPPPPPPK........",
            ".........KPPPPPPPPPPPPK.........",
            "..........KPPPPPPPPPPK..........",
            "...........KPPPPPPPPK...........",
            "............KPPPPPPK............",
            ".............KPPPPK.............",
            "..............KPPK..............",
            "...............KK...............",
            "................................",
        ],
        // A document: a page with a folded corner and lines of text.
        [TreeIconKind.Document] =
        [
            "................................",
            ".....KKKKKKKKKKKKKKK............",
            ".....KWWWWWWWWWWWWWKK...........",
            ".....KWWWWWWWWWWWWWKWK..........",
            ".....KWWWWWWWWWWWWWKWWK.........",
            ".....KWWWWWWWWWWWWWKWWWK........",
            ".....KWWWWWWWWWWWWWKWWWWK.......",
            ".....KWWWWWWWWWWWWWKWWWWWK......",
            ".....KWWWWWWWWWWWWWKKKKKKKK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWDDDDDDDDDDDDWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWDDDDDDDDDDDDDDWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWDDDDDDDDDDDDDWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWDDDDDDDDDDDDDDWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWDDDDDDDDDWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KWWWWWWWWWWWWWWWWWWWWK.....",
            ".....KKKKKKKKKKKKKKKKKKKKKK.....",
            "................................",
        ],
        // A "No name" group: two pages with folded corners, the back one up and to the right.
        [TreeIconKind.NoNameGroup] =
        [
            "................................",
            "...........KKKKKKKKKKKKK........",
            "...........KWWWWWWWWWWWKK.......",
            "...........KWWWWWWWWWWWKWK......",
            "...........KWWWWWWWWWWWKWWK.....",
            "...........KWWWWWWWWWWWKWWWK....",
            "...........KWWWWWWWWWWWKKKKKK...",
            "...........KWWWWWWWWWWWWWWWWK...",
            "...KKKKKKKKKKKKKK.....WWWWWWK...",
            "...KWWWWWWWWWWWWKK....WWWWWWK...",
            "...KWWWWWWWWWWWWKWK...DWWWWWK...",
            "...KWWWWWWWWWWWWKWWK..WWWWWWK...",
            "...KWWWWWWWWWWWWKWWWK.WWWWWWK...",
            "...KWWWWWWWWWWWWKKKKKKDDWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKWWWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKWWWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKDDWWWWK...",
            "...KWWDDDDDDDDDWWWWWWKWWWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKWWWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKWWWWWWK...",
            "...KWWDDDDDDDDDDDWWWWKWWWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKWWWWWWK...",
            "...KWWWWWWWWWWWWWWWWWKKKKKKKK...",
            "...KWWDDDDDDDDDDDWWWWK..........",
            "...KWWWWWWWWWWWWWWWWWK..........",
            "...KWWWWWWWWWWWWWWWWWK..........",
            "...KWWDDDDDDDDDDDWWWWK..........",
            "...KWWWWWWWWWWWWWWWWWK..........",
            "...KWWWWWWWWWWWWWWWWWK..........",
            "...KWWWWWWWWWWWWWWWWWK..........",
            "...KKKKKKKKKKKKKKKKKKK..........",
            "................................",
        ],
        // A resource type: two stacked cards with blue lines.
        [TreeIconKind.ResourceType] =
        [
            "................................",
            "................................",
            ".........KKKKKKKKKKKKKKKKKKKKK..",
            ".........KWWWWWWWWWWWWWWWWWWWK..",
            ".........KWWWWWWWWWWWWWWWWWWWK..",
            ".........KWWbbbbbbbbbbbbbWWWWK..",
            ".........KWWbbbbbbbbbbbbbWWWWK..",
            ".........KWWWWWWWWWWWWWWWWWWWK..",
            ".........KWWWWWWWWWWWWWWWWWWWK..",
            "..KKKKKKKKKKKKKKKKKKKKKWWWWWWK..",
            "..KWWWWWWWWWWWWWWWWWWWKWWWWWWK..",
            "..KWWWWWWWWWWWWWWWWWWWKWWWWWWK..",
            "..KWWBBBBBBBBBBBBBWWWWKWWWWWWK..",
            "..KWWBBBBBBBBBBBBBWWWWKbbWWWWK..",
            "..KWWWWWWWWWWWWWWWWWWWKbbWWWWK..",
            "..KWWWWWWWWWWWWWWWWWWWKWWWWWWK..",
            "..KWWBBBBBBBBBWWWWWWWWKWWWWWWK..",
            "..KWWBBBBBBBBBWWWWWWWWKWWWWWWK..",
            "..KWWWWWWWWWWWWWWWWWWWKWWWWWWK..",
            "..KWWWWWWWWWWWWWWWWWWWKWWWWWWK..",
            "..KWWBBBBBBBBBBBBBWWWWKWWWWWWK..",
            "..KWWBBBBBBBBBBBBBWWWWKKKKKKKK..",
            "..KWWWWWWWWWWWWWWWWWWWK.........",
            "..KWWWWWWWWWWWWWWWWWWWK.........",
            "..KWWBBBBBBBWWWWWWWWWWK.........",
            "..KWWBBBBBBBWWWWWWWWWWK.........",
            "..KWWWWWWWWWWWWWWWWWWWK.........",
            "..KWWWWWWWWWWWWWWWWWWWK.........",
            "..KWWWWWWWWWWWWWWWWWWWK.........",
            "..KKKKKKKKKKKKKKKKKKKKK.........",
            "................................",
            "................................",
        ],
        // A resource: a card with a blue edge and a 3 × 4 grid of black bytes.
        [TreeIconKind.Resource] =
        [
            "................................",
            "................................",
            "................................",
            "......KKKKKKKKKKKKKKKKKKKK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWWWWWWWWWWWWWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KBBBWWKKKWKKKWKKKWWK......",
            "......KKKKKKKKKKKKKKKKKKKK......",
            "................................",
            "................................",
            "................................",
        ],
    };

    private static readonly Dictionary<TreeIconKind, Bitmap> Bitmaps = [];
    private static readonly Dictionary<TreeIconKind, Bitmap> LargeBitmaps = [];

    /// <summary>The art's pixels as ARGB words, row by row.</summary>
    public static uint[] Pixels(TreeIconKind kind) => Pixels(kind, Art[kind], Size);

    /// <summary>The 32-pixel art's pixels as ARGB words, row by row.</summary>
    public static uint[] LargePixels(TreeIconKind kind) => Pixels(kind, LargeArt[kind], LargeSize);

    private static uint[] Pixels(TreeIconKind kind, string[] art, int size)
    {
        if (art.Length != size)
        {
            throw new InvalidOperationException($"{kind}: {art.Length} rows");
        }

        var pixels = new uint[size * size];
        for (int y = 0; y < size; y++)
        {
            if (art[y].Length != size)
            {
                throw new InvalidOperationException($"{kind}: row {y} has {art[y].Length} pixels");
            }

            for (int x = 0; x < size; x++)
            {
                pixels[y * size + x] = Palette[art[y][x]];
            }
        }
        return pixels;
    }

    /// <summary>A node kind's icon (a diagnostics group's header): the kind icon its rows would show.</summary>
    /// <summary>An icon kind's icon (the empty state's Recent list and drop zone).</summary>
    public static IValueConverter IconConverter { get; } = new FuncValueConverter<TreeIconKind, Bitmap?>(For);

    public static IValueConverter KindConverter { get; } = new FuncValueConverter<NodeKind, Bitmap?>(kind => For(kind switch
    {
        NodeKind.Input => TreeIconKind.HardDisk,
        NodeKind.Container => TreeIconKind.Floppy,
        NodeKind.File => TreeIconKind.Document,
        NodeKind.ResourceType => TreeIconKind.ResourceType,
        NodeKind.Resource => TreeIconKind.Resource,
        NodeKind.NoNameGroup => TreeIconKind.NoNameGroup,
        _ => TreeIconKind.Folder,
    }));

    /// <summary>A kind's 32-pixel icon (the inspector header's tile, when the selection has no large icon of its own).</summary>
    public static IValueConverter LargeConverter { get; } = new FuncValueConverter<TreeIconKind, Bitmap?>(LargeFor);

    /// <summary>The kind's icon as a bitmap (made once, on the UI thread); none for the loading placeholder.</summary>
    public static Bitmap? For(TreeIconKind kind) => Art.ContainsKey(kind) ? Make(Bitmaps, kind, Pixels, Size) : null;

    /// <summary>The kind's 32-pixel icon as a bitmap (made once, on the UI thread); none for the loading placeholder.</summary>
    public static Bitmap? LargeFor(TreeIconKind kind) => LargeArt.ContainsKey(kind) ? Make(LargeBitmaps, kind, LargePixels, LargeSize) : null;

    private static Bitmap Make(Dictionary<TreeIconKind, Bitmap> made, TreeIconKind kind, Func<TreeIconKind, uint[]> art, int size)
    {
        if (made.TryGetValue(kind, out var bitmap))
        {
            return bitmap;
        }

        var writeable = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var pixels = art(kind);
        using (var locked = writeable.Lock())
        {
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy((int[])(object)pixels, y * size, locked.Address + y * locked.RowBytes, size);
            }
        }

        return made[kind] = writeable;
    }
}

/// <summary>
/// A tree row's 16 DIP icon slot: the node's own icon once resolved, else its kind's, drawn 1:1 in device pixels
/// (1 device pixel per Mac pixel at 100%, 125% and 150%, centred; 2 at 200%; never 1.25 or 1.5: design/TOKENS.md).
/// When the row first shows on screen it asks the node to resolve its own icon; rows never scrolled into view never do.
/// </summary>
internal sealed class TreeIcon : PixelControl
{
    public static readonly StyledProperty<NodeViewModel?> NodeProperty = AvaloniaProperty.Register<TreeIcon, NodeViewModel?>(nameof(Node));

    // The alias badge, 7 × 7 pixels: a box with an arrow up and to the right ('#' black, '.' white).
    private static readonly string[] AliasBadge =
    [
        "#######",
        "#.###.#",
        "#..##.#",
        "#.#.#.#",
        "##....#",
        "#.....#",
        "#######",
    ];

    // The mark on a broken alias's badge, 5 × 5 at the icon's bottom right: a cross (the original missing), a disk (its
    // disk not open), a globe (on a network volume); '#' the mark's colour, '.' white.
    private static readonly string[] MissingMark = ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"];
    private static readonly string[] DiskMark = ["#####", "#.#.#", "#####", "#...#", "#####"];
    private static readonly string[] NetworkMark = [".###.", "#.#.#", "#####", "#.#.#", ".###."];

    private NodeViewModel? watched;
    private Bitmap? own;
    private bool watchingViewport;

    static TreeIcon() => AffectsRender<TreeIcon>(NodeProperty);

    public NodeViewModel? Node
    {
        get => GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != NodeProperty)
        {
            return;
        }

        if (watched is not null)
        {
            watched.PropertyChanged -= OnNodeChanged;
        }

        watched = Node;
        if (watched is not null)
        {
            watched.PropertyChanged += OnNodeChanged;
        }

        LoadOwn();
        WatchViewport(watched is not null && watched.IconPng is null);
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.AliasState))
        {
            InvalidateVisual();
            return;
        }

        if (e.PropertyName != nameof(NodeViewModel.IconPng))
        {
            return;
        }

        LoadOwn();
        InvalidateVisual();
    }

    private void LoadOwn()
    {
        own?.Dispose();
        own = watched?.IconPng is { } png ? new Bitmap(new MemoryStream(png)) : null;
    }

    private void WatchViewport(bool watch)
    {
        if (watch == watchingViewport)
        {
            return;
        }

        watchingViewport = watch;
        if (watch)
        {
            EffectiveViewportChanged += OnViewportChanged;
        }
        else
        {
            EffectiveViewportChanged -= OnViewportChanged;
        }
    }

    // On screen: resolve the node's own icon (once; the node keeps it).
    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (Node is not { } node || !IsEffectivelyVisible || Bounds.Width <= 0)
        {
            return;
        }

        if (!e.EffectiveViewport.Intersects(new Rect(Bounds.Size)))
        {
            return;
        }

        WatchViewport(false);
        _ = node.RequestIconAsync();
    }

    protected override Size MeasureOverride(Size availableSize) => new(TreeIcons.Size, TreeIcons.Size);

    public override void Render(DrawingContext context)
    {
        var bitmap = own ?? (Node is { } node ? TreeIcons.For(node.IconKind) : null);
        if (bitmap is null)
        {
            return;
        }

        var scaling = RenderScaling;
        var k = PixelScaling.DevicePixels(1, scaling);
        var slot = (int)Math.Round(TreeIcons.Size * scaling);
        int w = bitmap.PixelSize.Width * k, h = bitmap.PixelSize.Height * k;
        using var snap = PushSnap(context);
        var left = (slot - w) / 2 / scaling;
        var top = (slot - h) / 2 / scaling;
        context.DrawImage(bitmap, new Rect(left, top, w / scaling, h / scaling));
        // An alias file without its own (Finder-badged) icon: a 7-pixel arrow badge at the bottom left, as the
        // Finder's alias badge sits.
        if (own is null && Node is { IsAliasFile: true })
        {
            var u = k / scaling;
            var x0 = left;
            var y0 = top + h / scaling - 7 * u;
            for (var y = 0; y < 7; y++)
            {
                for (var x = 0; x < 7; x++)
                {
                    var black = AliasBadge[y][x] == '#';
                    context.FillRectangle(black ? Brushes.Black : Brushes.White, new Rect(x0 + x * u, y0 + y * u, u, u));
                }
            }
        }

        if (Node is { IsBrokenAlias: true, AliasState: { } state })
        {
            var (mark, colour) = state switch
            {
                ClassicMac.Files.AliasState.Missing => (MissingMark, Color.FromRgb(0xC0, 0x20, 0x20)),
                ClassicMac.Files.AliasState.Network => (NetworkMark, Color.FromRgb(0x20, 0x50, 0xC0)),
                _ => (DiskMark, Color.FromRgb(0x50, 0x50, 0x50)),
            };
            var u = k / scaling;
            var x0 = left + w / scaling - 5 * u;
            var y0 = top + h / scaling - 5 * u;
            var ink = new SolidColorBrush(colour);
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 5; x++)
                {
                    context.FillRectangle(mark[y][x] == '#' ? ink : Brushes.White, new Rect(x0 + x * u, y0 + y * u, u, u));
                }
            }
        }
    }
}
