using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.App.Tests;

// The view-model's byte meanings for the hex inspector (E8): strings by their layout, other types through a TMPL in an
// open file, as the template form finds one.
public sealed class ByteMeaningTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-meaning").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private async Task<(MainViewModel Model, InputNode Input)> Open(string name, ResourceFork fork, MainViewModel? model = null)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, fork.ToArray());
        model ??= new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        return (model, input);
    }

    private static ResourceNode Node(InputNode input, string type) =>
        (ResourceNode)input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];

    [Fact]
    public async Task Strings_have_meanings_from_their_layout()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR#"), 128, new byte[] { 0, 1, 2, (byte)'h', (byte)'i' }));
        fork.Add(new Resource(FourCC.FromString("STR "), 128, new byte[] { 1, (byte)'x' }));
        fork.Add(new Resource(FourCC.FromString("STR "), 129, new byte[] { 1, (byte)'y' }) { Attributes = ResourceAttributes.Compressed });
        var (model, input) = await Open("Strings.rsrc", fork);
        Assert.Equal(new ByteMeaning("Character 2 of string 1, “hi”", 3, 2, "i"), model.MeaningAt(Node(input, "STR#"), 4));
        Assert.Equal(new ByteMeaning("Length of the string", 0, 1, "1"), model.MeaningAt(Node(input, "STR "), 0));
        Assert.Null(model.MeaningAt(Node(input, "STR "), 2));                 // past the end
        var compressed = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR ").Children.OfType<ResourceNode>().Single(r => r.Resource.Id == 129);
        Assert.Null(model.MeaningAt(compressed, 0));                         // the hex view shows its compressed bytes
    }

    [Fact]
    public async Task Other_types_use_a_template_from_an_open_file_or_have_none()
    {
        var data = new ResourceFork();
        data.Add(new Resource(FourCC.FromString("Rsrc"), 128, new byte[] { 0, 7 }));
        var (model, input) = await Open("Data.rsrc", data);
        var node = Node(input, "Rsrc");
        Assert.Null(model.MeaningAt(node, 0));                                 // no template open

        var templates = new ResourceFork();
        templates.Add(new Resource(FourCC.FromString("TMPL"), 1000, EditTests.Tmpl(("ID", "DWRD"))) { Name = MacString.FromMacRoman("Rsrc") });
        await Open("Templates.rsrc", templates, model);
        Assert.Equal(new ByteMeaning("ID", 0, 2, "7"), model.MeaningAt(node, 1));
        Assert.Null(model.MeaningAt(node, 2));
        Assert.Null(model.MeaningAt(node, -1));
    }
}
