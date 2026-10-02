using ClassicMac.Code.Disassembly;

namespace ClassicMac.Code.Tests.Disassembly;

public class TrapNamesTests
{
    [Theory]
    [InlineData(0xA9F0, "_LoadSeg")]
    [InlineData(0xA9F1, "_UnLoadSeg")]
    [InlineData(0xA9F4, "_ExitToShell")]
    [InlineData(0xA89F, "_Unimplemented")]
    [InlineData(0xA9FF, "_Debugger")]
    [InlineData(0xA9C9, "_SysError")]
    [InlineData(0xAAFE, "_MixedModeMagic")]
    [InlineData(0xA9A0, "_GetResource")]
    [InlineData(0xA8A4, "_InverRect")]          // the trap macro, not the C routine InvertRect
    [InlineData(0xA00C, "_GetFileInfo")]
    [InlineData(0xA200, "_HOpen")]
    [InlineData(0xA260, "_HFSDispatch")]
    [InlineData(0xA060, "_FSDispatch")]
    [InlineData(0xA1AD, "_Gestalt")]
    [InlineData(0xA3AD, "_NewGestalt")]
    [InlineData(0xA22E, "_BlockMoveData")]
    public void SpotChecks(int word, string expected) => Assert.Equal(expected, TrapNames.Describe((ushort)word));

    [Theory]
    [InlineData(0xA11E, "_NewPtr")]
    [InlineData(0xA31E, "_NewPtr ,CLEAR")]
    [InlineData(0xA51E, "_NewPtr ,SYS")]
    [InlineData(0xA71E, "_NewPtr ,SYS,CLEAR")]
    [InlineData(0xA722, "_NewHandle ,SYS,CLEAR")]
    [InlineData(0xA401, "_Close ,ASYNC")]
    [InlineData(0xA402, "_Read ,ASYNC")]
    [InlineData(0xA600, "_HOpen ,ASYNC")]      // $A200 is _HOpen itself; $400 adds ASYNC
    [InlineData(0xA660, "_HFSDispatch ,ASYNC")]
    [InlineData(0xA41E, "_NewPtr ,SYS")]       // without bit 8: found by the key, $A11E's bit 8 not required
    [InlineData(0xA01E, "_NewPtr")]
    [InlineData(0xA162, "_PurgeSpace")]        // $A062 entry; bit 8 is not a named modifier
    [InlineData(0xA562, "_PurgeSpace ,SYS")]
    [InlineData(0xA400, "_Open ,ASYNC")]
    [InlineData(0xA20F, "_MountVol ,HFS")]
    [InlineData(0xA60F, "_MountVol ,ASYNC,HFS")]
    [InlineData(0xA204, "_Control ,IMMED")]
    [InlineData(0xA604, "_Control ,ASYNC,IMMED")]
    [InlineData(0xA63C, "_CmpString ,MARKS,CASE")]
    [InlineData(0xA43C, "_CmpString ,CASE")]
    [InlineData(0xA254, "_UprString ,MARKS")]
    [InlineData(0xA42F, "_PostEvent ,$400")]   // a trap whose bits have no names
    [InlineData(0xA63D, "_DrvrInstall ,$400,$200")]
    public void OsModifiers(int word, string expected) => Assert.Equal(expected, TrapNames.Describe((ushort)word));

    // Words with their own entries win over the key's entry plus modifiers.
    [Theory]
    [InlineData(0xA146, "_GetTrapAddress")]
    [InlineData(0xA346, "_GetOSTrapAddress")]
    [InlineData(0xA746, "_GetToolBoxTrapAddress")]
    [InlineData(0xA546, "_GetTrapAddress ,$400")]   // no entry: the most specific entry whose bits it has
    [InlineData(0xA047, "_SetTrapAddress")]
    [InlineData(0xA247, "_SetOSTrapAddress")]
    [InlineData(0xA647, "_SetToolBoxTrapAddress")]
    [InlineData(0xA056, "_LowerText")]
    [InlineData(0xA256, "_StripText")]
    [InlineData(0xA456, "_UpperText")]
    [InlineData(0xA656, "_StripUpperText")]
    [InlineData(0xA05C, "_MemoryDispatch")]
    [InlineData(0xA15C, "_MemoryDispatchA0Result")]
    [InlineData(0xA02F, "_PostEvent")]
    [InlineData(0xA12F, "_PPostEvent")]
    [InlineData(0xA02E, "_BlockMove")]
    [InlineData(0xA22E, "_BlockMoveData")]
    [InlineData(0xA1AD, "_Gestalt")]
    [InlineData(0xA3AD, "_NewGestalt")]
    [InlineData(0xA5AD, "_ReplaceGestalt")]
    [InlineData(0xA7AD, "_GetGestaltProcPtr")]
    [InlineData(0xA008, "_Create")]
    [InlineData(0xA208, "_HCreate")]
    [InlineData(0xA608, "_HCreate ,ASYNC")]
    public void AliasedEntries(int word, string expected) => Assert.Equal(expected, TrapNames.Describe((ushort)word));

    [Fact]
    public void DontPreserveA0()
    {
        var info = TrapNames.Lookup(0xA11E);
        Assert.True(info.DontPreserveA0);
        Assert.False(info.IsToolbox);
        Assert.Empty(info.Modifiers);
        Assert.False(TrapNames.Lookup(0xA01F).DontPreserveA0);
        Assert.True(TrapNames.Lookup(0xA162).DontPreserveA0);    // found by the key
        Assert.False(TrapNames.Lookup(0xA01E).DontPreserveA0);
        Assert.True(TrapNames.Lookup(0xA1FA).DontPreserveA0);    // unknown OS trap
        Assert.False(TrapNames.Lookup(0xA9A0).DontPreserveA0);   // bit 8 of a Toolbox trap is its number
    }

    [Fact]
    public void ModifierList()
    {
        Assert.Equal(["SYS", "CLEAR"], TrapNames.Lookup(0xA71E).Modifiers);
        Assert.Equal(["ASYNC", "HFS"], TrapNames.Lookup(0xA60F).Modifiers);
        Assert.Equal("NewPtr", TrapNames.Lookup(0xA71E).Name);
        Assert.True(TrapNames.TryGetName(0xA71E, out string name));
        Assert.Equal("NewPtr", name);
    }

    [Fact]
    public void MixedModeMagic()
    {
        Assert.Equal("_MixedModeMagic", TrapNames.Describe(0xAAFE));
        Assert.True(TrapNames.Lookup(0xAAFE).IsToolbox);
    }

    [Fact]
    public void NotATrap()
    {
        Assert.False(TrapNames.IsTrap(0x4E75));
        Assert.False(TrapNames.IsTrap(0xB000));
        Assert.True(TrapNames.IsTrap(0xA000));
        Assert.True(TrapNames.IsTrap(0xAFFF));
        Assert.Null(TrapNames.Lookup(0x4E75).Name);
    }

    [Fact]
    public void AutoPop()
    {
        Assert.Equal("_GetResource ,AUTOPOP", TrapNames.Describe(0xADA0));
        var info = TrapNames.Lookup(0xADA0);
        Assert.True(info.IsToolbox);
        Assert.True(info.AutoPop);
        Assert.Equal("GetResource", info.Name);
        Assert.False(TrapNames.Lookup(0xA9A0).AutoPop);
        Assert.Equal("_LoadSeg ,AUTOPOP", TrapNames.Describe(0xADF0));
        Assert.Equal("_MixedModeMagic ,AUTOPOP", TrapNames.Describe(0xAEFE));
        Assert.Equal("_AF00", TrapNames.Describe(0xAF00));   // unknown: raw, auto-pop or not
        Assert.True(TrapNames.Lookup(0xAF00).AutoPop);
    }

    [Theory]
    [InlineData(0xA0FA, "_A0FA")]
    [InlineData(0xAB00, "_AB00")]
    [InlineData(0xAF00, "_AF00")]
    [InlineData(0x4E75, "_4E75")]
    public void Unknown(int word, string expected)
    {
        Assert.Equal(expected, TrapNames.Describe((ushort)word));
        Assert.False(TrapNames.TryGetName((ushort)word, out _));
    }
}

public class SelectorNamesTests
{
    [Theory]
    [InlineData(0xA9EE, 0u, "NumToString")]        // Pack7, stack word
    [InlineData(0xA9EE, 1u, "StringToNum")]
    [InlineData(0xAA5A, 1u, "GetSharedLibrary")]   // CodeFragmentDispatch, stack word
    [InlineData(0xA8B5, 0x8008FFF2u, "LongDateToSeconds")] // ScriptUtil, stack long
    [InlineData(0xA8B5, 0xFFF2u, "LongDateToSeconds")]     // its low word alone
    [InlineData(0xA260, 9u, "PBGetCatInfo")]       // HFSDispatch, D0 word
    [InlineData(0xA660, 9u, "PBGetCatInfo")]       // asynchronous
    [InlineData(0xA1AD, 0x73797376u, "gestaltSystemVersion")] // Gestalt, 'sysv' in D0
    [InlineData(0xAB1D, 0x40001u, "LockPixels")]   // QDExtensions, D0 long
    [InlineData(0xAB1D, 0x14u, "OffscreenVersion")]
    [InlineData(0xA8FD, 0x04000C00u, "PrOpenDoc")] // PrGlue, stack long
    [InlineData(0xA822, 0xFFFFFFF1u, "ReadPartialResource")] // ResourceDispatch: D0 masked to $F
    [InlineData(0xAAA2, 0x1200u, "Entry2Index")]   // PaletteDispatch: D0 masked to $FF
    [InlineData(0xAA59, 0u, "NewRoutineDescriptor")] // MixedModeDispatch, D0 word
    [InlineData(0xA816, 0x0204u, "AEDisposeDesc")] // Pack8, D0 word
    [InlineData(0xADEE, 1u, "StringToNum")]        // a dispatcher called auto-pop
    public void Names(int trap, uint selector, string expected)
    {
        Assert.True(SelectorNames.TryGet((ushort)trap, selector, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(0xA9EE, SelectorLocation.Stack, SelectorWidth.Word)]
    [InlineData(0xA83D, SelectorLocation.Stack, SelectorWidth.Word)]   // TEDispatch
    [InlineData(0xA88F, SelectorLocation.Stack, SelectorWidth.Word)]   // OSDispatch
    [InlineData(0xA8B5, SelectorLocation.Stack, SelectorWidth.Long)]   // ScriptUtil
    [InlineData(0xA260, SelectorLocation.D0, SelectorWidth.Word)]      // HFSDispatch
    [InlineData(0xA816, SelectorLocation.D0, SelectorWidth.Word)]      // Pack8
    [InlineData(0xA82A, SelectorLocation.D0, SelectorWidth.Word)]      // ComponentDispatch
    [InlineData(0xABF2, SelectorLocation.D0, SelectorWidth.Word)]      // ThreadDispatch
    [InlineData(0xA1AD, SelectorLocation.D0, SelectorWidth.OSType)]    // Gestalt
    [InlineData(0xAB1D, SelectorLocation.D0, SelectorWidth.Long)]      // QDExtensions
    [InlineData(0xA800, SelectorLocation.D0, SelectorWidth.Long)]      // SoundDispatch
    [InlineData(0xA8FD, SelectorLocation.Stack, SelectorWidth.Long)]   // PrGlue
    [InlineData(0xAA5A, SelectorLocation.Stack, SelectorWidth.Word)]   // CodeFragmentDispatch [Verified: 68k apps]
    [InlineData(0xAA59, SelectorLocation.D0, SelectorWidth.Word)]      // MixedModeDispatch
    [InlineData(0xA060, SelectorLocation.D0, SelectorWidth.Word)]      // FSDispatch
    [InlineData(0xA825, SelectorLocation.D0, SelectorWidth.Word)]      // MenuDispatch
    [InlineData(0xAA73, SelectorLocation.D0, SelectorWidth.Word)]      // ControlDispatch
    [InlineData(0xABFC, SelectorLocation.D0, SelectorWidth.Word)]      // TranslationDispatch
    public void Conventions(int trap, SelectorLocation location, SelectorWidth width)
    {
        Assert.True(SelectorNames.TryGetConvention((ushort)trap, out var c));
        Assert.Equal(location, c.Location);
        Assert.Equal(width, c.Width);
    }

    [Fact]
    public void Masks()
    {
        Assert.True(SelectorNames.TryGetConvention(0xA822, out var c));
        Assert.Equal((0xA822, "ResourceDispatch", 0xFu), (c.Trap, c.Name, c.Mask));
        Assert.True(SelectorNames.TryGetConvention(0xA9EB, out c));     // Pack4: the opword's low byte
        Assert.Equal(0xFFu, c.Mask);
        Assert.True(SelectorNames.TryGetConvention(0xA800, out c));
        Assert.Equal(0xFFFFFFu, c.Mask);
        Assert.True(SelectorNames.TryGetConvention(0xA660, out c));     // the async HFSDispatch is HFSDispatch
        Assert.Equal(0xA260, c.Trap);
    }

    [Fact]
    public void Unknown()
    {
        Assert.False(SelectorNames.TryGet(0xA9EE, 0x7F, out _));
        Assert.False(SelectorNames.TryGet(0xA9A0, 0, out _));
        Assert.False(SelectorNames.TryGetConvention(0xA9A0, out _));
        Assert.False(SelectorNames.TryGet(0xA825, 0, out _));          // a dispatcher with no names in the tables
        Assert.False(SelectorNames.TryGet(0xA1AD, 0x7A7A7A7A, out _));  // 'zzzz'
    }
}

public class LowMemoryGlobalsTests
{
    [Fact]
    public void Lookups()
    {
        Assert.True(LowMemoryGlobals.TryGet(0x904, out var a5));
        Assert.Equal(("CurrentA5", 4), (a5.Name, a5.Size));
        Assert.True(LowMemoryGlobals.TryFind(0x16C, out var ticks, out int offset));
        Assert.Equal(("Ticks", 2), (ticks.Name, offset));
        Assert.False(LowMemoryGlobals.TryGet(0x16C, out _));
        Assert.True(LowMemoryGlobals.TryFind(0x16A, out _, out offset));
        Assert.Equal(0, offset);
        Assert.True(LowMemoryGlobals.TryGet(0x118, out var zone));
        Assert.Equal(("TheZone", 4, "THz"), (zone.Name, zone.Size, zone.Type));
        Assert.True(LowMemoryGlobals.TryFind(0x14A + 9, out var queue, out offset));   // the last byte of a QHdr
        Assert.Equal(("EventQueue", 10, 9), (queue.Name, queue.Size, offset));
    }

    [Fact]
    public void Misses()
    {
        Assert.False(LowMemoryGlobals.TryGet(0, out _));               // Executor's nilhandle is left out
        Assert.False(LowMemoryGlobals.TryFind(0, out _, out _));
        Assert.False(LowMemoryGlobals.TryFind(0xFFFFFFFF, out _, out _));
        Assert.False(LowMemoryGlobals.TryFind(0x14A + 10, out var g, out _) && g.Name == "EventQueue");
    }

    [Fact]
    public void Table()
    {
        var all = LowMemoryGlobals.All;
        Assert.True(all.Count > 200);
        for (int i = 1; i < all.Count; i++)
            Assert.True(all[i - 1].Address < all[i].Address);
        Assert.All(all, g => Assert.True(g.Size > 0 && char.IsUpper(g.Name[0])));
    }
}
