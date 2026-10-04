using ClassicMac.Resources.Export;
using ClassicMac.Tests;

namespace ClassicMac.Resources.Decoders.Tests;

// Damaged resources through the decoders (PLAN "Hostile input"): every golden fixture's data mutated and decoded, as
// the exporter would decode it. A decoder may report the damage or refuse it as malformed (InvalidDataException,
// EndOfStreamException); any other exception, or a decode past its time limit, is a bug.
public sealed class DecoderMutationTests
{
    [Fact]
    public void Damaged_resources_are_reported_or_refused_as_malformed()
    {
        var fork = GoldenFixtures.Fork();
        var failures = new List<string>();
        var index = 0;
        foreach (var fixture in GoldenFixtures.All())
        {
            if (ResourceDecoders.Create(fixture.Options).FirstOrDefault(d => d.CanDecode(fixture.Resource.Type)) is not { } decoder)
            {
                continue;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var data = fixture.Resource.GetData().ToArray();
            for (var i = 0; i < Mutations.PerInput(50); i++)
            {
                var seed = index * 1000 + i;
                var resource = new Resource(fixture.Resource.Type, fixture.Resource.Id, Mutations.Mutate(data, new Random(seed)))
                {
                    Name = fixture.Resource.Name,
                };
                Mutations.Run($"{fixture.Key} (seed {seed})", () => decoder.Decode(new DecodeInput(resource, resource.GetData(), fork, diagnostics: [])),
                    failures);
            }

            index++;
            if (watch.Elapsed.TotalSeconds > 2)
            {
                TestContext.Current.SendDiagnosticMessage($"{fixture.Key}: {watch.Elapsed.TotalSeconds:0.0} s");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join("\n", failures.Take(60)));
    }
}
