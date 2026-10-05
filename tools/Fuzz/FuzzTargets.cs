using ClassicMac.Code.Disassembly;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Code;
using ClassicMac.Resources.Export;

namespace Fuzz;

/// <summary>
/// The fuzz targets: each reader's entry point, read as an application would read it. A target may refuse damaged input
/// as malformed (<see cref="ExceptionFilters.IsMalformed"/>); any other exception is a crash (PLAN "Hostile input").
/// </summary>
internal static class FuzzTargets
{
    // Made on first use, not in a static initializer: instrumented code may run only once libFuzzer has set up its
    // coverage map (inside Fuzzer.LibFuzzer.Run), or SharpFuzz's tracing finds no map and throws.
    private static readonly Lazy<ReadOptions[]> Models =
        new(() => [ReadOptions.Default, ReadOptions.Default with { ResourceManager = ResourceManagerModel.Rom68k }]);

    private static readonly QuickDrawVersion[] QuickDraws = [QuickDrawVersion.MacOS9, QuickDrawVersion.MacRom];

    private static readonly Lazy<IReadOnlyList<IResourceDecoder>> Decoders = new(() => ResourceDecoders.Create());

    public static IReadOnlyDictionary<string, Action<ReadOnlyMemory<byte>>> All { get; } =
        new Dictionary<string, Action<ReadOnlyMemory<byte>>>(StringComparer.Ordinal)
        {
            ["container"] = Container,
            ["resource-fork"] = ResourceForks,
            ["resource"] = Resources,
            ["code"] = Code,
            ["pef"] = Pef,
            ["pict"] = Pict,
            ["first-aid"] = FirstAid,
            ["ndif-write"] = NdifWrite,
            ["hfs-edit"] = Strict(WriterTargets.HfsEdit),
            ["wrappers"] = Strict(WriterTargets.Wrappers),
            ["pict-write"] = Strict(WriterTargets.PictWrite),
        };

    /// <summary>Runs <paramref name="target"/> on <paramref name="input"/>, letting only a malformed-input refusal pass.</summary>
    public static void Run(Action<ReadOnlyMemory<byte>> target, ReadOnlyMemory<byte> input)
    {
        try
        {
            target(input);
        }
        catch (Exception e) when (ExceptionFilters.IsMalformed(e))
        {
        }
    }

    /// <summary>
    /// <paramref name="target"/> as a writer's target: a writer given valid input must make it, so a malformed-input
    /// refusal (its read-back check failing) is a crash too.
    /// </summary>
    public static Action<ReadOnlyMemory<byte>> Strict(Action<ReadOnlyMemory<byte>> target) => input =>
    {
        try
        {
            target(input);
        }
        catch (Exception e) when (ExceptionFilters.IsMalformed(e))
        {
            throw new InvalidOperationException("A writer refused its own output: " + e.Message, e);
        }
    };

    /// <summary>
    /// Runs every file in <paramref name="paths"/> (files, or folders of them) through <paramref name="target"/> without
    /// libFuzzer, as a crash is reproduced; writes each failure to <paramref name="output"/>. 1 when any failed.
    /// </summary>
    public static int Replay(Action<ReadOnlyMemory<byte>> target, IEnumerable<string> paths, TextWriter output)
    {
        var failed = 0;
        foreach (var path in paths.SelectMany<string, string>(p => Directory.Exists(p)
                     ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                     : [p]))
        {
            try
            {
                Run(target, File.ReadAllBytes(path));
            }
#pragma warning disable CA1031 // every other exception is the crash being reported
            catch (Exception e)
#pragma warning restore CA1031
            {
                output.WriteLine($"{path}: {e}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A reader's or decoder's unexpected exception, which ClassicMac reports as a diagnostic (<c>container.reader-fault</c>,
    /// <c>image.decoder-fault</c>) and carries on: a crash here.
    /// </summary>
    public static void ThrowOnFault(IEnumerable<Diagnostic> diagnostics)
    {
        if (diagnostics.FirstOrDefault(d => d.Code.EndsWith("-fault", StringComparison.Ordinal)) is { } fault)
        {
            throw new InvalidOperationException(fault.Message);
        }
    }

    /// <summary>The resource target's input: four bytes of type, then the data; null when shorter than a type.</summary>
    public static (FourCC Type, ReadOnlyMemory<byte> Data)? ResourceInput(ReadOnlyMemory<byte> input)
    {
        var reader = new BigEndianReader(input);
        return reader.TryReadUInt32(out var type) ? (new FourCC(type), input[4..]) : null;
    }

    /// <inheritdoc cref="ResourceInput(ReadOnlyMemory{byte})"/>
    public static byte[] ResourceInput(FourCC type, ReadOnlyMemory<byte> data)
    {
        var writer = new BigEndianWriter();
        writer.WriteFourCC(type);
        writer.WriteBytes(data.Span);
        return writer.ToArray();
    }

    // A host file holding the input, unwrapped as deep as it goes with every fork read.
    private static void Container(ReadOnlyMemory<byte> input)
    {
        var diagnostics = new List<Diagnostic>();
        var file = new MacFile { Name = MacString.FromMacRoman("input"), DataFork = ForkData.FromBytes(input) };
        ReadAll(ContainerUnwrapper.Default.Unwrap(file, "host file", new ContainerContext(null, diagnostics)));
        ThrowOnFault(diagnostics);
    }

    private static void ReadAll(ContainerNode node)
    {
        _ = node.File.DataFork.ToArray();
        _ = node.File.ResourceFork.ToArray();
        foreach (var child in node.Children)
        {
            ReadAll(child);
        }
    }

    // A resource fork read by both Resource Manager models, each resource's data fetched (and decompressed).
    private static void ResourceForks(ReadOnlyMemory<byte> input)
    {
        foreach (var model in Models.Value)
        {
            var fork = ResourceFork.Read(input, model);
            foreach (var resource in fork.Resources)
            {
                _ = ResourceDecompression.Default.GetData(resource, fork, model, []);
            }
        }
    }

    // One resource through every decoder that takes its type, as the exporter decodes it.
    private static void Resources(ReadOnlyMemory<byte> input)
    {
        if (ResourceInput(input) is not var (type, data))
        {
            return;
        }

        var resource = new Resource(type, 128, data);
        var fork = new ResourceFork();
        fork.Add(resource);
        var diagnostics = new List<Diagnostic>();
        foreach (var decoder in Decoders.Value.Where(d => d.CanDecode(type)))
        {
            decoder.Decode(new DecodeInput(resource, data, fork, diagnostics: diagnostics));
        }

        ThrowOnFault(diagnostics);
    }

    // A resource fork's 68k and PowerPC code listed.
    private static void Code(ReadOnlyMemory<byte> input) =>
        _ = CodeExport.Disassemble(ResourceFork.Read(input), () => ReadOnlyMemory<byte>.Empty, diagnostics: []);

    // A PEF container read and listed.
    private static void Pef(ReadOnlyMemory<byte> input) =>
        _ = CodeListing.ForFragment(PefContainer.Read(input, []), "fuzz");

    // A picture drawn by both QuickDraws. PictReader also refuses data that is no picture, or a pixel format it does not
    // draw, with NotSupportedException (its documented contract, kept from QuickDraw.Pict).
    private static void Pict(ReadOnlyMemory<byte> input)
    {
        foreach (var quickDraw in QuickDraws)
        {
            try
            {
                _ = PictReader.Decode(input, new PictDecodeOptions { QuickDraw = quickDraw });
            }
            catch (NotSupportedException)
            {
            }
        }
    }

    // The NDIF writer (ndif.md §3): whole sectors of the input as a disk, made into an image, rewritten around the disk
    // with its last byte changed, and (when the first byte says) split into two parts that are rewritten too. The first
    // byte chooses the kind of image and the split, the second the chunk size: one image a run keeps the runs quick.
    // Every step reads its result back, so any refusal is a crash.
    private static void NdifWrite(ReadOnlyMemory<byte> input) => Strict(data =>
    {
        var disk = data[..(data.Length / 512 * 512)];
        if (disk.Length == 0)
        {
            return;
        }

        var changed = disk.ToArray();
        changed[^1] ^= 0xFF;
        var formats = Enum.GetValues<NdifFormat>();
        var options = new NdifCreateOptions { Format = formats[data.Span[0] % formats.Length], ChunkSectors = 1 + data.Span[1] % 64 };
        var image = NdifWriter.Create(disk, "fuzz.img", options);
        _ = NdifWriter.Rewrite(image, changed);
        if ((data.Span[0] & 0x10) != 0 && image.DataFork.Length > 512)
        {
            _ = NdifWriter.RewriteSegmented(NdifWriter.Split(image, 2, "fuzz"), changed);
        }
    })(input);

    // An HFS or HFS Plus volume checked and repaired.
    private static void FirstAid(ReadOnlyMemory<byte> input)
    {
        _ = HfsFirstAid.Verify(ForkData.FromBytes(input));
        _ = HfsFirstAid.Repair(ForkData.FromBytes(input)).Volume;
    }
}
