using ClassicMac.Core;
using ClassicMac.Resources.Editing;

namespace ClassicMac.Resources.Tests;

public class EditSessionTests
{
    private static readonly FourCC Str = FourCC.FromString("STR ");

    private static ResourceFork Fork()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, 2 }) { Name = MacString.FromMacRoman("first") });
        fork.Add(new Resource(Str, 129, new byte[] { 3 }));
        fork.Add(new Resource(FourCC.FromString("TEXT"), 128, new byte[] { 4 }) { Attributes = ResourceAttributes.Compressed });
        return fork;
    }

    [Fact]
    public void Edits_apply_undo_and_redo()
    {
        var session = new EditSession(Fork());
        var fork = session.Fork;
        var first = fork.Find(Str, 128)!;

        session.Execute(new DeleteResource(first));
        Assert.Null(fork.Find(Str, 128));
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.Same(first, fork.Resources[0]);                   // back where it was
        Assert.False(session.IsDirty);

        var duplicate = new DuplicateResource(first);
        session.Execute(duplicate);
        Assert.Equal(130, duplicate.Copy!.Id);                   // the next free ID from 128
        Assert.Equal(first.Name, duplicate.Copy.Name);

        session.Execute(new SetResourceInfo(first, 200, MacString.FromMacRoman("renamed"), ResourceAttributes.Purgeable));
        Assert.Equal((200, "renamed", ResourceAttributes.Purgeable), ((int)first.Id, first.Name!.Value.ToMacRoman(), first.Attributes));
        session.Undo();
        Assert.Equal((128, "first"), ((int)first.Id, first.Name!.Value.ToMacRoman()));
        session.Redo();
        Assert.Equal(200, first.Id);

        var text = fork.Find(FourCC.FromString("TEXT"), 128)!;
        session.Execute(new SetResourceData(text, new byte[] { 9, 9 }));
        Assert.Equal(ResourceAttributes.None, text.Attributes);  // new data is stored uncompressed
        session.Undo();
        Assert.Equal(ResourceAttributes.Compressed, text.Attributes);
        Assert.Equal(new byte[] { 4 }, text.GetData().ToArray());
    }

    [Fact]
    public void Saving_marks_the_session_clean_and_undo_past_it_dirty()
    {
        var session = new EditSession(Fork());
        session.Execute(new AddResource(Str, 300, null, new byte[] { 7 }));
        session.MarkSaved();
        Assert.False(session.IsDirty);
        session.Undo();
        Assert.True(session.IsDirty);
        session.Redo();
        Assert.False(session.IsDirty);
        session.Undo();
        session.Execute(new AddResource(Str, 301, null, new byte[] { 8 }));   // the saved state can no longer be reached
        Assert.True(session.IsDirty);
        session.Undo();
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void The_rules_refuse_clashes_and_hand_set_compression()
    {
        var fork = Fork();
        var first = fork.Find(Str, 128)!;
        Assert.Equal(["edit.duplicate-id"], ResourceEditRules.Check(fork, Str, 129, null, 0, first).Select(d => d.Code));
        Assert.Empty(ResourceEditRules.Check(fork, Str, 128, null, 0, first));
        Assert.Equal(["edit.compressed"], ResourceEditRules.Check(fork, Str, 500, null, ResourceAttributes.Compressed).Select(d => d.Code));
        Assert.Equal(["edit.reserved-id"], ResourceEditRules.Check(fork, Str, 5, null, 0).Select(d => d.Code));
        Assert.Equal(DiagnosticSeverity.Warning, ResourceEditRules.Check(fork, Str, 5, null, 0)[0].Severity);
    }

    [Fact]
    public void Forks_are_compared_by_content()
    {
        var a = Fork();
        var b = ResourceFork.Read(a.ToArray());
        Assert.Empty(ResourceForkComparison.Differences(a, b));
        b.Find(Str, 129)!.SetData(new byte[] { 0 });
        b.Remove(b.Find(Str, 128)!);
        Assert.Equal(2, ResourceForkComparison.Differences(a, b).Count);
    }
}
