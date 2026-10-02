using System;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>What the hex view's Find box holds: bytes as hex, or text in Mac OS Roman.</summary>
    public enum HexFindMode
    {
        Hex,
        Text,
    }

    /// <summary>Finding bytes in the hex view (design/boards/hex.md, the follow-up to E7).</summary>
    public static class HexSearch
    {
        /// <summary>
        /// The bytes a Find box's <paramref name="text"/> stands for: hex digits two a byte (spaces and "0x" ignored), or
        /// text in Mac OS Roman; false with why it cannot be.
        /// </summary>
        public static bool TryParse(string text, HexFindMode mode, out byte[] pattern, out string? error)
        {
            pattern = [];
            error = null;
            if (mode == HexFindMode.Text)
            {
                if (text.Length == 0)
                {
                    error = "Type text to find";
                    return false;
                }

                if (text.FirstOrDefault(c => !MacRoman.TryGetByte(c, out _)) is var bad && bad != default)
                {
                    error = $"Not in Mac OS Roman: {bad}";
                    return false;
                }

                pattern = MacRoman.Encode(text);
                return true;
            }

            var digits = string.Concat(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part));
            if (digits.Length == 0)
            {
                error = "Type bytes to find";
                return false;
            }

            if (digits.FirstOrDefault(c => !Uri.IsHexDigit(c)) is var notHex && notHex != default)
            {
                error = $"Not hex: {notHex}";
                return false;
            }

            if (digits.Length % 2 != 0)
            {
                error = "Hex needs two digits a byte";
                return false;
            }

            pattern = Convert.FromHexString(digits);
            return true;
        }

        /// <summary>
        /// The next match of <paramref name="pattern"/>: forward, the first at or after <paramref name="start"/>; back, the
        /// last before it; wrapping around the ends. -1 when there is none.
        /// </summary>
        public static int Find(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int start, bool forward)
        {
            if (pattern.Length == 0 || pattern.Length > data.Length)
            {
                return -1;
            }

            var last = data.Length - pattern.Length;
            if (forward)
            {
                var from = Math.Clamp(start, 0, last + 1);
                var after = from <= last ? data[from..].IndexOf(pattern) : -1;
                return after >= 0 ? from + after : data.IndexOf(pattern);
            }

            var before = data[..Math.Clamp(start - 1 + pattern.Length, 0, data.Length)].LastIndexOf(pattern);
            return before >= 0 && before < start ? before : data.LastIndexOf(pattern);
        }

        /// <summary>How many matches there are, overlapping ones included.</summary>
        public static int Count(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) => Ordinal(data, pattern, data.Length) - 1;

        /// <summary>Which match (1-based) starts at <paramref name="offset"/>: the matches before it, plus one.</summary>
        public static int Ordinal(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int offset)
        {
            var count = 0;
            for (var at = 0; pattern.Length > 0 && at < offset && at <= data.Length - pattern.Length; at++)
            {
                if (data.Slice(at, pattern.Length).SequenceEqual(pattern))
                {
                    count++;
                }
            }

            return count + 1;
        }
    }

    // Find in the Hex tab: the match is selected (read only) or put under the cursor (editing), highlighted, and its line
    // scrolled into view.
    public sealed partial class MainViewModel
    {
        /// <summary>The Find box's text.</summary>
        [ObservableProperty]
        private string findText = "";

        /// <summary>Whether the Find box holds hex bytes or text.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FindModeIndex))]
        private HexFindMode findMode = HexFindMode.Hex;

        /// <summary>The Find mode switch, in <see cref="FindModes"/> order.</summary>
        public int FindModeIndex
        {
            get => (int)FindMode;
            set => FindMode = value == 1 ? HexFindMode.Text : HexFindMode.Hex;
        }

        /// <summary>The switch's segments.</summary>
        public static string[] FindModes { get; } = ["Hex", "Text"];

        /// <summary>"2 of 5", "Not found" or why the text is not a pattern; null before a find.</summary>
        [ObservableProperty]
        private string? findStatus;

        /// <summary>Whether the last find found nothing or could not read the pattern.</summary>
        [ObservableProperty]
        private bool findFailed;

        /// <summary>Raised with the line a found match is on, for the view to scroll to it.</summary>
        public event Action<int>? HexLineShown;

        private long hexSelectedOffset = -1;

        [RelayCommand]
        private void FindNext() => Find(forward: true);

        [RelayCommand]
        private void FindPrevious() => Find(forward: false);

        private void Find(bool forward)
        {
            if (!HexSearch.TryParse(FindText, FindMode, out var pattern, out var error))
            {
                (FindStatus, FindFailed) = (error, true);
                return;
            }

            byte[] data;
            long position;
            if (HexEdit is { } editor)
            {
                data = editor.ToArray();
                position = editor.Cursor;
            }
            else if (HexSource is { } source && source.Data.Length <= ReadOptions.MaxResourceSize)
            {
                data = source.Data.ToArray();
                position = hexSelectedOffset;
            }
            else
            {
                return;
            }

            // Next: after the current byte (from the start when none); previous: before it (from the end when none).
            var start = forward ? (int)(position + 1) : position < 0 ? data.Length : (int)position;
            var found = HexSearch.Find(data, pattern, start, forward);
            if (found < 0)
            {
                (FindStatus, FindFailed) = ("Not found", true);
                return;
            }

            if (HexEdit is { } editing)
            {
                editing.MoveTo(found);
                editing.Lines.ShowMatch(found, pattern.Length);
            }
            else
            {
                SelectHexByte(found);
                HexLines?.ShowMatch(found, pattern.Length);
            }

            FindStatus = string.Create(CultureInfo.InvariantCulture,
                $"{HexSearch.Ordinal(data, pattern, found)} of {HexSearch.Count(data, pattern)}");
            FindFailed = false;
            HexLineShown?.Invoke(found / 16);
        }
    }
}
