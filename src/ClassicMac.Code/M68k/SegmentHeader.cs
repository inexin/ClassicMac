using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>
    /// A <c>'CODE'</c> segment's header. Near (4 bytes): the offset of its first jump-table entry (from the table's
    /// start) and its entry count. Far (<c>$28</c> bytes, starting <c>$FFFF</c>): two (offset, count) pairs, then the A5
    /// and PC relocation lists' offsets with the values they were last relocated for
    /// [Doc: Inside Macintosh II, the Segment Loader; Mac OS Runtime Architectures, the 32-bit everything header].
    /// </summary>
    /// <param name="IsFar">Whether it is the far header.</param>
    /// <param name="FirstNearOffset">Near: the first entry's offset. Far: the first "near" entry's offset (MPW uses this pair).</param>
    /// <param name="NearCount">Near: the entry count. Far: the "near" entry count.</param>
    /// <param name="FirstFarOffset">Far: the first "far" entry's offset (Retro68 uses this pair).</param>
    /// <param name="FarCount">Far: the "far" entry count.</param>
    /// <param name="A5RelocationOffset">Far: the A5 relocation list's offset in the resource (0: none).</param>
    /// <param name="A5AtLastRelocation">Far: the A5 the segment was last relocated for (0 in a file).</param>
    /// <param name="PcRelocationOffset">Far: the PC relocation list's offset in the resource (0: none).</param>
    /// <param name="AddressAtLastRelocation">Far: the address the segment was last relocated for (0 in a file).</param>
    public sealed record SegmentHeader(bool IsFar, uint FirstNearOffset, uint NearCount, uint FirstFarOffset, uint FarCount,
        uint A5RelocationOffset, uint A5AtLastRelocation, uint PcRelocationOffset, uint AddressAtLastRelocation)
    {
        /// <summary>The near header's length.</summary>
        public const int NearLength = 4;

        /// <summary>The far header's length.</summary>
        public const int FarLength = 0x28;

        /// <summary>The header's length: where the code starts.</summary>
        public int Length => IsFar ? FarLength : NearLength;

        /// <summary>The jump-table entries the segment owns: each pair with a nonzero count, both if both.</summary>
        public IReadOnlyList<int> EntryIndices
        {
            get
            {
                var indices = new SortedSet<int>();
                Add(FirstNearOffset, NearCount);
                Add(FirstFarOffset, FarCount);
                return [.. indices];

                void Add(uint first, uint count)
                {
                    for (long i = 0; i < count && i < MaxEntries; i++) indices.Add((int)(first / 8 + i));
                }
            }
        }

        // A jump table holds at most this many entries (its size is a 32-bit byte count, but CODE 0 is far smaller).
        private const int MaxEntries = 0x10000;

        /// <summary>
        /// Reads a segment's header. A segment shorter than its header is reported (<c>m68k.segment-header</c>) and
        /// gives null.
        /// </summary>
        public static SegmentHeader? Read(ReadOnlyMemory<byte> segment, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(segment);
            bool far = reader.Length >= 2 && reader.ReadUInt16At(0) == 0xFFFF;
            int length = far ? FarLength : NearLength;
            if (reader.Length < length)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.segment-header",
                    $"The segment is {reader.Length} bytes, shorter than its {length}-byte {(far ? "far" : "near")} header.", 0));
                return null;
            }
            SegmentHeader header;
            if (!far)
                header = new SegmentHeader(false, reader.ReadUInt16At(0), reader.ReadUInt16At(2), 0, 0, 0, 0, 0, 0);
            else
            {
                reader.Position = 4;
                header = new SegmentHeader(true, reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
            }
            if ((header.NearCount != 0 && header.FirstNearOffset % 8 != 0) || (header.FarCount != 0 && header.FirstFarOffset % 8 != 0))
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.segment-entry-offset",
                    "The segment's first jump-table entry offset is not a multiple of 8.", 0));
            return header;
        }
    }
}
