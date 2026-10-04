using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Tests;

namespace ClassicMac.Resources.Tests;

// Damaged resource forks and compressed resources (PLAN "Hostile input"): mutated forks read and every resource's data
// fetched as an application would get it, and compressed data, mutated and random, through each `dcmp` with both
// Resource Manager models. Damage is reported or refused as malformed, never anything else.
public sealed class ResourceMutationTests
{
    private static readonly ReadOptions[] Models =
        [ReadOptions.Default, ReadOptions.Default with { ResourceManager = ResourceManagerModel.Rom68k }];

    // A compressed resource's data: the 18-byte header (version 8 names the dcmp at +14, version 9 at +12) and a payload.
    private static byte[] Compressed(short dcmp, uint size, byte[] payload, byte version)
    {
        var data = new byte[CompressedResourceHeader.Length + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, CompressedResourceHeader.Signature);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), CompressedResourceHeader.Length);
        data[6] = version;
        data[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), size);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(version == 8 ? 14 : 12), dcmp);
        payload.CopyTo(data, CompressedResourceHeader.Length);
        return data;
    }

    // A fork of plain, named and compressed resources.
    private static byte[] Fork()
    {
        var random = new Random(1);
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR "), 128, "\u0005hello"u8.ToArray()) { Name = MacString.FromMacRoman("greeting") });
        fork.Add(new Resource(FourCC.FromString("TEXT"), 129, "Some text for the fork."u8.ToArray()));
        for (short dcmp = 0; dcmp <= 3; dcmp++)
        {
            var payload = new byte[64];
            random.NextBytes(payload);
            fork.Add(new Resource(FourCC.FromString("CMPR"), (short)(130 + dcmp), Compressed(dcmp, 256, payload, dcmp == 2 ? (byte)9 : (byte)8))
            {
                Attributes = ResourceAttributes.Compressed,
            });
        }

        return fork.ToArray();
    }

    [Fact]
    public void Damaged_forks_are_reported_or_refused_as_malformed()
    {
        var fork = Fork();
        var failures = new List<string>();
        for (var seed = 0; seed < Mutations.PerInput(300); seed++)
        {
            var mutant = Mutations.Mutate(fork, new Random(seed));
            Mutations.Run($"fork (seed {seed})", () =>
            {
                foreach (var model in Models)
                {
                    var read = ResourceFork.Read(mutant, model);
                    foreach (var resource in read.Resources)
                    {
                        _ = ResourceDecompression.Default.GetData(resource, read, model, []);
                    }
                }
            }, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 8)]
    [InlineData(2, 9)]
    [InlineData(3, 8)]
    public void Random_and_damaged_compressed_data_is_reported_or_refused(short dcmp, byte version)
    {
        var failures = new List<string>();
        for (var seed = 0; seed < Mutations.PerInput(300); seed++)
        {
            var random = new Random(dcmp * 100_000 + seed);
            var payload = new byte[random.Next(0, 400)];
            random.NextBytes(payload);
            var data = Compressed(dcmp, (uint)random.Next(0, 8192), payload, version);
            if (seed % 2 == 1)
            {
                data = Mutations.Mutate(data, random);                           // the header damaged too
            }

            var resource = new Resource(FourCC.FromString("CMPR"), 128, data) { Attributes = ResourceAttributes.Compressed };
            Mutations.Run($"dcmp {dcmp} (seed {seed})", () =>
            {
                foreach (var model in Models)
                {
                    _ = ResourceDecompression.Default.GetData(resource, null, model, []);
                }
            }, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
