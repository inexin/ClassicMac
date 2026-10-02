# Code resources

Code that is not an application lives in resources the system loads and calls: definition procedures (`CDEF`,
`WDEF`, `MDEF`, `MBDF`, `LDEF`), drivers (`DRVR`), packages (`PACK`, `proc`), components (`thng` and their code) and
native code (`ndrv`, `nlib`, `ncod`, …). 68k code resources usually start with Apple's standard header; a driver has a
header of routine offsets; a dispatched package may start with an `$A9FF` table; a fat resource puts a Mixed Mode
routine descriptor (`$AAFE`) in front of a PEF container; native code is a PEF container from offset 0. ClassicMac
reads each of these headers and the PEF containers behind them.

| | |
| --- | --- |
| Identified by | By shape: `$600A` (or `$6000`, `$4EFA`) and a type at +$04; `$A9FF` at +$00; `$AAFE` at +$00 or at a standard header's branch target; `Joy!peff` at +$00. `DRVR` and `thng` by resource type |
| ClassicMac | Reads; `ClassicMac.Code.M68k` (`CodeResourceHeader`, `DriverHeader`, `PackageHeader`, `ComponentResource`, `RoutineDescriptor`), `ClassicMac.Code.Ppc.PefContainer` |
| Verified against | The Mac OS 9 System file's code resources; Disk Copy 6.1.2's `DRVR` |
| Sources | *Inside Macintosh: Devices* (the driver header); *Inside Macintosh: More Macintosh Toolbox* (the Component Manager); *Inside Macintosh: PowerPC System Software* (the Mixed Mode Manager); `MixedMode.h`, `Components.h`. The `$A9FF` table, and the base its offsets count from, are fitted to the System file |

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [Writing](#3-writing)
4. [Variants](#4-variants)
5. [ClassicMac](#5-classicmac)
6. [Diagnostics](#6-diagnostics)
7. [Verification](#7-verification)
8. [Not covered](#8-not-covered)
9. [References](#9-references)

## 1. Layout

### 1.1 The standard code-resource header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Branch | `BRA.S` over the header: `$60xx`, usually `$600A` |
| +$02 | 2 | flags | 0 in most of the System's; 1 in `CDEF` −1 and 1 |
| +$04 | 4 | resourceType | The resource's type |
| +$08 | 2 | resourceID | Usually, not always, the resource's own ID |
| +$0A | 2 | version | |
| +$0C | | Code | |

Two other branch forms carry the same +$04 to +$0B fields, with the branch's displacement in place of the flags:
`$6000 disp.w` (`BRA.W`; the `'dcmp'` 0 and 1 decompressors, `PTCH`, `ptch`) and `$4EFA disp.w` (`JMP d16(PC)`;
some `ptch`). The branch target is 2 + the displacement. Execution starts at offset 0, on the branch. The branch
usually lands at +$0C, right after the header; some land further in (`PACK` 3's `$604E` at +$50, `BRA.W` to +$16 and
+$788) [Verified: the Mac OS 9 System file].

[Doc: Inside Macintosh, the definition procedure resource format] [Verified: the Mac OS 9 System file] The ID
differs from the resource's own in 8 of the System's 63 standard headers [Verified: the Mac OS 9 System file].

A code resource without the header is raw code, also entered at offset 0, usually on `LINK A6` (`$4E56`)
[Verified: the Mac OS 9 System file]. The type at +$04 is usually the resource's own; the System's `proc` −8224 names
`MDEF` (`$6010 $0000 'MDEF' $DFE0 $0000`) [Verified: the Mac OS 9 System file].

### 1.2 `DRVR`

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | drvrFlags | Below |
| +$02 | 2 | drvrDelay | Ticks between periodic actions |
| +$04 | 2 | drvrEMask | A desk accessory's event mask |
| +$06 | 2 | drvrMenu | A desk accessory's menu ID |
| +$08 | 2 | drvrOpen | The open routine's offset from the resource's start |
| +$0A | 2 | drvrPrime | The prime routine's offset |
| +$0C | 2 | drvrCtl | The control routine's offset |
| +$0E | 2 | drvrStatus | The status routine's offset |
| +$10 | 2 | drvrClose | The close routine's offset |
| +$12 | 1 + n | drvrName | Pascal string; may differ from the resource name |

| Flag | Name | Meaning |
| --- | --- | --- |
| `$0100` | dReadEnable | Handles Read |
| `$0200` | dWritEnable | Handles Write |
| `$0400` | dCtlEnable | Handles Control |
| `$0800` | dStatEnable | Handles Status |
| `$1000` | dNeedGoodBye | Wants a goodbye call when the heap is reinitialized |
| `$2000` | dNeedTime | Wants periodic time |
| `$4000` | dNeedLock | Must be locked in memory |

[Doc: Inside Macintosh: Devices, the driver header] Other bits of drvrFlags are set in real drivers
(`.PCCardSRAMDisk` `$6F04`, `.DSP` `$4440`), and a routine offset may be 0 (the prime routine of six video drivers)
[Verified: the Mac OS 9 System file]. Not every `DRVR` resource has the header: the System's `.ATADisk` starts
`$6000 $0102` [Verified: the Mac OS 9 System file].

### 1.3 The `$A9FF` package form

Some `PACK`, `proc` and `dimg` resources start with `_Debugger` guarding a header and a dispatch table:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Trap | `$A9FF` (`_Debugger`) |
| +$02 | 4 | Type | The resource's type |
| +$06 | 2 | ID | |
| +$08 | 2 | Version | |
| +$0A | 2 | Flags | Bit 0: the table has an entry for every second selector |
| +$0C | 1 | First selector | `i8` |
| +$0D | 1 | Last selector | `i8` |
| +$0E | 2 each | Table | A word offset per selector, first to last (stepping by 2 with flags bit 0); 0 for none |

[Fitted: the Mac OS 9 System file] A routine is at +$0A (the flags word) + its offset. Counted that way, all 277
nonzero offsets of the System's 14 `$A9FF` resources land inside the resource on a valid instruction, 196 on a `LINK`
and 22 right after the previous routine's return (`RTS`, `RTD`, `JMP (A0)`); counted from the resource start or from
the table at +$0E, few or none do. `PACK` 15's selector 0, offset `$07E0`, is the `LINK A6,#-4` at `$07EA`, right after
an `UNLK`; `RTS` [Verified: the Mac OS 9 System file]. `PACK` 8, 9, 11, 13–15 and seven `proc` use the form; `PACK`
2 has the standard header instead. A step of 2 runs from the first selector up to the last, so first 0 and last 5
gives 0, 2 and 4; `PACK` 11 runs from −12 to 60 (37 entries) [Verified: the Mac OS 9 System file].

### 1.4 Components (`thng`)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | componentType | |
| +$04 | 4 | componentSubType | |
| +$08 | 4 | componentManufacturer | |
| +$0C | 4 | componentFlags | |
| +$10 | 4 | componentFlagsMask | |
| +$14 | 6 | component | The code: a type and an `i16` ID |
| +$1A | 6 | componentName | A `'STR '` |
| +$20 | 6 | componentInfo | A `'STR '` |
| +$26 | 6 | componentIcon | An `'ICON'` |
| +$2C | 4 | componentVersion | Extended form only, as are the fields below |
| +$30 | 4 | componentRegisterFlags | |
| +$34 | 2 | componentIconFamily | |
| +$36 | 4 | count | The platform entries that follow |
| +$3A | 12 each | Platforms | Below |

A platform entry:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | componentFlags | |
| +$04 | 6 | component | The code for this platform: a type and an ID |
| +$0A | 2 | platformType | 1 68k, 2 PowerPC |

[Doc: Inside Macintosh: More Macintosh Toolbox, Component Manager; Components.h, ExtComponentResource] A `thng` of 44
bytes is the basic form; a longer one the extended form. The platform entries are in use when componentRegisterFlags
has componentHasMultiplePlatforms (`$08`) [Doc: Components.h]; in the System every `thng` with entries has it, and
the 11 extended ones without it list none [Verified: the Mac OS 9 System file]. The `component` field is whatever the
developer put there: in `thng` −21003 it is `cdek` −21003, the same PowerPC code its only platform entry names
[Verified: the Mac OS 9 System file]. A 68k platform's code (`sift`, a 68k `vdig`) is raw 68k code
entered at 0; a PowerPC platform's (`nift`, `cdek`, `vdig`, `scal`, `dcod`) is a PEF container from offset 0; every
resource a platform names exists [Verified: the Mac OS 9 System file].

### 1.5 Routine descriptors (`$AAFE`)

A fat code resource starts with a routine descriptor, either at offset 0 or right after a standard header whose branch
lands on it (+$0C):

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | goMixedModeTrap | `$AAFE` (`_MixedModeMagic`) |
| +$02 | 1 | version | 7 |
| +$03 | 1 | routineDescriptorFlags | `$20` behind a standard header, 0 at offset 0, in the samples |
| +$04 | 4 | reserved1 | |
| +$08 | 1 | reserved2 | |
| +$09 | 1 | selectorInfo | |
| +$0A | 2 | routineCount | The number of routine records minus 1 |
| +$0C | 20 each | Routine records | Below |

A routine record:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | procInfo | The calling convention and parameter sizes |
| +$04 | 1 | reserved1 | |
| +$05 | 1 | ISA | Low nibble: 0 68k, 1 PowerPC; high nibble: the runtime architecture |
| +$06 | 2 | routineFlags | Bit 0 (kProcDescriptorIsRelative): the procedure is relative to the descriptor; `$20` (kProcDescriptorIsIndex): it is an index, not an address or offset |
| +$08 | 4 | procDescriptor | An offset from the descriptor's start when relative; else an address |
| +$0C | 4 | reserved2 | |
| +$10 | 4 | selector | For a dispatched routine |

[Doc: Inside Macintosh: PowerPC System Software, Mixed Mode Manager; MixedMode.h] In every sample there is one
record, PowerPC, with routineFlags 7 (relative, needs preparing, native ISA); its procedure is a PEF container in the
resource ([pef.md](pef.md)) whose main entry point is where the routine starts [Verified: the Mac OS 9 System file].

### 1.6 Native code resources

Many resource types hold a PEF container from offset 0: `ncod`, `nlib`, `ndrv` (exporting `TheDriverDescription` and
`DoDriverIO`), `nift`, `fovr`, `ncmp`, `cdek`, `dcod`, `scal`, `vdig`, `ntrb`, `nitt`, `sfvr`, `ppct`, `pthg`, `qtcm`,
`hqda`, `ndmc`, `nsnd` [Verified: the Mac OS 9 System file]. They are read as [pef.md](pef.md) describes.

## 2. Reading

1. A resource starting `Joy!peff` is a PEF container ([pef.md](pef.md)).
2. A resource with `$AAFE` at offset 0, or behind a standard header whose branch lands on it, has a routine
   descriptor: read it (§1.5), and for each relative record that is not an index read the PEF container at
   descriptor + procDescriptor.
3. A resource starting `$A9FF` has the package form (§1.3): each nonzero offset's routine is at +$0A + the offset.
4. A resource with a branch form (§1.1) and a type at +$04 has the standard header; its code starts at the branch
   target. Any other is raw code from offset 0.
5. A `DRVR` has the driver header (§1.2); a `thng` is a component (§1.4).

[Doc: the Inside Macintosh volumes above] [Verified: the Mac OS 9 System file]

## 3. Writing

None.

## 4. Variants

- Placement of the routine descriptor: at offset 0 in `LDEF` 0, `expt` and `nsrd`; after a standard header in 29
  `CDEF`, 7 `WDEF`, `MDEF` and `MBDF` [Verified: the Mac OS 9 System file].
- A `DRVR` may be stored compressed; two of the System's are [Verified: the Mac OS 9 System file].

## 5. ClassicMac

- `CodeResourceHeader.Read` recognises the header only when the type at +$04 is the one asked for or, without one,
  four printable characters (`$20`–`$7E`); otherwise the resource is raw code (so `proc` −8224, read as a `proc`, is
  raw code entered on its branch). A branch landing outside the resource, or inside the header (below +$0C), is
  reported and the header kept. `BRA.L` (`$60FF`) is not taken as a header form. [ClassicMac]
- `DriverHeader.IsStandard` is false when a routine offset (at or past the resource's length) or the name falls
  outside the resource (the resource is not in the driver format); a resource shorter than `$13` bytes is not read.
  `Flags` keeps every bit, named or not. [ClassicMac]
- The `$A9FF` table is kept as stored; `PackageEntry.TargetOffset` gives each routine's place by §1.3. [ClassicMac]
- A `thng` between the basic and extended lengths is read in the basic form and reported; platform entries cut short
  are reported and the rest kept. Platform entries without componentHasMultiplePlatforms are read and reported.
  [ClassicMac]
- `RoutineDescriptor.Read` reads every record, keeping the descriptor flags and selectorInfo as stored; a relative
  target that does not start `Joy!peff` is left as 68k code (no container), and a container is read whatever the ISA.
  [ClassicMac]
- Decompressing a compressed resource is the caller's ([compressed-resources.md](../resources/compressed-resources.md)).
  [ClassicMac]

Code resources are exported with a listing and a model as [disassembly.md](../output/disassembly.md) describes.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `m68k.code-header-branch` | Warning | A standard header's branch lands outside the resource or inside the header | Keeps the header | Not traced |
| `m68k.drvr-nonstandard` | Warning | A `DRVR`'s offsets or name fall outside it, or it is shorter than the header | Marks it non-standard (or reads nothing) | Not traced |
| `m68k.package-range` | Warning | The last selector is before the first | Reads no entries | Not traced |
| `m68k.package-truncated` | Warning | The `$A9FF` header or table runs past the resource | Keeps the entries that fit | Not traced |
| `m68k.routine-descriptor-truncated` | Error | The descriptor's header or records run past the resource | Keeps the records that fit | Not traced |
| `m68k.routine-descriptor-version` | Warning | The version is not 7 | Reads on | Not traced |
| `m68k.routine-pef` | Error | A relative target starts `Joy!peff` but cannot be read | Leaves the record without a container | Not traced |
| `m68k.routine-target` | Error | A relative target lies outside the resource | Leaves the record without a container | Not traced |
| `m68k.thng-platform-truncated` | Warning | The platform entries run past the resource | Keeps the entries that fit | Not traced |
| `m68k.thng-platforms-unflagged` | Warning | A `thng` lists platforms without componentHasMultiplePlatforms | Keeps them | Not traced |
| `m68k.thng-truncated` | Warning | A `thng` is longer than the basic form but shorter than the extended one | Reads the basic form | Not traced |

The container's own codes (`pef.*`) are in [pef.md §6](pef.md#6-diagnostics).

## 7. Verification

- Hand-built resources (`tests/ClassicMac.Code.Tests/M68k`): `CodeResourceHeaderTests` (each branch form, flags 1,
  branches past +$0C, raw code, the type check with `proc` −8224's shape, `BRA.L`, branches outside and into the
  header), `DriverHeaderTests` (the header, the flags with unnamed bits, a routine offset of 0, offsets at the last
  byte and at the length, non-standard drivers), `PackageHeaderTests` (one offset per selector and its routine at
  +$0A + the offset, 0 for none, negative selectors, the step of 2 to an odd last selector and across 0, damage),
  `ComponentResourceTests` (the basic and extended forms, the base code spec, componentHasMultiplePlatforms set and
  clear, no platforms, a count of `$FFFFFFFF`, damage), `RoutineDescriptorTests` (at 0 and behind a header with their
  containers, absolute, 68k and CFM-68K routines, two records with selectors, descriptor flags and selectorInfo, an
  index procedure, non-PEF and outside targets, damage, the version).
- Gated on `CLASSICMAC_CODE_CORPUS` (`CodeResourceCorpusTests`, `M68kCorpusTests`; skipped without it):
  - the Mac OS 9 System file:
    - `CDEF` 0: a `BRA.S` header (ID 0, version `$0B`), the descriptor at +$0C (version 7, flags `$20`), one PowerPC
      relative record (procInfo `$3BB0`) whose container at +$2C has main 1:$28;
    - `LDEF` 0: the descriptor at 0, its container at +$20;
    - 41 fat resources (29 `CDEF`, 1 `LDEF`, 1 `MBDF`, 1 `MDEF`, 7 `WDEF`, 1 `expt`, 1 `nsrd`), every record with a
      container, compressed resources decompressed first;
    - 27 `DRVR` (2 compressed), three non-standard (`.ATADisk`, IDs −20268, −20267 and 53), one whose header name
      differs from its resource name (`.Display_Video_Apple_Civic` in `.Display_Video_Apple_Planaria`);
    - 47 `thng`, each platform naming an existing resource: raw code for 68k, a PEF container for PowerPC;
    - 14 `$A9FF` resources, each naming its own type, `PACK` 15 with 7 entries, its selector 0 at `$07EA`; all 277
      routines inside, on a valid instruction, 196 on a `LINK` and 22 after a return;
  - Disk Copy 6.1.2's `DRVR` 0: the standard `.HDI` driver, open at `$18`, close at `$30`.

## 8. Not covered

- Writing code resources.
- The `$A9FF` version and flags beyond bit 0 (kept as stored), and the dispatch code that reads the table: the +$0A
  base is fitted, not traced.
- The routine descriptor's flags (`$20`), selectorInfo and selectors in use, and descriptors with several records (68k
  and PowerPC pairs): read as stored, none seen.
- Other code-resource shapes: `gpch` patch tables, `ptch`/`PTCH` bodies, `scod`, `lodr`, `boot`, `krnl`, `vm`,
  `AINI`, `card`; the `'dcmp'` 2 and 3 entry tables.
- CFM-68K code resources.

## 9. References

1. Apple Computer, *Inside Macintosh: Devices* (1994), the driver header.
2. Apple Computer, *Inside Macintosh: More Macintosh Toolbox* (1993), the Component Manager.
3. Apple Computer, *Inside Macintosh: PowerPC System Software* (1994), the Mixed Mode Manager.
4. Apple Computer, Universal Interfaces 3.4: `MixedMode.h`, `Components.h`.
