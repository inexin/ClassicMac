using System;
using System.IO;
using System.Threading;
using ClassicMac.Core;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Graphics.QuickTime;

namespace ClassicMac.Graphics.Pict
{
    /// <summary>Pictures drawn into a <see cref="QuickDrawPort"/>.</summary>
    public static class PictDrawing
    {
        /// <summary>
        /// Plays <paramref name="picture"/> into the port, its frame mapped to <paramref name="destination"/> (local
        /// coordinates), as <c>DrawPicture</c> does. The port's state is saved and restored around it, so its pen, colours,
        /// patterns, text and clip come back unchanged; OpColor is left black, and on the ROM's QuickDraw the highlight
        /// colour is left as the picture set it. During playback the picture starts from DrawPicture's defaults, draws
        /// nothing until its ClipRgn opcode, and then only inside that region and the port's clip. A hidden pen stays
        /// hidden.
        /// </summary>
        /// <param name="port">The port drawn into.</param>
        /// <param name="picture">A picture, bare or with a <c>.pict</c> file's 512-byte header.</param>
        /// <param name="destination">Where the picture's frame goes.</param>
        /// <param name="imageCodec">Decodes QuickTime images whose codec is not built in.</param>
        /// <param name="cancellationToken">Cancels playback between opcodes.</param>
        /// <exception cref="NotSupportedException">The picture uses an unsupported pixel format.</exception>
        /// <exception cref="EndOfStreamException">The picture data is truncated.</exception>
        public static void DrawPicture(this QuickDrawPort port, byte[] picture, MacRect destination,
            IPictImageCodec? imageCodec = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(port);
            ArgumentNullException.ThrowIfNull(picture);
            var drawing = port.Options;
            var options = new PictDecodeOptions
            {
                QuickDraw = drawing.Version, ScreenDepth = drawing.ScreenDepth, Fonts = drawing.Fonts, TextFallback = drawing.TextFallback,
                HiliteColor = drawing.HiliteColor?.ToRgba(), PreserveAlpha = drawing.PreserveAlpha, ImageCodec = imageCodec,
            };
            var reader = new ClassicMac.Core.BigEndianReader(picture);
            var info = PictHeader.Parse(reader, out bool v1);
            var saved = port.Save();
            try
            {
                var player = new GrafPort(port, info.FrameRect, info.BoundsRect, port.ToCanvas(destination), options);
                PictReader.Play(reader, info, v1, player, options, cancellationToken);
            }
            finally
            {
                // The ROM writes the highlight colour into the real port's grafVars and leaves it; Mac OS 9 plays into a copy.
                port.Restore(saved, hilite: port.MacOS9);
            }
        }
    }
}
