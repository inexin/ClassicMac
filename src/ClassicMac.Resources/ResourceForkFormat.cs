namespace ClassicMac.Resources
{
    // Layout constants of a resource fork, from Inside Macintosh: More Macintosh Toolbox, "Resource File Format".
    internal static class ResourceForkFormat
    {
        // Fork header: data offset, map offset, data length, map length (four big-endian u32).
        public const int HeaderLength = 16;

        // The header plus the system (112) and application (128) areas; data conventionally starts here.
        public const int ReservedEnd = 256;

        // Map header: header copy (16), next-map handle (4), file reference (2), attributes (mAttr, 1), in-memory flags
        // (mInMemoryAttr, 1), type-list offset (2), name-list offset (2).
        public const int MapHeaderLength = 28;
        public const int MapAttributesOffset = 22;
        public const int MapFlagsOffset = 23;
        public const int MapTypeListOffsetOffset = 24;
        public const int MapNameListOffsetOffset = 26;

        // Type list: count - 1 (u16), then entries of type (4), count - 1 (2), reference-list offset (2).
        public const int TypeCountLength = 2;
        public const int TypeEntryLength = 8;

        // Reference entry: ID (2), name offset (2), attributes (1) + data offset (3), handle (4).
        public const int ReferenceEntryLength = 12;

        // A name offset of -1 means the resource has no name.
        public const ushort NoName = 0xFFFF;

        // Data offsets are 24-bit.
        public const int MaxDataOffset = 0xFFFFFF;

        // The last byte a fork's data area or map may end at: the Resource Manager opens and grows none past it.
        public const long MaxForkEnd = 0xFFFFFE;
    }
}
