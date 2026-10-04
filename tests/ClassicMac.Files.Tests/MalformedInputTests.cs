using System.Buffers.Binary;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// Damaged inputs that once escaped as other exceptions (found by mutation fuzzing): each is refused as malformed.
public sealed class MalformedInputTests
{
    private static byte[] Sample(string folder, string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", folder, name));

    // A legacy StuffIt member whose two compressed fork lengths, each in range, add up past int's range.
    [Fact]
    public void Legacy_StuffIt_fork_lengths_past_int_are_refused_as_malformed()
    {
        var archive = Sample("StuffIt151", "fx151_huf.sit");
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(22 + 92), 0x7FFF_FFF0);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(22 + 96), 0x7FFF_FFF0);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext()));
    }
}
