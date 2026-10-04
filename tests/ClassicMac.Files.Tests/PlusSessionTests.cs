using ClassicMac.Core;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// An HFS Plus volume image in an edit session: First Aid repairs it and Save As writes the repair; every other edit is
// refused, since ClassicMac writes only HFS volumes.
public sealed class PlusSessionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cm-plus-session").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void A_volume_is_repaired_and_saved_but_not_otherwise_edited()
    {
        var image = FirstAidPlusTests.Base();
        FirstAidPlusTests.Put32(image, FirstAidPlusTests.Header + 32, 9);           // fileCount
        var path = Path.Combine(directory, "Plus.img");
        File.WriteAllBytes(path, image);

        var session = InputEditSession.Open(path);

        Assert.Equal(InputEditKind.HfsPlusVolume, session.Kind);
        Assert.Throws<InvalidOperationException>(() => session.AddFolder("New"));
        var result = session.Repair();
        Assert.True(result.Written);
        Assert.NotEmpty(session.Changes);
        var saved = Path.Combine(directory, "Repaired.img");
        session.SaveAs(saved);
        Assert.Equal(FirstAidVerdict.AppearsOk, HfsFirstAid.Verify(ForkData.FromFile(saved)).Verdict);
        Assert.Equal(image, File.ReadAllBytes(path));
    }
}
