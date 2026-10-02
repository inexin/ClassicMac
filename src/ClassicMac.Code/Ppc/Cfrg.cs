using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Code.Ppc
{
    /// <summary>What a code fragment is for (a <c>'cfrg'</c> member's usage byte; CodeFragments.h's CFragUsage).</summary>
    public enum CfrgUsage : byte
    {
        /// <summary>A shared library others import from (kImportLibraryCFrag).</summary>
        ImportLibrary = 0,
        /// <summary>An application (kApplicationCFrag).</summary>
        Application = 1,
        /// <summary>A plug-in loaded by its host (kDropInAdditionCFrag).</summary>
        DropIn = 2,
        /// <summary>A stub library, for linking only (kStubLibraryCFrag).</summary>
        StubLibrary = 3,
        /// <summary>A stub library whose imports are weak (kWeakStubLibraryCFrag).</summary>
        WeakStubLibrary = 4,
    }

    /// <summary>Where a code fragment's container is (a <c>'cfrg'</c> member's where byte; CFragLocatorKind).</summary>
    public enum CfrgWhere : byte
    {
        /// <summary>Already in memory (kMemoryCFragLocator).</summary>
        Memory = 0,
        /// <summary>In the file's data fork (kDataForkCFragLocator).</summary>
        DataFork = 1,
        /// <summary>In a resource of the file (kResourceCFragLocator).</summary>
        Resource = 2,
        /// <summary>A byte stream (kByteStreamCFragLocator).</summary>
        ByteStream = 3,
        /// <summary>Another fragment, by name (kNamedFragmentCFragLocator).</summary>
        NamedFragment = 4,
    }

    /// <summary>A member's extension: its kind and its data after the 4-byte kind and size header.</summary>
    public sealed record CfrgExtension(ushort Kind, ReadOnlyMemory<byte> Data)
    {
        /// <summary>The kind of the search extension (<c>0x30EE</c>, kCFragResourceSearchExtensionKind).</summary>
        public const ushort SearchKind = 0x30EE;
    }

    /// <summary>
    /// The search extension (kind <c>0x30EE</c>): the library kind (such as <c>'ndrv'</c> or <c>'otan'</c>) and four
    /// qualifier strings [Doc: CodeFragments.h, CFragResourceSearchExtension; Verified: Mac OS 9 System].
    /// </summary>
    public sealed record CfrgSearchExtension(FourCC LibraryKind, IReadOnlyList<string> Qualifiers);

    /// <summary>One code fragment a <c>'cfrg'</c> describes (CFragResourceMember).</summary>
    /// <param name="Architecture">The instruction set: <c>'pwpc'</c>, or <c>'m68k'</c> for CFM-68K.</param>
    /// <param name="UpdateLevel">The update level: 0 for a complete fragment (kIsCompleteCFrag), 1 and up for an update to one (kFirstCFragUpdate …).</param>
    /// <param name="CurrentVersion">The current version.</param>
    /// <param name="OldDefVersion">The oldest definition version it is compatible with.</param>
    /// <param name="Usage1">The first usage word: an application's stack size.</param>
    /// <param name="Usage2">The second usage word: an application's subdirectory ID, a library's flags.</param>
    /// <param name="Usage">What the fragment is for.</param>
    /// <param name="Where">Where the container is.</param>
    /// <param name="Offset">The container's offset in its fork (0 its start).</param>
    /// <param name="Length">The container's length (0 the rest of the fork).</param>
    /// <param name="Where1">The first locator word (uWhere1): an address space ID (spaceID) or a fork kind (forkKind).</param>
    /// <param name="Where2">The second locator word (uWhere2): reserved, or a fork instance (forkInstance).</param>
    /// <param name="Name">The fragment's name.</param>
    /// <param name="Extensions">The member's extensions.</param>
    /// <param name="MemberSize">The member's stored size, name and extensions included.</param>
    /// <param name="Position">Where the member starts in the resource.</param>
    public sealed record CfrgMember(FourCC Architecture, byte UpdateLevel, uint CurrentVersion, uint OldDefVersion,
        uint Usage1, ushort Usage2, CfrgUsage Usage, CfrgWhere Where, uint Offset, uint Length, uint Where1, ushort Where2,
        string Name, IReadOnlyList<CfrgExtension> Extensions, int MemberSize, int Position)
    {
        /// <summary>The search extension, if the member has one that reads.</summary>
        public CfrgSearchExtension? Search
        {
            get
            {
                foreach (var extension in Extensions)
                    if (extension.Kind == CfrgExtension.SearchKind && Cfrg.TryReadSearch(extension.Data, out var search))
                        return search;
                return null;
            }
        }
    }

    /// <summary>
    /// A code fragment resource (<c>'cfrg'</c> 0, version 1): the code fragments a file holds and where each one's PEF
    /// container is [Doc: Mac OS Runtime Architectures, ch. 7 / CodeFragments.h, CFragResource].
    /// </summary>
    public sealed class Cfrg
    {
        private const int HeaderSize = 32;
        private const int MemberFixedSize = 42;

        private Cfrg(ushort version, IReadOnlyList<CfrgMember> members)
        {
            Version = version;
            Members = members;
        }

        /// <summary>The resource version (1).</summary>
        public ushort Version { get; }

        /// <summary>The members, in stored order.</summary>
        public IReadOnlyList<CfrgMember> Members { get; }

        /// <summary>
        /// Reads a <c>'cfrg'</c>. The 32-byte header (version at 10, member count at 30) is followed by the members, each
        /// <c>memberSize</c> bytes: 42 fixed bytes, the Pascal name, then, if there are extensions, padding to a multiple
        /// of 4 and the extensions (kind, size including the 4-byte header, data). Members that run past the resource,
        /// or have an impossible size, are reported (<c>cfrg.*</c>) and reading stops.
        /// </summary>
        /// <exception cref="InvalidDataException">The header is shorter than 32 bytes or its version is not 1.</exception>
        public static Cfrg Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(data);
            if (reader.Length < HeaderSize)
                throw new InvalidDataException($"A 'cfrg' header is {HeaderSize} bytes; this resource has {reader.Length}.");
            var version = reader.ReadUInt16At(10);
            if (version != 1)
                throw new InvalidDataException($"'cfrg' version {version} is not supported (only version 1 is defined).");
            int count = reader.ReadUInt16At(30);

            var members = new List<CfrgMember>(count);
            int at = HeaderSize;
            for (int i = 0; i < count; i++)
            {
                if (at > reader.Length - (MemberFixedSize + 1))
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "cfrg.member-truncated",
                        $"'cfrg' member {i} of {count} runs past the end of the resource; {i} read.", at));
                    break;
                }
                var member = ReadMember(reader, at, diagnostics);
                if (member is null) break;
                members.Add(member);
                at += member.MemberSize;
            }
            return new Cfrg(version, members);
        }

        private static CfrgMember? ReadMember(BigEndianReader reader, int at, ICollection<Diagnostic> diagnostics)
        {
            reader.Position = at;
            var architecture = reader.ReadFourCC();
            reader.Skip(3); // reservedA (2), reservedB (1)
            var updateLevel = reader.ReadByte();
            var currentVersion = reader.ReadUInt32();
            var oldDefVersion = reader.ReadUInt32();
            var usage1 = reader.ReadUInt32();
            var usage2 = reader.ReadUInt16();
            var usage = (CfrgUsage)reader.ReadByte();
            var where = (CfrgWhere)reader.ReadByte();
            var offset = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            var where1 = reader.ReadUInt32();
            var where2 = reader.ReadUInt16();
            int extensionCount = reader.ReadUInt16();
            int memberSize = reader.ReadUInt16();
            int nameLength = reader.ReadByte();

            int nameEnd = at + MemberFixedSize + 1 + nameLength;
            if (memberSize < MemberFixedSize + 1 + nameLength || memberSize > reader.Length - at)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "cfrg.member-size",
                    $"A 'cfrg' member's size {memberSize} does not fit its name or the resource; reading stopped.", at));
                return null;
            }
            var name = MacRoman.Decode(reader.ReadBytes(nameLength));

            var extensions = new List<CfrgExtension>(extensionCount);
            if (extensionCount > 0)
            {
                // The name is padded so the extensions start on a 4-byte boundary (the member starts on one).
                int e = at + ((nameEnd - at + 3) & ~3);
                int end = at + memberSize;
                for (int i = 0; i < extensionCount; i++)
                {
                    if (e > end - 4)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "cfrg.extension-truncated",
                            $"'cfrg' member '{name}' has {extensionCount} extensions but room for {i}.", e));
                        break;
                    }
                    var kind = reader.ReadUInt16At(e);
                    int size = reader.ReadUInt16At(e + 2);
                    if (size < 4 || size > end - e)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "cfrg.extension-truncated",
                            $"'cfrg' member '{name}' extension {i} has size {size}, outside the member.", e));
                        break;
                    }
                    extensions.Add(new CfrgExtension(kind, reader.Source.Slice(e + 4, size - 4)));
                    e += size;
                }
            }
            return new CfrgMember(architecture, updateLevel, currentVersion, oldDefVersion, usage1, usage2, usage, where,
                offset, length, where1, where2, name, extensions, memberSize, at);
        }

        // The search extension's data: the library kind, then four Pascal strings.
        internal static bool TryReadSearch(ReadOnlyMemory<byte> data, out CfrgSearchExtension? search)
        {
            search = null;
            var reader = new BigEndianReader(data);
            if (!reader.TryReadFourCC(out var kind)) return false;
            var qualifiers = new List<string>(4);
            for (int i = 0; i < 4; i++)
            {
                if (!reader.TryReadByte(out var n) || !reader.TryReadBytes(n, out var bytes)) return false;
                qualifiers.Add(MacRoman.Decode(bytes));
            }
            search = new CfrgSearchExtension(kind, qualifiers);
            return true;
        }
    }
}
