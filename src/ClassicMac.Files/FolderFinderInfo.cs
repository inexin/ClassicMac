using System;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// A folder's Finder information, from <i>Inside Macintosh: Macintosh Toolbox Essentials</i>, the Finder Interface
    /// chapter: the <c>DInfo</c> record (window rectangle, flags, icon location, view) and the <c>DXInfo</c> record
    /// (scroll position, open chain, script, extended flags, comment, put-away folder). HFS stores them in a catalog folder
    /// record's <c>dirUsrInfo</c> and <c>dirFndrInfo</c>, HFS Plus in <c>userInfo</c> and <c>finderInfo</c>. See
    /// docs/formats/file-systems/finder-windows.md.
    /// </summary>
    public sealed record FolderFinderInfo
    {
        /// <summary>All fields zero.</summary>
        public static FolderFinderInfo Empty { get; } = new();

        /// <summary>The size of <c>DInfo</c> followed by <c>DXInfo</c>.</summary>
        public const int Length = 32;

        /// <summary>The folder's window, in global coordinates (<c>frRect</c>).</summary>
        public MacRect WindowBounds { get; init; }

        /// <summary>The Finder flags (<c>frFlags</c>): invisible, custom icon, label, ….</summary>
        public FinderFlags Flags { get; init; }

        /// <summary>The folder's icon position in the window of the folder that holds it (<c>frLocation</c>).</summary>
        public MacPoint Location { get; init; }

        /// <summary>How the window shows its contents (<c>frView</c>), uninterpreted.</summary>
        public short View { get; init; }

        /// <summary>The window's scroll position (<c>frScroll</c>).</summary>
        public MacPoint ScrollPosition { get; init; }

        /// <summary>The chain of open folders (<c>frOpenChain</c>).</summary>
        public int OpenChain { get; init; }

        /// <summary>The script of the folder's name (<c>frScript</c>), when its high bit is set.</summary>
        public sbyte Script { get; init; }

        /// <summary>The extended flags (<c>frXFlags</c>).</summary>
        public sbyte ExtendedFlags { get; init; }

        /// <summary>The comment's ID in the desktop database (<c>frComment</c>).</summary>
        public short Comment { get; init; }

        /// <summary>The folder the item was dragged to the desktop from (<c>frPutAway</c>).</summary>
        public int PutAway { get; init; }

        /// <summary>Reads <c>DInfo</c> and <c>DXInfo</c>; shorter input is padded with zeros.</summary>
        public static FolderFinderInfo Read(ReadOnlySpan<byte> source)
        {
            var bytes = new byte[Length];
            source[..Math.Min(source.Length, Length)].CopyTo(bytes);
            var reader = new BigEndianReader(bytes);
            return new FolderFinderInfo
            {
                WindowBounds = reader.ReadMacRect(),
                Flags = (FinderFlags)reader.ReadUInt16(),
                Location = reader.ReadMacPoint(),
                View = reader.ReadInt16(),
                ScrollPosition = reader.ReadMacPoint(),
                OpenChain = reader.ReadInt32(),
                Script = (sbyte)reader.ReadByte(),
                ExtendedFlags = (sbyte)reader.ReadByte(),
                Comment = reader.ReadInt16(),
                PutAway = reader.ReadInt32(),
            };
        }

        /// <summary>Writes <c>DInfo</c> and <c>DXInfo</c>, 32 bytes, as <see cref="Read"/> reads them.</summary>
        public void Write(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException($"Folder Finder info needs {Length} bytes.", nameof(destination));
            }

            var writer = new BigEndianWriter(Length);
            writer.WriteMacRect(WindowBounds);
            writer.WriteUInt16((ushort)Flags);
            writer.WriteMacPoint(Location);
            writer.WriteInt16(View);
            writer.WriteMacPoint(ScrollPosition);
            writer.WriteInt32(OpenChain);
            writer.WriteByte((byte)Script);
            writer.WriteByte((byte)ExtendedFlags);
            writer.WriteInt16(Comment);
            writer.WriteInt32(PutAway);
            writer.WrittenSpan.CopyTo(destination);
        }

        /// <summary>The 32 bytes <see cref="Write"/> writes.</summary>
        public byte[] ToArray()
        {
            var bytes = new byte[Length];
            Write(bytes);
            return bytes;
        }
    }
}
