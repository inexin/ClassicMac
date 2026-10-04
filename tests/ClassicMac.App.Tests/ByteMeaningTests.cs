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
        Assert.Equal(new ByteMeaning("Character 2 of string 1, “hi”", 3, 2, "i"), model.TemplateFinder.MeaningAt(Node(input, "STR#"), 4));
        Assert.Equal(new ByteMeaning("Length of the string", 0, 1, "1"), model.TemplateFinder.MeaningAt(Node(input, "STR "), 0));
        Assert.Null(model.TemplateFinder.MeaningAt(Node(input, "STR "), 2));                 // past the end
        var compressed = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR ").Children.OfType<ResourceNode>().Single(r => r.Resource.Id == 129);
        Assert.Null(model.TemplateFinder.MeaningAt(compressed, 0));                         // the hex view shows its compressed bytes
    }

    // 'CODE' by its ID: 'CODE' 0's jump table, another segment's header and code (code-segments.md).
    [Fact]
    public async Task Code_resources_have_meanings_by_their_ID()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("CODE"), 0, new byte[] { 0, 0, 0, 0x28, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0x20, 0, 0, 0x3F, 0x3C, 0, 1, 0xA9, 0xF0 }));
        fork.Add(new Resource(FourCC.FromString("CODE"), 1, new byte[] { 0, 0, 0, 1, 0x4E, 0x75 }));
        var (model, input) = await Open("App.rsrc", fork);
        var segments = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "CODE").Children.OfType<ResourceNode>().ToList();
        Assert.Equal(new ByteMeaning("Entry 0: segment", 20, 2, "1"), model.TemplateFinder.MeaningAt(segments.Single(r => r.Resource.Id == 0), 21));
        Assert.Equal(new ByteMeaning("Number of jump-table entries", 2, 2, "1"), model.TemplateFinder.MeaningAt(segments.Single(r => r.Resource.Id == 1), 3));
        Assert.Equal(new ByteMeaning("Code, at +$0000", 4, 2, null), model.TemplateFinder.MeaningAt(segments.Single(r => r.Resource.Id == 1), 4));
    }

    [Fact]
    public async Task Other_types_use_a_template_from_an_open_file_or_have_none()
    {
        var data = new ResourceFork();
        data.Add(new Resource(FourCC.FromString("Rsrc"), 128, new byte[] { 0, 7 }));
        var (model, input) = await Open("Data.rsrc", data);
        var node = Node(input, "Rsrc");
        Assert.Null(model.TemplateFinder.MeaningAt(node, 0));                                 // no template open

        var templates = new ResourceFork();
        templates.Add(new Resource(FourCC.FromString("TMPL"), 1000, EditTests.Tmpl(("ID", "DWRD"))) { Name = MacString.FromMacRoman("Rsrc") });
        await Open("Templates.rsrc", templates, model);
        Assert.Equal(new ByteMeaning("ID", 0, 2, "7"), model.TemplateFinder.MeaningAt(node, 1));
        Assert.Null(model.TemplateFinder.MeaningAt(node, 2));
        Assert.Null(model.TemplateFinder.MeaningAt(node, -1));
    }
}
