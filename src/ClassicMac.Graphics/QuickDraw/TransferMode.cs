namespace ClassicMac.Graphics.QuickDraw
{
    /// <summary>
    /// QuickDraw's transfer modes (<i>Inside Macintosh: Imaging With QuickDraw</i>, "Boolean Transfer Modes", "Arithmetic
    /// Transfer Modes", "Highlighting"). Any other value may be cast to it too: the renderer draws each mode value as the
    /// chosen QuickDraw does, including the undocumented ones.
    /// </summary>
    public enum TransferMode
    {
        /// <summary>The source replaces the destination.</summary>
        SrcCopy = 0,
        /// <summary>Source ink (the foreground colour) over the destination.</summary>
        SrcOr = 1,
        /// <summary>Source ink inverts the destination.</summary>
        SrcXor = 2,
        /// <summary>Source ink erases to the background colour.</summary>
        SrcBic = 3,
        /// <summary>The inverted source replaces the destination.</summary>
        NotSrcCopy = 4,
        /// <summary>Inverted source ink over the destination.</summary>
        NotSrcOr = 5,
        /// <summary>Inverted source ink inverts the destination.</summary>
        NotSrcXor = 6,
        /// <summary>Inverted source ink erases to the background colour.</summary>
        NotSrcBic = 7,
        /// <summary>The pattern replaces the destination.</summary>
        PatCopy = 8,
        /// <summary>Pattern ink over the destination.</summary>
        PatOr = 9,
        /// <summary>Pattern ink inverts the destination.</summary>
        PatXor = 10,
        /// <summary>Pattern ink erases to the background colour.</summary>
        PatBic = 11,
        /// <summary>The inverted pattern replaces the destination.</summary>
        NotPatCopy = 12,
        /// <summary>Inverted pattern ink over the destination.</summary>
        NotPatOr = 13,
        /// <summary>Inverted pattern ink inverts the destination.</summary>
        NotPatXor = 14,
        /// <summary>Inverted pattern ink erases to the background colour.</summary>
        NotPatBic = 15,
        /// <summary>A weighted average of source and destination (the weight is the port's <c>OpColor</c>).</summary>
        Blend = 32,
        /// <summary>Source plus destination, pinned at <c>OpColor</c>.</summary>
        AddPin = 33,
        /// <summary>Source plus destination, wrapping.</summary>
        AddOver = 34,
        /// <summary>Destination minus source, pinned at <c>OpColor</c>.</summary>
        SubPin = 35,
        /// <summary>The source except where it is the background colour.</summary>
        Transparent = 36,
        /// <summary>The larger of source and destination, per component.</summary>
        AddMax = 37,
        /// <summary>Destination minus source, wrapping.</summary>
        SubOver = 38,
        /// <summary>The smaller of source and destination, per component.</summary>
        AdMin = 39,
        /// <summary>Text drawn dimmed (a colour between foreground and background, or a grey pattern).</summary>
        GrayishTextOr = 49,
        /// <summary>Highlighting: the background colour and the highlight colour swap.</summary>
        Hilite = 50,
        /// <summary>Added to a CopyBits mode: dither when copying to a shallower screen.</summary>
        DitherCopy = 64,
    }
}
