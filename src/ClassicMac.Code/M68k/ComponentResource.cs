using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>A resource named by type and ID (Components.h ResourceSpec).</summary>
    public sealed record ResourceSpec(FourCC Type, short Id);

    /// <summary>One platform's code for a component (ComponentPlatformInfo).</summary>
    /// <param name="ComponentFlags">The component flags on that platform.</param>
    /// <param name="Code">The code resource.</param>
    /// <param name="PlatformType">The platform: 1 68k, 2 PowerPC.</param>
    public sealed record ComponentPlatform(uint ComponentFlags, ResourceSpec Code, short PlatformType)
    {
        /// <summary>The 68k platform type (gestalt68k).</summary>
        public const short M68k = 1;

        /// <summary>The PowerPC platform type (gestaltPowerPC).</summary>
        public const short PowerPC = 2;
    }

    /// <summary>
    /// A component resource (<c>'thng'</c>): the component description, the code, name, info and icon resources, and in
    /// the extended form the version, registration flags, icon family and per-platform code
    /// [Doc: Inside Macintosh: More Macintosh Toolbox, ExtComponentResource; Verified: the Mac OS 9 System's 'thng's].
    /// </summary>
    public sealed class ComponentResource
    {
        private ComponentResource() { }

        /// <summary>The component type.</summary>
        public FourCC Type { get; private init; }

        /// <summary>The component subtype.</summary>
        public FourCC SubType { get; private init; }

        /// <summary>The manufacturer.</summary>
        public FourCC Manufacturer { get; private init; }

        /// <summary>The component flags.</summary>
        public uint Flags { get; private init; }

        /// <summary>The flags mask.</summary>
        public uint FlagsMask { get; private init; }

        /// <summary>The code resource (the 68k one in an extended resource that lists platforms).</summary>
        public ResourceSpec Code { get; private init; } = null!;

        /// <summary>The name resource (a <c>'STR '</c>).</summary>
        public ResourceSpec Name { get; private init; } = null!;

        /// <summary>The info resource (a <c>'STR '</c>).</summary>
        public ResourceSpec Info { get; private init; } = null!;

        /// <summary>The icon resource (an <c>'ICON'</c>).</summary>
        public ResourceSpec Icon { get; private init; } = null!;

        /// <summary>Whether the extended fields are there.</summary>
        public bool IsExtended { get; private init; }

        /// <summary>The component version (extended form).</summary>
        public uint Version { get; private init; }

        /// <summary>The registration flags (extended form).</summary>
        public uint RegisterFlags { get; private init; }

        /// <summary>The icon family's ID (extended form).</summary>
        public short IconFamily { get; private init; }

        /// <summary>The per-platform code (extended form).</summary>
        public IReadOnlyList<ComponentPlatform> Platforms { get; private init; } = [];

        /// <summary>
        /// Reads a <c>'thng'</c>. Extended fields or platform entries cut short are reported (<c>m68k.thng-*</c>).
        /// </summary>
        /// <exception cref="System.IO.InvalidDataException">The resource is shorter than the 44-byte basic form.</exception>
        public static ComponentResource Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(data);
            if (reader.Length < BasicLength)
                throw new System.IO.InvalidDataException($"A 'thng' is at least {BasicLength} bytes; this one has {reader.Length}.");
            var type = reader.ReadFourCC();
            var subType = reader.ReadFourCC();
            var manufacturer = reader.ReadFourCC();
            var flags = reader.ReadUInt32();
            var flagsMask = reader.ReadUInt32();
            var code = Spec(reader);
            var name = Spec(reader);
            var info = Spec(reader);
            var icon = Spec(reader);
            if (reader.Remaining == 0)
                return new ComponentResource
                {
                    Type = type, SubType = subType, Manufacturer = manufacturer, Flags = flags, FlagsMask = flagsMask,
                    Code = code, Name = name, Info = info, Icon = icon,
                };
            if (reader.Length < ExtendedLength)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.thng-truncated",
                    $"The 'thng' has {reader.Length} bytes: more than the basic form, too few for the extended one ({ExtendedLength}).", BasicLength));
                return new ComponentResource
                {
                    Type = type, SubType = subType, Manufacturer = manufacturer, Flags = flags, FlagsMask = flagsMask,
                    Code = code, Name = name, Info = info, Icon = icon,
                };
            }
            var version = reader.ReadUInt32();
            var registerFlags = reader.ReadUInt32();
            var iconFamily = reader.ReadInt16();
            uint count = reader.ReadUInt32();
            var platforms = new List<ComponentPlatform>();
            for (uint i = 0; i < count; i++)
            {
                if (reader.Remaining < PlatformLength)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.thng-platform-truncated",
                        $"The 'thng' lists {count} platforms but has room for {i}.", reader.Position));
                    break;
                }
                var componentFlags = reader.ReadUInt32();
                var platformCode = Spec(reader);
                platforms.Add(new ComponentPlatform(componentFlags, platformCode, reader.ReadInt16()));
            }
            return new ComponentResource
            {
                Type = type, SubType = subType, Manufacturer = manufacturer, Flags = flags, FlagsMask = flagsMask,
                Code = code, Name = name, Info = info, Icon = icon, IsExtended = true, Version = version,
                RegisterFlags = registerFlags, IconFamily = iconFamily, Platforms = platforms,
            };
        }

        private const int BasicLength = 44;
        private const int ExtendedLength = 58;
        private const int PlatformLength = 12;

        private static ResourceSpec Spec(BigEndianReader reader) => new(reader.ReadFourCC(), reader.ReadInt16());
    }
}
