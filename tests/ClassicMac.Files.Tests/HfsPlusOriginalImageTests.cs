using System.Security.Cryptography;
using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Tests;

public sealed class HfsPlusOriginalImageTests
{
    [Fact]
    public void JournaledMacOsHfsPlusImageListsFilesWithThePublishedContents()
    {
        string? imagePath = Environment.GetEnvironmentVariable("CLASSICMAC_HFSPLUS_REFERENCE_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            Assert.Skip("Set CLASSICMAC_HFSPLUS_REFERENCE_IMAGE to Digital Corpora's nps-2009-hfsjtest1/image.gen1.dmg.");

        using (FileStream image = File.OpenRead(imagePath))
            Assert.Equal("BEB7795DD6D1A5319F9C20101855FFFF9665FCC11C6B23DE822D50C0D1E388EE",
                Convert.ToHexString(SHA256.HashData(image)));

        var diagnostics = new List<Diagnostic>();
        ContainerNode root = ContainerUnwrapper.Default.Unwrap(imagePath, diagnostics: diagnostics);
        MacFile first = Assert.Single(root.Leaves(), node => node.File.MacPath == "file1.txt").File;
        MacFile second = Assert.Single(root.Leaves(), node => node.File.MacPath == "file2.txt").File;

        Assert.Equal(28, first.DataFork.Length);
        Assert.Equal("BC4EB9FA980CB82808292E47F023874B8E63D068",
            Convert.ToHexString(SHA1.HashData(first.DataFork.ToArray())));
        Assert.Equal(23, second.DataFork.Length);
        Assert.Equal("D1D2A3107039298F5E70C1699D36D1D37BF66B20",
            Convert.ToHexString(SHA1.HashData(second.DataFork.ToArray())));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }
}
