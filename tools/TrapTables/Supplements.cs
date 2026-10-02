/// <summary>
/// What Multiversal does not give: trap words it lacks, the assembler trap-macro name where Multiversal uses the C
/// routine's name (<c>PBGetFInfo</c> is the trap <c>_GetFileInfo</c>, <c>InvertRect</c> is <c>_InverRect</c>), the
/// modifier bits of traps outside the Memory and File Managers, and the dispatchers it does not declare. These
/// override Multiversal's entry for the same word. All are Apple's names and rules [Doc: Inside Macintosh, the
/// trap macros of each manager's assembly-language summary, and the "Routine descriptors" chapter of Mac OS Runtime
/// Architectures for _MixedModeMagic].
/// </summary>
static class Supplements
{
    /// <summary>Trap word, macro name and modifier kind (null: keep the kind derived from Multiversal).</summary>
    public static readonly (ushort Word, string Name, string? Kind)[] Traps =
    [
        // File Manager and Device Manager (Inside Macintosh: Files; Devices). $400 = ASYNC; for _Control, _Status
        // and _KillIO $200 = IMMED.
        (0xA000, "Open", "File"), (0xA001, "Close", "File"), (0xA002, "Read", "File"), (0xA003, "Write", "File"),
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

        // Memory Manager (Inside Macintosh: Memory). $400 = SYS, $200 = CLEAR.
        (0xA019, "InitZone", "Memory"), (0xA027, "ReallocHandle", "Memory"), (0xA040, "ResrvMem", "Memory"),
        (0xA05C, "MemoryDispatch", "None"), (0xA15C, "MemoryDispatchA0Result", "None"),
        (0xA0A4, "HeapDispatch", "None"), (0xA22E, "BlockMoveData", "None"), (0xA055, "StripAddress", "None"),
        (0xA091, "Translate24To32", "None"),

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
    ];

    // Dispatchers Multiversal does not declare: the selector is a word in D0 (MOVE.W or MOVEQ before the trap).
    public static readonly (ushort Trap, string Name, string Location, string Width, uint Mask)[] Conventions =
    [
        (0xA260, "HFSDispatch", "D0", "Word", 0xFFFF), (0xA82A, "ComponentDispatch", "D0", "Word", 0xFFFF),
        (0xA825, "MenuDispatch", "D0", "Word", 0xFFFF), (0xAA73, "ControlDispatch", "D0", "Word", 0xFFFF),
        (0xABF2, "ThreadDispatch", "D0", "Word", 0xFFFF), (0xABFC, "TranslationDispatch", "D0", "Word", 0xFFFF),
    ];
}
