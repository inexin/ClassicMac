/// <summary>
/// What Multiversal does not give: trap words it lacks, the assembler trap-macro name where Multiversal uses the C
/// routine's name (<c>PBGetFInfo</c> is the trap <c>_GetFileInfo</c>, <c>InvertRect</c> is <c>_InverRect</c>), the
/// modifier bits of traps outside the Memory and File Managers, the dispatchers it does not declare, and the
/// selector names of dispatchers it leaves empty. These override Multiversal's entry for the same word or selector;
/// Removed drops Multiversal's words that Apple gives another. All are Apple's names and rules [Doc: Inside Macintosh,
/// the trap macros of each manager's assembly-language summary, and the "Routine descriptors" chapter of Mac OS
/// Runtime Architectures for _MixedModeMagic; Apple's Universal Interfaces 3.x (Traps.h, Devices.h, Resources.h);
/// Technical Q&amp;A FL07 for _FSDispatch].
/// </summary>
static class Supplements
{
    /// <summary>Trap word, macro name and modifier kind (null: keep the kind derived from Multiversal).</summary>
    public static readonly (ushort Word, string Name, string? Kind)[] Traps =
    [
        // File Manager and Device Manager (Inside Macintosh: Files; Devices). $400 = ASYNC. For _Close, _Read,
        // _Write, _Control, _Status and _KillIO $200 = IMMED (Inside Macintosh: Devices, PBRead and PBWrite: "set bit
        // 9 to execute it immediately"; Universal Interfaces 3.x Devices.h, PBCloseImmed $A201 … PBKillIOImmed
        // $A206). For the File Manager's own traps $200 is the HFS variant ($A200 _HOpen, $A20F _MountVol).
        (0xA000, "Open", "File"), (0xA001, "Close", "Device"), (0xA002, "Read", "Device"), (0xA003, "Write", "Device"),
        (0xA004, "Control", "Device"), (0xA005, "Status", "Device"), (0xA006, "KillIO", "Device"),
        (0xA007, "GetVolInfo", "File"), (0xA008, "Create", "File"), (0xA009, "Delete", "File"),
        (0xA00A, "OpenRF", "File"), (0xA00B, "Rename", "File"), (0xA00C, "GetFileInfo", "File"),
        (0xA00D, "SetFileInfo", "File"), (0xA00E, "UnmountVol", "File"), (0xA00F, "MountVol", "File"),
        (0xA010, "Allocate", "File"), (0xA011, "GetEOF", "File"), (0xA012, "SetEOF", "File"),
        (0xA013, "FlushVol", "File"), (0xA014, "GetVol", "File"), (0xA015, "SetVol", "File"), (0xA017, "Eject", "File"),
        (0xA018, "GetFPos", "File"), (0xA035, "OffLine", "File"), (0xA041, "SetFilLock", "File"),
        (0xA042, "RstFilLock", "File"), (0xA043, "SetFilType", "File"), (0xA044, "SetFPos", "File"),
        (0xA045, "FlushFile", "File"), (0xA200, "HOpen", "File"), (0xA207, "HGetVInfo", "File"),
        (0xA208, "HCreate", "File"), (0xA209, "HDelete", "File"), (0xA20A, "HOpenRF", "File"),
        (0xA20B, "HRename", "File"), (0xA20C, "HGetFileInfo", "File"), (0xA20D, "HSetFileInfo", "File"),
        (0xA20E, "HUnmountVol", "File"), (0xA210, "AllocContig", "File"), (0xA214, "HGetVol", "File"),
        (0xA215, "HSetVol", "File"), (0xA241, "HSetFLock", "File"), (0xA242, "HRstFLock", "File"),
        (0xA260, "HFSDispatch", "File"),
        // _FSDispatch | kAsyncMask is its asynchronous call (Technical Q&A FL07).
        (0xA060, "FSDispatch", "File"),

        // Memory Manager (Inside Macintosh: Memory). $400 = SYS, $200 = CLEAR.
        (0xA019, "InitZone", "Memory"), (0xA027, "ReallocHandle", "Memory"), (0xA040, "ResrvMem", "Memory"),
        (0xA05C, "MemoryDispatch", "None"), (0xA15C, "MemoryDispatchA0Result", "None"),
        (0xA0A4, "HeapDispatch", "None"), (0xA22E, "BlockMoveData", "None"), (0xA055, "StripAddress", "None"),
        (0xA091, "Translate24To32", "None"),
        // Apple's word for _PurgeSpace (Universal Interfaces 3.x Traps.h); Multiversal's $A062 is removed (Removed).
        (0xA162, "PurgeSpace", "Memory"),

        // Trap Manager (Inside Macintosh: Operating System Utilities): $200 = new OS table, $600 = new Toolbox table.
        (0xA146, "GetTrapAddress", "None"), (0xA047, "SetTrapAddress", "None"), (0xA247, "SetOSTrapAddress", "None"),
        (0xA346, "GetOSTrapAddress", "None"), (0xA647, "SetToolBoxTrapAddress", "None"),
        (0xA746, "GetToolBoxTrapAddress", "None"),

        // String comparison (Inside Macintosh: Operating System Utilities): $200 = MARKS, $400 = CASE.
        (0xA03C, "CmpString", "String"), (0xA050, "RelString", "String"), (0xA054, "UprString", "String"),
        (0xA056, "LowerText", "None"), (0xA256, "StripText", "None"), (0xA456, "UpperText", "None"),
        (0xA656, "StripUpperText", "None"),

        // Other OS traps (Inside Macintosh: Operating System Utilities; Devices; Processes; Macintosh Toolbox
        // Essentials).
        (0xA02F, "PostEvent", "None"), (0xA12F, "PPostEvent", "None"), (0xA030, "OSEventAvail", "None"),
        (0xA031, "GetOSEvent", "None"), (0xA032, "FlushEvents", "None"), (0xA03D, "DrvrInstall", "None"),
        (0xA03E, "DrvrRemove", "None"), (0xA04E, "AddDrive", "None"), (0xA04F, "RDrvrInstall", "None"),
        (0xA051, "ReadXPRam", "None"), (0xA052, "WriteXPRam", "None"), (0xA05B, "PowerOff", "None"),
        (0xA06C, "InitFS", "None"), (0xA06D, "InitEvents", "None"), (0xA06E, "SlotManager", "None"),
        (0xA071, "AttachVBL", "None"), (0xA072, "DoVBLTask", "None"), (0xA075, "SIntInstall", "None"),
        (0xA076, "SIntRemove", "None"), (0xA07C, "ADBOp", "None"), (0xA07D, "GetDefaultStartup", "None"),
        (0xA07E, "SetDefaultStartup", "None"), (0xA07F, "InternalWait", "None"), (0xA080, "GetVideoDefault", "None"),
        (0xA081, "SetVideoDefault", "None"), (0xA082, "DTInstall", "None"), (0xA083, "SetOSDefault", "None"),
        (0xA084, "GetOSDefault", "None"), (0xA085, "PMgrOp", "None"), (0xA285, "IdleUpdate", "None"),
        (0xA485, "IdleState", "None"), (0xA685, "SerialPower", "None"), (0xA086, "IOPInfoAccess", "None"),
        (0xA087, "IOPMsgRequest", "None"), (0xA088, "IOPMoveData", "None"), (0xA089, "SCSIAtomic", "None"),
        (0xA08A, "Sleep", "None"), (0xA28A, "SleepQInstall", "None"), (0xA48A, "SleepQRemove", "None"),
        (0xA08B, "CommToolboxDispatch", "None"), (0xA08D, "DebugUtil", "None"), (0xA08F, "DeferUserFn", "None"),
        (0xA092, "EgretDispatch", "None"), (0xA09F, "PowerDispatch", "None"), (0xA0AE, "VADBProc", "None"),
        (0xA0DD, "PPC", "None"), (0xA0FE, "TEFindWord", "None"), (0xA0FF, "TEFindLine", "None"),
        (0xA193, "Microseconds", "None"), (0xA1AD, "Gestalt", "None"), (0xA7AD, "GetGestaltProcPtr", "None"),
        // Universal Interfaces 3.x: Traps.h (_PowerMgrDispatch, _FSMDispatch) and Devices.h (DriverInstallReserveMem,
        // ONEWORDINLINE(0xA43D), which has no _ macro there).
        (0xA09E, "PowerMgrDispatch", "None"), (0xA0AC, "FSMDispatch", "None"),
        (0xA43D, "DriverInstallReserveMem", "None"),

        // Toolbox traps: the macro names (Inside Macintosh: More Macintosh Toolbox; Imaging With QuickDraw;
        // Macintosh Toolbox Essentials; Processes).
        (0xA80E, "Get1IxResource", null), (0xA80F, "Get1IxType", null), (0xA821, "MaxSizeRsrc", null),
        (0xA844, "X2Fix", null), (0xA846, "X2Frac", null), (0xA843, "Fix2X", null), (0xA845, "Frac2X", null),
        (0xA859, "BitXOr", null), (0xA875, "SetPBits", null), (0xA8A4, "InverRect", null),
        (0xA8B3, "InverRoundRect", null), (0xA8D5, "InverRgn", null), (0xA8DE, "SetRecRgn", null),
        (0xA8E7, "XOrRgn", null), (0xA8FF, "GetFName", null), (0xA90A, "CalcVBehind", null),
        (0xA911, "CheckUpDate", null), (0xA922, "BeginUpDate", null), (0xA923, "EndUpDate", null),
        (0xA93F, "GetItmIcon", null), (0xA940, "SetItmIcon", null), (0xA941, "GetItmStyle", null),
        (0xA942, "SetItmStyle", null), (0xA943, "GetItmMark", null), (0xA944, "SetItmMark", null),
        (0xA94A, "SetMFlash", null), (0xA953, "UpdtControl", null), (0xA978, "UpdtDialog", null),
        (0xA9A5, "SizeRsrc", null), (0xA9AD, "RmveResource", null), (0xA9BF, "GetRMenu", null),
        (0xA9C2, "SysEdit", null), (0xA9F2, "Launch", null), (0xA815, "SCSIDispatch", null), (0xA819, "XMunger", null),
        (0xA82B, "Pack9", null), (0xA82C, "Pack10", null), (0xA82F, "Pack13", null), (0xA833, "ScrnBitMap", null),
        (0xA8F7, "Layout", null), (0xA9AC, "AddReference", null), (0xA9AE, "RmveReference", null),
        (0xA9CA, "PutIcon", null), (0xA9E8, "Pack1", null), (0xA9F3, "Chain", null), (0xA9F8, "MethodDispatch", null),
        (0xAA38, "UpdatePixMap", null), (0xAA53, "DictionaryDispatch", null), (0xAA54, "TextServicesDispatch", null),
        (0xAA57, "DockingDispatch", null), (0xAADB, "CursorDeviceDispatch", null), (0xABC3, "NQDMisc", null),
        (0xABF8, "StdOpcodeProc", null),

        // Segment Loader (Inside Macintosh: Processes) and the Trap Manager's unimplemented-trap handler.
        (0xA9F0, "LoadSeg", null), (0xA9F1, "UnLoadSeg", null), (0xA89F, "Unimplemented", null),
        // goMixedModeTrap, the first word of a routine descriptor (data, not an instruction) [Doc: Mac OS Runtime
        // Architectures].
        (0xAAFE, "MixedModeMagic", null),
        // Dispatchers (Inside Macintosh: More Macintosh Toolbox; QuickTime Components; Thread Manager; Macintosh
        // Easy Open).
        (0xA82A, "ComponentDispatch", null), (0xA825, "MenuDispatch", null), (0xAA73, "ControlDispatch", null),
        (0xABF2, "ThreadDispatch", null), (0xABFC, "TranslationDispatch", null),
        // Mac OS 8 (Universal Interfaces 3.x Traps.h).
        (0xAA74, "AppearanceDispatch", null),
    ];

    /// <summary>Multiversal's words that Apple's interfaces give another word: _PurgeSpace is $A162.</summary>
    public static readonly ushort[] Removed = [0xA062];

    // Dispatchers Multiversal does not declare (the selector is a word in D0: MOVE.W or MOVEQ before the trap), and
    // conventions that replace Multiversal's: _FP68K's selector is its whole opword (see Fp68k below), where
    // Multiversal keeps only the low byte.
    public static readonly (ushort Trap, string Name, string Location, string Width, uint Mask)[] Conventions =
    [
        (0xA260, "HFSDispatch", "D0", "Word", 0xFFFF), (0xA82A, "ComponentDispatch", "D0", "Word", 0xFFFF),
        (0xA825, "MenuDispatch", "D0", "Word", 0xFFFF), (0xAA73, "ControlDispatch", "D0", "Word", 0xFFFF),
        (0xABF2, "ThreadDispatch", "D0", "Word", 0xFFFF), (0xABFC, "TranslationDispatch", "D0", "Word", 0xFFFF),
        (0xA9EB, "Pack4", "Stack", "Word", 0xFFFF),
        // _ResourceDispatch: MOVEQ #sel,D0, the whole word (Universal Interfaces 3.x Resources.h: $7001
        // ReadPartialResource … $7010 FSpResourceFileAlreadyOpen), where Multiversal masks to $F.
        (0xA822, "ResourceDispatch", "D0", "Word", 0xFFFF),
    ];

    /// <summary>
    /// Selector names Multiversal lacks; they replace its entry for the same selector. A D0 selector set by MOVEQ is
    /// sign-extended, so MOVEQ #-1 is $FFFF once masked to the word.
    /// </summary>
    public static IEnumerable<(ushort Trap, uint Selector, string Name)> Selectors =>
        Fp68k().Concat(Elems68k).Concat(Pack7).Concat(Components).Concat(Menus).Concat(Controls).Concat(Threads)
            .Concat(Translation).Concat(Resources);

    // _ResourceDispatch selectors Multiversal lacks [Doc: Universal Interfaces 3.x Resources.h].
    private static readonly (ushort, uint, string)[] Resources = [(0xA822, 0x10, "FSpResourceFileAlreadyOpen")];

    /// <summary>
    /// _FP68K (_Pack4) opwords, named as the SANE assembler macros name them [Doc: Apple Numerics Manual, second
    /// edition, the FP68K operations and their macros]. The opword is the operation code (bits 0–4: FOADD = $0000
    /// … FOCLASS = $001C, FOSETENV = $0001 … FOTESTXCP = $001B) plus the operand format (bits 11–13: FFEXT = $0000,
    /// FFDBL = $0800, FFSGL = $1000, FFINT = $2000, FFLNG = $2800, FFCOMP = $3000) plus FFEXT96 = $0020 for the
    /// 96-bit extended type of the 68881. The name is the operation and the format together: FADDD is FOADD +
    /// FFDBL, so the listing shows what is being added; the format letter is X, D, S, I, L or C and a 96-bit form
    /// ends in 96. Only the combinations the macros define are named (FOZ2X takes the source's format: FS2X;
    /// FOX2Z the destination's: FX2S; FOB2D, FOD2B, FOCLASS and FONEXT take some formats, the one-operand
    /// operations extended only, FOSCALB always FFINT, the environment calls none), so an opword outside them
    /// keeps its number.
    /// </summary>
    public static IEnumerable<(ushort Trap, uint Selector, string Name)> Fp68k()
    {
        const ushort Trap = 0xA9EB;
        const uint Ext96 = 0x0020;
        (string Letter, uint Bits)[] all = [("X", 0x0000), ("D", 0x0800), ("S", 0x1000), ("I", 0x2000), ("L", 0x2800), ("C", 0x3000)];
        var notExt = all[1..];
        static (string, uint)[] Pick(params string[] letters) =>
            [.. letters.Select(l => (l, l switch { "X" => 0x0000u, "D" => 0x0800u, "S" => 0x1000u, "I" => 0x2000u, "L" => 0x2800u, _ => 0x3000u }))];

        // Two-operand arithmetic and comparisons: every format, each also in 96-bit form.
        foreach (var (op, code) in new[] { ("ADD", 0x00u), ("SUB", 0x02u), ("MUL", 0x04u), ("DIV", 0x06u), ("CMP", 0x08u), ("CPX", 0x0Au), ("REM", 0x0Cu) })
            foreach (var (f, bits) in all)
            {
                yield return (Trap, bits | code, $"F{op}{f}");
                yield return (Trap, bits | Ext96 | code, $"F{op}{f}96");
            }
        // FOZ2X ($000E): the format is the source's.
        foreach (var (f, bits) in all)
        {
            yield return (Trap, bits | 0x0E, $"F{f}2X");
            yield return (Trap, bits | Ext96 | 0x0E, $"F{f}2X96");
        }
        // FOX2Z ($0010): the format is the destination's; extended to extended is FOZ2X.
        foreach (var (f, bits) in notExt)
        {
            yield return (Trap, bits | 0x10, $"FX2{f}");
            yield return (Trap, bits | Ext96 | 0x10, $"FX2{f}96");
        }
        // FOB2D ($000B) and FOD2B ($0009): binary to and from a Decimal record; only extended has a 96-bit form.
        foreach (var (f, bits) in all)
        {
            yield return (Trap, bits | 0x0B, $"F{f}2DEC");
            yield return (Trap, bits | 0x09, $"FDEC2{f}");
        }
        yield return (Trap, Ext96 | 0x0B, "FX2DEC96");
        yield return (Trap, Ext96 | 0x09, "FDEC2X96");
        // FOCLASS ($001C) and FONEXT ($0013): the floating formats only (FCLASS also comp).
        foreach (var (f, bits) in Pick("X", "D", "S", "C"))
            yield return (Trap, bits | 0x1C, $"FCLASS{f}");
        foreach (var (f, bits) in Pick("X", "D", "S"))
            yield return (Trap, bits | 0x13, $"FNEXT{f}");
        yield return (Trap, Ext96 | 0x1C, "FCLASSX96");
        yield return (Trap, Ext96 | 0x13, "FNEXTX96");
        // One-operand operations on extended (FORTI is FRINTX, FOTTI FTINTX).
        foreach (var (op, code) in new[] { ("SQRT", 0x12u), ("RINT", 0x14u), ("TINT", 0x16u), ("LOGB", 0x1Au), ("CPYSGN", 0x11u), ("NEG", 0x0Du), ("ABS", 0x0Fu) })
        {
            yield return (Trap, code, $"F{op}X");
            yield return (Trap, Ext96 | code, $"F{op}X96");
        }
        // FOSCALB ($0018): an extended scaled by an integer, so its format is always FFINT.
        yield return (Trap, 0x2000 | 0x18, "FSCALBX");
        yield return (Trap, 0x2000 | Ext96 | 0x18, "FSCALBX96");
        // The environment calls, with no operand format.
        foreach (var (name, code) in new[] { ("FSETENV", 0x01u), ("FGETENV", 0x03u), ("FSETHV", 0x05u), ("FGETHV", 0x07u), ("FSETXCP", 0x15u), ("FPROCENTRY", 0x17u), ("FPROCEXIT", 0x19u), ("FTESTXCP", 0x1Bu) })
            yield return (Trap, code, name);
    }

    // _Elems68K (_Pack5) opwords and their macros [Doc: Apple Numerics Manual, second edition, the elementary
    // functions]. Bits 15 and 14 count the operands (FOXPWRI = $8010, FOCOMPOUND = $C014) and fall outside
    // Multiversal's $FF mask; ELEXT96 = $0080 marks the 96-bit forms and stays inside it.
    private static IEnumerable<(ushort, uint, string)> Elems68k =>
        new[]
        {
            ("FLNX", 0x00u), ("FLOG2X", 0x02u), ("FLN1X", 0x04u), ("FLOG21X", 0x06u), ("FEXPX", 0x08u), ("FEXP2X", 0x0Au),
            ("FEXP1X", 0x0Cu), ("FEXP21X", 0x0Eu), ("FXPWRI", 0x10u), ("FXPWRY", 0x12u), ("FCOMPOUND", 0x14u),
            ("FANNUITY", 0x16u), ("FSINX", 0x18u), ("FCOSX", 0x1Au), ("FTANX", 0x1Cu), ("FATANX", 0x1Eu), ("FRANDX", 0x20u),
        }.SelectMany(e => new[] { ((ushort)0xA9EC, e.Item2, e.Item1), ((ushort)0xA9EC, e.Item2 | 0x80, e.Item1 + "96") });

    // _Pack7 (DecStr68K) selectors 2–4, SANE's decimal-string conversions, by their macros [Doc: Apple Numerics
    // Manual, second edition, "Conversions between decimal strings and decimal records"].
    private static readonly (ushort, uint, string)[] Pack7 =
        [(0xA9EE, 2, "FPSTR2DEC"), (0xA9EE, 3, "FDEC2STR"), (0xA9EE, 4, "FCSTR2DEC")];

    // _ComponentDispatch, MOVEQ #sel,D0 [Doc: Inside Macintosh: More Macintosh Toolbox, "Summary of the Component
    // Manager", trap macros requiring routine selectors]. _GetComponentIconSuite ($29) is in the Component Manager
    // interface's inline glue. D0 = 0 has the Component Manager call the component itself rather than field the
    // request [Doc: ibid., "Defining a Component's Interfaces"]; it is named after the interface's ComponentCallNow
    // macro, which makes every such call. $FF is shared by CallComponentFunction and
    // CallComponentFunctionWithStorage; the first is the name.
    private static readonly (ushort, uint, string)[] Components =
    [
        (0xA82A, 0x00, "ComponentCallNow"), (0xA82A, 0x01, "RegisterComponent"), (0xA82A, 0x02, "UnregisterComponent"),
        (0xA82A, 0x03, "CountComponents"), (0xA82A, 0x04, "FindNextComponent"), (0xA82A, 0x05, "GetComponentInfo"),
        (0xA82A, 0x06, "GetComponentListModSeed"), (0xA82A, 0x07, "OpenComponent"), (0xA82A, 0x08, "CloseComponent"),
        (0xA82A, 0x0A, "GetComponentInstanceError"), (0xA82A, 0x0B, "SetComponentInstanceError"),
        (0xA82A, 0x0C, "GetComponentInstanceStorage"), (0xA82A, 0x0D, "SetComponentInstanceStorage"),
        (0xA82A, 0x0E, "GetComponentInstanceA5"), (0xA82A, 0x0F, "SetComponentInstanceA5"),
        (0xA82A, 0x10, "GetComponentRefcon"), (0xA82A, 0x11, "SetComponentRefcon"),
        (0xA82A, 0x12, "RegisterComponentResource"), (0xA82A, 0x13, "CountComponentInstances"),
        (0xA82A, 0x14, "RegisterComponentResourceFile"), (0xA82A, 0x15, "OpenComponentResFile"),
        (0xA82A, 0x18, "CloseComponentResFile"), (0xA82A, 0x1C, "CaptureComponent"), (0xA82A, 0x1D, "UncaptureComponent"),
        (0xA82A, 0x1E, "SetDefaultComponent"), (0xA82A, 0x21, "OpenDefaultComponent"), (0xA82A, 0x24, "DelegateComponentCall"),
        (0xA82A, 0x29, "GetComponentIconSuite"), (0xA82A, 0xFFFF, "CallComponentFunction"),
    ];

    // _MenuDispatch, MOVE.W #sel,D0: the high byte is the parameter size in words [Doc: Inside Macintosh: Macintosh
    // Toolbox Essentials, "Menu Manager" (InsertFontResMenu, InsertIntlResMenu); the Mac OS 8 Menu Manager
    // interface's inline glue for the rest]. SetMenuItemTextEncoding keeps the glue of its ScriptCode
    // predecessor ($0408).
    private static readonly (ushort, uint, string)[] Menus =
    [
        (0xA825, 0x0400, "InsertFontResMenu"), (0xA825, 0x0601, "InsertIntlResMenu"), (0xA825, 0x020C, "MenuEvent"),
        (0xA825, 0x0502, "SetMenuItemCommandID"), (0xA825, 0x0503, "GetMenuItemCommandID"),
        (0xA825, 0x0404, "SetMenuItemModifiers"), (0xA825, 0x0505, "GetMenuItemModifiers"),
        (0xA825, 0x0606, "SetMenuItemIconHandle"), (0xA825, 0x0707, "GetMenuItemIconHandle"),
        (0xA825, 0x0408, "SetMenuItemTextEncoding"), (0xA825, 0x0509, "GetMenuItemTextEncoding"),
        (0xA825, 0x050A, "SetMenuItemRefCon"), (0xA825, 0x050B, "GetMenuItemRefCon"),
        (0xA825, 0x040D, "SetMenuItemHierarchicalID"), (0xA825, 0x050E, "GetMenuItemHierarchicalID"),
        (0xA825, 0x040F, "SetMenuItemFontID"), (0xA825, 0x0510, "GetMenuItemFontID"),
        (0xA825, 0x0511, "SetMenuItemRefCon2"), (0xA825, 0x0512, "GetMenuItemRefCon2"),
        (0xA825, 0x0513, "SetMenuItemKeyGlyph"), (0xA825, 0x0514, "GetMenuItemKeyGlyph"),
        (0xA825, 0x0018, "IsMenuItemIconEnabled"), (0xA825, 0x0019, "EnableMenuItemIcon"),
        (0xA825, 0x0020, "DisableMenuItemIcon"),
    ];

    // _ControlDispatch, MOVE.W #sel,D0 [Doc: Mac OS 8 Control Manager Reference; the Mac OS 8 Control Manager
    // interface's inline glue].
    private static readonly (ushort, uint, string)[] Controls =
    [
        (0xAA73, 0x01, "CreateRootControl"), (0xAA73, 0x02, "GetRootControl"), (0xAA73, 0x03, "EmbedControl"),
        (0xAA73, 0x04, "AutoEmbedControl"), (0xAA73, 0x05, "IsControlActive"), (0xAA73, 0x06, "IsControlVisible"),
        (0xAA73, 0x07, "ActivateControl"), (0xAA73, 0x08, "DeactivateControl"), (0xAA73, 0x09, "FindControlUnderMouse"),
        (0xAA73, 0x0A, "HandleControlClick"), (0xAA73, 0x0B, "HandleControlKey"), (0xAA73, 0x0C, "IdleControls"),
        (0xAA73, 0x0D, "GetKeyboardFocus"), (0xAA73, 0x0E, "SetKeyboardFocus"), (0xAA73, 0x0F, "AdvanceKeyboardFocus"),
        (0xAA73, 0x10, "ReverseKeyboardFocus"), (0xAA73, 0x11, "GetControlFeatures"), (0xAA73, 0x12, "SetControlData"),
        (0xAA73, 0x13, "GetControlData"), (0xAA73, 0x14, "GetControlDataSize"), (0xAA73, 0x15, "GetSuperControl"),
        (0xAA73, 0x16, "CountSubControls"), (0xAA73, 0x17, "GetIndexedSubControl"),
        (0xAA73, 0x18, "DrawControlInCurrentPort"), (0xAA73, 0x19, "ClearKeyboardFocus"),
        (0xAA73, 0x1A, "SetControlSupervisor"), (0xAA73, 0x1B, "GetBestControlRect"),
        (0xAA73, 0x1C, "SetControlFontStyle"), (0xAA73, 0x1D, "SetUpControlBackground"),
        (0xAA73, 0x1E, "SetControlVisibility"), (0xAA73, 0xFFFE, "SendControlMessage"),
        (0xAA73, 0xFFFF, "DumpControlHierarchy"),
    ];

    // _ThreadDispatch, MOVE.W #sel,D0: the high byte is the parameter size in words [Doc: Thread Manager for
    // Macintosh Applications; the Thread Manager interface's inline glue]. YieldToAnyThread is CLR.L -(SP) then
    // YieldToThread's selector.
    private static readonly (ushort, uint, string)[] Threads =
    [
        (0xABF2, 0x0501, "CreateThreadPool"), (0xABF2, 0x0402, "GetFreeThreadCount"), (0xABF2, 0x0E03, "NewThread"),
        (0xABF2, 0x0504, "DisposeThread"), (0xABF2, 0x0205, "YieldToThread"), (0xABF2, 0x0206, "GetCurrentThread"),
        (0xABF2, 0x0407, "GetThreadState"), (0xABF2, 0x0508, "SetThreadState"), (0xABF2, 0x0209, "SetThreadScheduler"),
        (0xABF2, 0x070A, "SetThreadSwitcher"), (0xABF2, 0x000B, "ThreadBeginCritical"),
        (0xABF2, 0x000C, "ThreadEndCritical"), (0xABF2, 0x060D, "SetDebuggerNotificationProcs"),
        (0xABF2, 0x020E, "GetThreadCurrentTaskRef"), (0xABF2, 0x060F, "GetThreadStateGivenTaskRef"),
        (0xABF2, 0x0410, "SetThreadReadyGivenTaskRef"), (0xABF2, 0x0611, "SetThreadTerminator"),
        (0xABF2, 0x0512, "SetThreadStateEndCritical"), (0xABF2, 0x0413, "GetDefaultThreadStackSize"),
        (0xABF2, 0x0414, "ThreadCurrentStackSpace"), (0xABF2, 0x0615, "GetSpecificFreeThreadCount"),
    ];

    // _TranslationDispatch, MOVEQ #sel,D0 [Doc: Inside Macintosh: More Macintosh Toolbox, "Summary of the
    // Translation Manager" ($09, $0C, $1C, $1E); the Translation Manager interface's inline glue for the rest].
    private static readonly (ushort, uint, string)[] Translation =
    [
        (0xABFC, 0x09, "ExtendFileTypeList"), (0xABFC, 0x0C, "TranslateFile"), (0xABFC, 0x0E, "TranslateScrap"),
        (0xABFC, 0x16, "GetDocumentKindString"), (0xABFC, 0x1C, "GetFileTypesThatAppCanNativelyOpen"),
        (0xABFC, 0x1E, "CanDocBeOpened"), (0xABFC, 0x36, "GetTranslationExtensionName"),
        (0xABFC, 0x37, "GetPathFromTranslationDialog"), (0xABFC, 0x38, "GetFileTranslationPaths"),
    ];
}
