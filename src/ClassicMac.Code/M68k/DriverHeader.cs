using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>A driver's flags (drvrFlags): what it handles and needs.</summary>
    [Flags]
    public enum DriverFlags : ushort
    {
        /// <summary>None.</summary>
        None = 0,
        /// <summary>It can read (dReadEnable).</summary>
        Read = 0x0100,
        /// <summary>It can write (dWritEnable).</summary>
        Write = 0x0200,
        /// <summary>It handles Control calls (dCtlEnable).</summary>
        Control = 0x0400,
        /// <summary>It handles Status calls (dStatEnable).</summary>
        Status = 0x0800,
        /// <summary>It wants a goodbye call when the heap is reinitialized (dNeedGoodBye).</summary>
        NeedGoodbye = 0x1000,
        /// <summary>It wants periodic time (dNeedTime).</summary>
        NeedTime = 0x2000,
        /// <summary>It must be locked in memory (dNeedLock).</summary>
        NeedLock = 0x4000,
    }

    /// <summary>
    /// A <c>'DRVR'</c> header: flags, delay, event mask, menu ID, the offsets of the open, prime, control, status and close
    /// routines (from the resource start) and the driver's name (a Pascal string at <c>$12</c>, which may differ from the
    /// resource name) [Doc: Inside Macintosh: Devices, the driver header].
    /// </summary>
    /// <param name="Flags">The flags word.</param>
    /// <param name="Delay">Ticks between periodic actions.</param>
    /// <param name="EventMask">The desk accessory event mask.</param>
    /// <param name="Menu">The desk accessory's menu ID.</param>
    /// <param name="Open">The open routine's offset.</param>
    /// <param name="Prime">The prime routine's offset.</param>
    /// <param name="Control">The control routine's offset.</param>
    /// <param name="Status">The status routine's offset.</param>
    /// <param name="Close">The close routine's offset.</param>
    /// <param name="Name">The driver name.</param>
    /// <param name="IsStandard">False when a routine offset or the name falls outside the resource: the resource is not in this format.</param>
    public sealed record DriverHeader(DriverFlags Flags, ushort Delay, ushort EventMask, short Menu, ushort Open, ushort Prime,
        ushort Control, ushort Status, ushort Close, string Name, bool IsStandard)
    {
        /// <summary>Where the name is.</summary>
        public const int NameOffset = 0x12;

        /// <summary>
        /// Reads a <c>'DRVR'</c> header. One whose routine offsets or name fall outside the resource is reported
        /// (<c>m68k.drvr-nonstandard</c>) with <see cref="IsStandard"/> false; one shorter than the fixed fields gives null.
        /// </summary>
        public static DriverHeader? Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(data);
            if (reader.Length < NameOffset + 1)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.drvr-nonstandard",
                    $"The 'DRVR' is {reader.Length} bytes, too short for a driver header."));
                return null;
            }
            var flags = (DriverFlags)reader.ReadUInt16();
            var delay = reader.ReadUInt16();
            var eventMask = reader.ReadUInt16();
            var menu = reader.ReadInt16();
            ushort open = reader.ReadUInt16(), prime = reader.ReadUInt16(), control = reader.ReadUInt16(),
                status = reader.ReadUInt16(), close = reader.ReadUInt16();
            int nameLength = reader.ReadByte();
            bool standard = true;
            string name = "";
            if (nameLength > reader.Remaining)
            {
                standard = false;
            }
            else
            {
                name = MacRoman.Decode(reader.ReadBytes(nameLength));
            }

            foreach (var offset in (ReadOnlySpan<ushort>)[open, prime, control, status, close])
            {
                if (offset >= reader.Length)
                {
                    standard = false;
                }
            }

            if (!standard)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.drvr-nonstandard",
                    "The 'DRVR' is not a standard driver: its routine offsets or name fall outside the resource."));
            }

            return new DriverHeader(flags, delay, eventMask, menu, open, prime, control, status, close, name, standard);
        }
    }
}
