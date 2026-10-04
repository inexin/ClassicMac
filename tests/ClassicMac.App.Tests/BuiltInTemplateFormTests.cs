using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.App.Tests;

// The built-in templates in the app: the template form and the byte meanings use them for types no open file has a
// TMPL for, and a TMPL in an open file wins.
public sealed class BuiltInTemplateFormTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-builtin").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private async Task<(MainViewModel Model, ResourceNode Node)> Open(string type, byte[] data, MainViewModel? model = null, string name = "Data.rsrc")
    {
        var path = Path.Combine(folder, name);
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString(type), 128, data));
        File.WriteAllBytes(path, fork.ToArray());
        model ??= new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        var node = (ResourceNode)input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
        model.Selected = node;
        return (model, node);
    }

    public static TheoryData<string> Types => [.. BuiltInTemplates.Types.Select(t => t.ToString())];

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Each_built_in_type_opens_in_the_template_form_and_round_trips(string type)
    {
        var builtIn = BuiltInTemplates.For(FourCC.FromString(type))!;
        var data = builtIn.Template.Write(builtIn.Template.NewValues());         // a new resource of the type
        var (model, _) = await Open(type, data);
        var form = Assert.IsType<TemplateForm>(model.Forms.Form);
        Assert.Equal($"Template: built in, from {builtIn.Source}", form.Source);
        Assert.Equal(data, form.BuildData());
    }

    [Fact]
    public async Task A_menu_bar_reads_and_edits_through_its_built_in_template()
    {
        var (model, node) = await Open("MBAR", [0, 2, 0, 128, 0, 129]);
        var form = Assert.IsType<TemplateForm>(model.Forms.Form);
        var list = Assert.IsType<TemplateListRow>(form.Fields[1]);
        Assert.Equal(["128", "129"], list.Items.Select(i => ((TemplateScalarRow)i.Fields[0]).Text));
        ((TemplateScalarRow)list.Items[1].Fields[0]).Text = "130";
        model.Forms.ApplyFormCommand.Execute(null);
        Assert.Equal(new byte[] { 0, 2, 0, 128, 0, 130 }, FindNode(model).Resource.GetData().ToArray());
        Assert.Equal(new ByteMeaning("Menu ID of item 2", 4, 2, "130"), model.TemplateFinder.MeaningAt(FindNode(model), 5));   // byte meanings use it too
    }

    private static ResourceNode FindNode(MainViewModel model) =>
        (ResourceNode)model.Roots[0].Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "MBAR").Children[0];

    [Fact]
    public async Task A_TMPL_in_an_open_file_wins_over_the_built_in_one()
    {
        var templates = Path.Combine(folder, "Templates.rsrc");
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("TMPL"), 1000, EditTests.Tmpl(("Raw", "HEXD"))) { Name = MacString.FromMacRoman("MBAR") });
        File.WriteAllBytes(templates, fork.ToArray());
        var model = new MainViewModel();
        await (await model.OpenAsync(templates))!.EnsureLoadedAsync();
        await Open("MBAR", [0, 1, 0, 128], model);
        var form = Assert.IsType<TemplateForm>(model.Forms.Form);
        Assert.Contains("TMPL 1000", form.Source);
        Assert.Equal("Raw", form.Fields.Single().Label);
    }

    [Fact]
    public async Task Types_with_a_form_of_their_own_keep_it()
    {
        var (model, _) = await Open("STR#", [0, 1, 1, (byte)'a']);
        Assert.IsType<StringListForm>(model.Forms.Form);
        Assert.False(model.Forms.HasTemplateChoice);                               // no built-in template for STR#
    }
}
