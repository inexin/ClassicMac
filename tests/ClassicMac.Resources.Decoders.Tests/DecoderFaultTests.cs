using ClassicMac.Core;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// A decoder failing on damaged data with an index or arithmetic exception has a bug: it is reported as an error of its
// own (image.decoder-fault), not as data that cannot be decoded, and the resource is still written raw.
public class DecoderFaultTests
{
    private sealed class Faulty(Exception exception) : ImageDecoder(DecodeOptions.Default, "test.faulty", "TEST")
    {
        protected override IReadOnlyList<DecodedFile> DecodeImages(DecodeInput input) => throw exception;
    }

    private static List<Diagnostic> Decode(Exception exception)
    {
        var resource = new Resource(FourCC.FromString("TEST"), 128, new byte[] { 1, 2, 3 });
        var fork = new ResourceFork();
        fork.Add(resource);
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(new Faulty(exception).Decode(new DecodeInput(resource, resource.GetData(), fork, diagnostics: diagnostics)));
        return diagnostics;
    }

    [Fact]
    public void An_index_or_arithmetic_failure_is_a_decoder_fault()
    {
        foreach (var exception in new Exception[] { new IndexOutOfRangeException(), new OverflowException(), new DivideByZeroException() })
        {
            var fault = Assert.Single(Decode(exception));
            Assert.Equal(("image.decoder-fault", DiagnosticSeverity.Error), (fault.Code, fault.Severity));
            Assert.Contains(exception.GetType().Name, fault.Message);
        }
    }

    [Fact]
    public void Damaged_data_is_still_undecodable()
    {
        var diagnostic = Assert.Single(Decode(new InvalidDataException("bad")));
        Assert.Equal(("image.undecodable", DiagnosticSeverity.Warning), (diagnostic.Code, diagnostic.Severity));
    }
}
