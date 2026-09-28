# Sound resources — an implementer's specification

This document describes the classic Mac OS sound resource (`'snd '`) completely enough to write a reader that gives,
sample for sample, what the Mac OS 9 Sound Manager plays: the resource and its commands, the three sampled-sound
headers, the uncompressed sample formats, and the codecs that ship with Sound Manager 3.5.1 — MACE 3:1 and 6:1,
IMA 4:1 and µ-law. It also specifies what ClassicMac writes from a sound: a WAV file, a JSON sidecar, and the
decoded samples its viewer plays. It is the behaviour of `ClassicMac.Resources.Decoders.Sound`, written so that the
code never has to be read.

Where *Inside Macintosh* is silent, the rules come from the Sound Manager's own code. ClassicMac's MACE decoder, in
particular, comes only from Apple's binaries: Apple never published MACE, and the open decoders that exist (FFmpeg,
LGPL, and resource_dasm's, which is derived from FFmpeg) are behavioural references only. No code or table was taken
from them.

References:

- *Inside Macintosh: Sound* (Apple, 1994), chapter 2 "Sound Manager": sound resources, sound commands, the
  `SoundHeader`, `ExtSoundHeader` and `CmpSoundHeader` records. Constants as in Apple's `Sound.h` (Universal
  Interfaces 3.4).
- Sound Manager 3.5.1 on Mac OS 9.0, disassembly:
  - the Sound Manager's 68k code in the System file (`gpch` 666, a relinked copy of the ROM's): `SndPlay`,
    `GetSoundHeaderOffset`, `SetSoundHeader`, `GetCompressionInfo`, `ParseSndHeader`;
  - SoundLib (`nlib` 666), the PowerPC code a PowerPC Mac runs: `Exp1to3` and `Exp1to6`, the MACE expanders;
  - the `sdec` decompressor components: `ima4` (`thng`/`sift`/`nift` −16589) and the `MAC3`/`MAC6` wrappers
    (−16566, −16567);
  - the NewWorld Mac OS ROM's 68k Sound Manager (3.2) and its MACE code, where the 68k behaviour differs.
- ITU-T Recommendation G.711 (µ-law).
- IMA Digital Audio Focus and Technical Working Groups, *Recommended Practices for Enhancing Digital Audio
  Compatibility in Multimedia Systems*, revision 3.00 (1992): the IMA ADPCM algorithm.
- Microsoft, *Multimedia Programming Interface and Data Specifications 1.0* (1991), and *Multiple Channel Audio Data
  and WAVE Files* (`WAVE_FORMAT_EXTENSIBLE`): the WAV output.

Contents

1. [Conventions](#1-conventions)
2. [The resource](#2-the-resource)
3. [Sound commands](#3-sound-commands)
4. [Finding the sound header](#4-finding-the-sound-header)
5. [Sound headers](#5-sound-headers)
6. [Sample formats](#6-sample-formats)
7. [Loops and base note](#7-loops-and-base-note)
8. [MACE 3:1 and 6:1](#8-mace-31-and-61)
9. [IMA 4:1](#9-ima-41)
10. [µ-law](#10-µ-law)
11. [ClassicMac's output](#11-classicmacs-output)
12. [Diagnostics](#12-diagnostics)
13. [Not covered](#13-not-covered)

---

## 1. Conventions

- The conventions and source tags of [README.md](README.md) apply: big-endian values, `$` hex offsets, and one of
  **[Doc]**, **[Code]**, **[Verified]**, **[Author]**, **[Fitted]** on every rule. A tag with a question mark
  (**[Fitted?]**) marks a rule whose source is not settled.
- In this document:
  - **[Doc]** is *Inside Macintosh: Sound* and Apple's `Sound.h`;
  - **[Code]** is Sound Manager 3.5.1 on Mac OS 9.0 (the System file's 68k Sound Manager, SoundLib, the `sdec`
    components), or the 68k ROM where it says so. Apple's routines were also run in an emulator against the models
    given here, with no mismatch;
  - **[Verified]** means byte-identical to the Sound Manager's own decoding of samples it compressed itself,
    verified in SheepShaver, Mac OS 9.0: MACE 3:1 and 6:1 mono and stereo (from 8-bit sources), MACE 3:1 and 6:1
    mono (from 16-bit sources), IMA 4:1 mono and stereo, µ-law mono;
  - **[Author]** is ITU-T G.711 for µ-law and the IMA recommendation for IMA ADPCM.
- Sentences that begin "ClassicMac …" describe ClassicMac's own choices (its output, its handling of damage). They
  are not format rules and carry a tag only when they rest on a source.
- "The Sound Manager" means Sound Manager 3.5.1 as Mac OS 9.0 runs it on a PowerPC Mac.
- Arithmetic in pseudocode is on 32-bit signed integers. `>>` is an arithmetic (sign-propagating) shift right, so it
  rounds towards minus infinity. `clamp(v, lo, hi)` limits `v` to `lo … hi`. `wrap16(v)` keeps the low 16 bits of `v`
  as a signed value.
- Sound Manager result codes: −205 `badChannel`, −206 `badFormat`, −223 `siInvalidCompression` [Doc].
- A **frame** is one sample per channel. A **packet** is the codec's smallest unit per channel (§5.5).

---

## 2. The resource

A `'snd '` resource is a list of commands for a sound channel, usually a single `bufferCmd` that plays a sampled
sound stored in the same resource. There are two formats, told apart by the first word [Doc].

### 2.1 Format 1

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | `format` = 1 |
| +2 | 2 | u16 | `numSynths` (*n*) |
| +4 | 6*n* | | synthesizer entries (§2.3) |
| +4 + 6*n* | 2 | u16 | `numCommands` (*m*) |
| +6 + 6*n* | 8*m* | | commands (§3.1) |
| +6 + 6*n* + 8*m* | … | | normally the sound header (§5) |

[Doc]. The usual resource has one synthesizer (5, sampled sound) and one command, so its header is at offset 20.

### 2.2 Format 2

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | `format` = 2 |
| +2 | 2 | u16 | `refCount`, for the application's use |
| +4 | 2 | u16 | `numCommands` (*m*) |
| +6 | 8*m* | | commands (§3.1) |
| +6 + 8*m* | … | | the sound header (§5) |

[Doc]. Format 2 was meant for HyperCard and names no synthesizer; the channel must be a sampled-sound channel.

### 2.3 Synthesizer entries

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | synthesizer resource ID: 1 `squareWaveSynth`, 3 `waveTableSynth`, 5 `sampledSynth`; 11 and 13 the old MACE 3 and MACE 6 synthesizers (`MACE3snthID`, `MACE6snthID`) |
| +2 | 4 | u32 | init options for the channel |

[Doc]. Init options (bit masks, combined by OR):

| Value | Name | Meaning |
| --- | --- | --- |
| $0002 | `initChanLeft` | play on the left channel only |
| $0003 | `initChanRight` | play on the right channel only |
| $0004 | `initNoInterp` | no interpolation when changing rate |
| $0008 | `initNoDrop` | no drop-sample conversion |
| $0080 | `initMono` | mono channel |
| $00C0 | `initStereo` | stereo channel (`initStereoMask` $00C0) |
| $0300 | `initMACE3` | MACE 3:1 channel |
| $0400 | `initMACE6` | MACE 6:1 channel |

[Doc]. The Sound Manager's `SetupSndHeader` writes format 1, synthesizer 5, with init options $0380 for MACE 3
mono, $03C0 for MACE 3 stereo, $0480 for MACE 6 mono, and $0080 or $00C0 for IMA 4:1 and µ-law [Verified].

### 2.4 What the Sound Manager uses

- `SndPlay` accepts format 1 and 2 only; any other format fails with −206 [Code].
- Format 1: only the first synthesizer's ID is used. Its init options, and any further synthesizers, do not change
  how the resource is read [Code].
- Format 1 with no synthesizer: the channel gets the note synthesizer, and a `bufferCmd` then fails with −205 [Code].
- Format 2: `refCount` is ignored [Code].
- All commands run in order [Code].
- No other resource type holds sounds: the Sound Manager fetches only `'snd '` (`SysBeep`, falling back to ID 1, and
  `SndStartFilePlay`). There is no `'csnd'` anywhere in the System file or the ROM [Code].

ClassicMac reads formats 1 and 2, keeps the synthesizers or the reference count for the JSON (§11.3), and decodes the
sampled sound whatever the synthesizers say.

---

## 3. Sound commands

### 3.1 The command record

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | `cmd`: the command code in bits 0–14; bit 15 ($8000) is the data-offset flag |
| +2 | 2 | i16 | `param1` |
| +4 | 4 | i32 | `param2` |

[Doc].

### 3.2 The data-offset flag

- With bit 15 set, `param2` is an **offset from the start of the resource** (to a sound header, for `bufferCmd` and
  `soundCmd`) [Doc][Code].
- Without it, `param2` is an absolute memory address [Code], meaningless in a file. ClassicMac ignores such commands
  when looking for a sound.
- A sampled-sound resource stores `bufferCmd` as $8051 and `soundCmd` as $8050 [Doc].

### 3.3 Command codes

[Doc] (`Sound.h`). The names are what ClassicMac writes in the JSON.

| Code | Name | Code | Name |
| --- | --- | --- | --- |
| 0 | `nullCmd` | 45 | `getAmpCmd` |
| 3 | `quietCmd` | 46 | `volumeCmd` |
| 4 | `flushCmd` | 47 | `getVolumeCmd` |
| 5 | `reInitCmd` | 60 | `waveTableCmd` |
| 10 | `waitCmd` | 61 | `phaseCmd` |
| 11 | `pauseCmd` | 80 | `soundCmd` |
| 12 | `resumeCmd` | 81 | `bufferCmd` |
| 13 | `callBackCmd` | 82 | `rateCmd` |
| 14 | `syncCmd` | 83 | `continueCmd` |
| 24 | `availableCmd` | 84 | `doubleBufferCmd` |
| 25 | `versionCmd` | 85 | `getRateCmd` |
| 26 | `totalLoadCmd` | 86 | `rateMultiplierCmd` |
| 27 | `loadCmd` | 87 | `getRateMultiplierCmd` |
| 40 | `freqDurationCmd` | 90 | `sizeCmd` |
| 41 | `restCmd` | 91 | `convertCmd` |
| 42 | `freqCmd` | | |
| 43 | `ampCmd` | | |
| 44 | `timbreCmd` | | |

### 3.4 `bufferCmd` and `soundCmd`

- `bufferCmd` plays the header's frames once, at `sampleRate` × the channel's rate multiplier. The loop points and
  the base note are ignored [Code].
- Undocumented: a `bufferCmd` whose `param1` is −3141 sets the rate from the base note, 2^((60 − `baseFrequency`)/12)
  [Code].
- `soundCmd` only installs the sampled sound as the channel's instrument; the notes come from later commands
  (`freqDurationCmd`, `freqCmd`) [Doc][Code]. Instrument playback is where the loop and base note count (§7).

---

## 4. Finding the sound header

The Sound Manager has two ways of finding a resource's header, and they disagree on format 2 [Code]:

- **`SndPlay`**, format 1: runs the commands; a `bufferCmd` or `soundCmd` with the data-offset flag finds its header
  at `param2` [Doc][Code].
- **`SndPlay`**, format 2: computes the header itself, at **6 + 8 × `numCommands`**, right after the commands, and
  never reads `param2` [Code].
- **`GetSoundHeaderOffset`** (used by `ParseSndHeader` and by `SndStartFilePlay`), both formats: takes the first
  command that is exactly $8050 or $8051 and returns its `param2` unchecked. A $0051 command is missed
  [Code][Verified].

Real files depend on the difference. Realmz's format 2 sounds carry a `bufferCmd` with `param2` = 20, the format 1
position, while their header is at 14. `SndPlay` plays them correctly; `GetSoundHeaderOffset` returns 20 [Verified],
and `ParseSndHeader` then finds an `encode` byte of $6E and fails with −206 [Code].

ClassicMac follows `SndPlay`:

```
players = commands whose cmd has bit 15 set and whose code (cmd & $7FFF) is 80 or 81
if players is empty: no sampled sound (the JSON alone, §11.1)
offset = players[0].param2
if format == 2 and offset >= 0 and offset != 6 + 8 × numCommands:
    offset = 6 + 8 × numCommands                       # diagnostic sound.header-offset
read the header at offset (§5)
```

- With several such commands the first is decoded (diagnostic `sound.several-sounds`); `SndPlay` would play each in
  turn [Code].
- The selection (first flagged command with code 80 or 81) is the same as `GetSoundHeaderOffset`'s [Code]. Whether
  `SndPlay`'s format 2 path needs such a command at all was not traced [Fitted?].

---

## 5. Sound headers

### 5.1 The common fields

Every header starts with these 22 bytes [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | `samplePtr`: 0 when the samples follow the header |
| +$04 | 4 | u32 | standard header: `length`; extended and compressed: `numChannels` |
| +$08 | 4 | UnsignedFixed | `sampleRate`, in hertz |
| +$0C | 4 | u32 | `loopStart`, in frames |
| +$10 | 4 | u32 | `loopEnd`, in frames |
| +$14 | 1 | u8 | `encode`: $00 `stdSH`, $FF `extSH`, $FE `cmpSH` |
| +$15 | 1 | u8 | `baseFrequency`: the MIDI note the samples sound at (60 = middle C) |

- A non-zero `samplePtr` is an absolute address of the samples [Doc][Code]. ClassicMac reads the samples after the
  header anyway (diagnostic `sound.sample-pointer`).
- Any other `encode` fails with −206 [Code].
- Common rates: `rate22khz` $56EE8BA3 (22254.545 Hz, the classic Mac's hardware rate), `rate11khz` $2B7745D1
  (11127.273 Hz), `rate44khz` $AC440000 [Doc].

### 5.2 Standard header (`encode` $00)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 22 | | the common fields (§5.1); +$04 is `length` |
| +$16 | `length` | u8[] | `sampleArea`: the samples |

- Always one channel of 8-bit offset binary (`'raw '`, $80 = silence) [Doc][Code].
- `length` is the number of frames, which is also the number of bytes [Doc][Code].

### 5.3 Extended header (`encode` $FF)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 22 | | the common fields (§5.1); +$04 is `numChannels` |
| +$16 | 4 | u32 | `numFrames` |
| +$1A | 10 | extended80 | `AIFFSampleRate` (not read) |
| +$24 | 4 | u32 | `markerChunk` (not read) |
| +$28 | 4 | u32 | `instrumentChunks` (not read) |
| +$2C | 4 | u32 | `AESRecording` (not read) |
| +$30 | 2 | u16 | `sampleSize`: bits per sample |
| +$32 | 2 | u16 | `futureUse1` |
| +$34 | 4 | u32 | `futureUse2` |
| +$38 | 4 | u32 | `futureUse3` |
| +$3C | 4 | u32 | `futureUse4` |
| +$40 | … | | `sampleArea`: the samples |

Layout [Doc]; which fields are read [Code].

- `numChannels` is documented as a long, but the Sound Manager reads **the word at +6** [Code]. ClassicMac does the
  same.
- More than 2 channels fails with −206 [Code]. ClassicMac accepts 1 to 64 and refuses 0 or more
  (`sound.bad-header`).
- `SetSoundHeader` raises a `sampleSize` below 8 to 8; `ParseSndHeader` does not [Code]. ClassicMac accepts 8, 16, 24
  and 32 and refuses any other size (`sound.bad-header`).
- 8-bit samples are offset binary (`'raw '`), larger ones big-endian two's complement (`'twos'`) [Code].
- Channels are interleaved by frame; a frame is `numChannels` × `sampleSize`/8 bytes [Doc][Code].
- The samples take `numFrames` × `numChannels` × `sampleSize`/8 bytes.

### 5.4 Compressed header (`encode` $FE)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 22 | | the common fields (§5.1); +$04 is `numChannels` |
| +$16 | 4 | u32 | `numFrames`: **packets** per channel (§5.5) |
| +$1A | 10 | extended80 | `AIFFSampleRate` (not read) |
| +$24 | 4 | u32 | `markerChunk` (not read) |
| +$28 | 4 | OSType | `format`: the sample format (§6) |
| +$2C | 4 | u32 | `futureUse2` |
| +$30 | 4 | u32 | `stateVars` (not read) |
| +$34 | 4 | u32 | `leftOverSamples` (not read) |
| +$38 | 2 | i16 | `compressionID` (§5.5) |
| +$3A | 2 | u16 | `packetSize` (not read) |
| +$3C | 2 | u16 | `snthID` (not read) |
| +$3E | 2 | u16 | `sampleSize`: bits per sample of the (decompressed, for PCM: stored) samples |
| +$40 | … | | `sampleArea`: the samples |

Layout [Doc]. Beyond the common fields the Sound Manager reads only `numFrames`, `format`, `compressionID` and
`sampleSize`; `packetSize`, `snthID`, `stateVars` and `leftOverSamples` are never used [Code]. `numChannels` is the
word at +6, as in §5.3 [Code].

Note that `sampleSize` sits at +$3E here but at +$30 in an extended header [Doc]; reading one at the other's place
is a common mistake.

### 5.5 `compressionID`, `format` and packets

`GetCompressionInfo` picks the sample format from `compressionID` [Code] (names [Doc]):

| `compressionID` | Name | Sound Manager 3.5.1 |
| --- | --- | --- |
| 0 | `notCompressed` | PCM, **whatever `format` says**: `sampleSize` 8 → `'raw '`, otherwise `'twos'` |
| −1 | `fixedCompression` | use `format` |
| −2 | `variableCompression` | use `format` |
| 1 | `twoToOne` | refused, −223 |
| 2 | `eightToThree` | refused, −223 |
| 3 | `threeToOne` | `'MAC3'` |
| 4 | `sixToOne` | `'MAC6'` |
| any other | | refused, −223 |

- `'raw '`, `'twos'`, `'MAC3'` and `'MAC6'` are built into the Sound Manager. Any other `format` is looked up as an
  `sdec` component (its `GetInfo` selector `'cmfa'`); if there is none, −223 [Code].
- **`numFrames` counts packets**: the sample count is `numFrames` × samples per packet [Code][Verified].
  `SetupSndHeader` writes 7424 for 44 544 samples of MACE (44 544 / 6), 696 for IMA 4:1 (44 544 / 64) and 44 544 for
  µ-law [Verified].

| Format | Samples per packet | Bytes per packet per channel | Output of the Sound Manager's decoder |
| --- | --- | --- | --- |
| `'raw '`, `'twos'` | 1 | `sampleSize`/8 | as stored |
| `'MAC3'` | 6 | 2 | 8-bit offset binary |
| `'MAC6'` | 6 | 1 | 8-bit offset binary |
| `'ima4'` | 64 | 34 | 16-bit `'twos'` (or 8-bit on request) |
| `'ulaw'` | 1 | 1 | 16-bit `'twos'` |

MACE and IMA 4:1 [Code]; µ-law's counts [Verified].

ClassicMac reads `compressionID` as the table says (an ID it refuses gives `sound.bad-header`), then takes the
samples:

```
PCM format (§6):   numFrames × numChannels × bytesPerSample bytes
MAC3, MAC6, ima4, ulaw:  numFrames × numChannels × bytesPerPacket bytes
any other format:  everything from +$40 to the end of the resource (not decoded; sound.codec)
```

Where the resource holds fewer bytes than that, the samples are cut to what it holds (`sound.short`).

---

## 6. Sample formats

| `format` | Meaning | Source |
| --- | --- | --- |
| `'raw '` | 8-bit offset binary: $80 is silence, $00 the most negative value | [Doc][Code] |
| `'twos'` | two's complement, big-endian, 8 or 16 bits (24 and 32 in an extended header, §5.3) | [Doc][Code] |
| `'sowt'` | 16-bit two's complement, little-endian (an `sdec` component in Mac OS 9.0) | [Code] |
| `'in24'` | 24-bit two's complement, big-endian | [Fitted?] |
| `'in32'` | 32-bit two's complement, big-endian | [Fitted?] |
| `'fl32'` | 32-bit IEEE 754 float, big-endian, ±1.0 full scale | [Fitted?] |
| `'fl64'` | 64-bit IEEE 754 float, big-endian | [Fitted?] |
| `'MAC3'` | MACE 3:1 (§8) | [Code] |
| `'MAC6'` | MACE 6:1 (§8) | [Code] |
| `'ima4'` | IMA 4:1 ADPCM (§9) | [Code] |
| `'ulaw'` | µ-law (§10) | [Code] |

- Multi-channel samples are interleaved by frame (by packet, for the codecs) [Doc][Code].
- Stock Mac OS 9.0 plays `'raw '`, `'twos'`, `'sowt'`, `'ulaw'`, `'ima4'`, `'MAC3'` and `'MAC6'` [Code]. `'in24'`,
  `'in32'`, `'fl32'`, `'fl64'` and `'alaw'` are not in the System file; they come with QuickTime [Code]. ClassicMac
  reads the first four as the table says; `'alaw'` and any other codec are not decoded (`sound.codec`).
- ClassicMac takes `'raw '`, `'twos'` and `'sowt'` with a `sampleSize` of 16, 24 or 32 as that many bits, and any
  other size as 8 bits; a `'raw '` sample wider than 8 bits is read as big-endian offset binary [Fitted?].
- A trailing partial frame is dropped.

---

## 7. Loops and base note

The loop points and the base note are used **only for instrument playback** (`soundCmd` followed by note commands),
never by `bufferCmd` [Code]:

- A note plays at 2^((note − `baseFrequency`)/12) × the recorded rate [Code].
- A loop exists only if `loopEnd` > `loopStart` and `loopEnd` − `loopStart` > 2 [Code].
- With a loop, the attack plays frames [0, `loopEnd`), the sustain repeats [`loopStart`, `loopEnd`) while the note
  lasts, and the release plays from `loopEnd` + 1 for `numFrames` − `loopEnd` frames [Code].
- Loop points count frames [Doc].

ClassicMac keeps them as information in the WAV's `smpl` chunk (§11.2) and the JSON. It takes a `baseFrequency` of 0
as 60 [Fitted?].

---

## 8. MACE 3:1 and 6:1

MACE (Macintosh Audio Compression and Expansion) compresses 8-bit sound 3:1 or 6:1 [Doc]. Apple documented the
routines (`Comp3to1`, `Exp1to3`, `Comp6to1`, `Exp1to6`) but not the algorithm; everything below is from the code.

- The `sdec` components `'MAC3'` and `'MAC6'` are wrappers: they call the Sound Manager's `Exp1to3` and `Exp1to6`
  through `_SoundDispatch` [Code].
- On a PowerPC Mac those are SoundLib's native routines, which this section describes. The 68k ROM (and the copy in
  `gpch` 666) holds 68k versions: identical for 3:1 but for one saturation corner, slightly different in 6:1 rounding
  (§8.6) [Code].
- The tables are byte-identical in SoundLib, `gpch` 666 and the ROM [Code].

### 8.1 Packets, channels and state

- **MACE 3:1**: 2 bytes per channel → 6 samples. **MACE 6:1**: 1 byte per channel → 6 samples [Doc][Code].
- Channels alternate by packet: MACE 3 stores L L R R L L R R …, MACE 6 stores L R L R … [Code][Verified].
- Each channel has its own state; all of it starts at zero [Code].
- A trailing partial packet (or partial set of packets across the channels) is dropped: the component decodes
  ⌊samples/6⌋ packets [Code].
- The output is 6 × `numFrames` frames, channels interleaved by frame.

### 8.2 Levels and table rows

Each code, 3 or 2 bits, updates the channel's `level` and picks a delta from a table row chosen by the level
**before** the update [Code]:

```
row(adjust, code):
    old   = level
    level = old + adjust[code] - (old >> 5)
    if level < 0: level = 0
    return (old >> 4) & $7F
```

- 3-bit codes use the adjustment table `T3` and the delta table `T3D` (128 rows × 8 columns); 2-bit codes use `T2`
  and `T2D` (128 rows × 4 columns) (§8.8) [Code].
- `level` is a 32-bit value on PowerPC and is never limited from above; the row wraps through the `& $7F` mask
  [Code].

### 8.3 MACE 3:1

State per channel: `level`, `prev`. Within each byte the fields are taken **lowest bits first** [Code]:

```
for each packet (2 bytes) of the channel:
    for each byte b of the packet:
        code3(b & 7,        T3, T3D)
        code3((b >> 3) & 3, T2, T2D)
        code3(b >> 5,       T3, T3D)

code3(code, adjust, deltas):
    d    = deltas[row(adjust, code)][code]
    v    = clamp(d + prev, -32767, 32767)
    prev = v - (v >> 3)                        # the value decays by 1/8
    emit byte(v)

byte(v) = ((v >> 8) & $FF) XOR $80             # 8-bit offset binary
```

[Code][Verified].

### 8.4 MACE 6:1

State per channel: `level`, `pred`, `fac`, `last`, `A` (the older value), `B` (the newer). Within each byte the
fields are taken **highest bits first**, and each code gives two samples [Code]:

```
for each byte b of the channel:
    code6(b >> 5,       T3, T3D)
    code6((b >> 3) & 3, T2, T2D)
    code6(b & 7,        T3, T3D)

code6(code, adjust, deltas):
    d = deltas[row(adjust, code)][code]
    v = clamp(d + pred, -32767, 32767)
    if (d & $8000) == (last & $8000):  fac = min(fac + 506, 32767)
    else:                              fac = max(fac - 314, -32767)
    last = v
    pred = (v * fac) >> 15
    emit byte(clamp(((3 * A) >> 3) + (B >> 1) + (v >> 3), -32767, 32767))
    emit byte(clamp((B >> 1) + (A >> 3) + ((3 * v) >> 3), -32767, 32767))
    A = B
    B = v
```

[Code][Verified]. `d` and `last` both lie within 16 bits, so `& $8000` compares their signs. The two samples
interpolate between the two previous values and the new one, so the output lags the input by about 4 samples [Code].

### 8.5 Output

- Both expanders give **8-bit offset binary** samples [Doc][Code][Verified].
- Asked for 16-bit output, the Sound Manager replicates the byte: `s16 = ((b XOR $80) << 8) | (b XOR $80)`
  [Verified]. No extra precision exists.

ClassicMac writes MACE as 8-bit.

### 8.6 The 68k ROM's expanders

The 68k versions work in 16-bit words [Code]:

- **3:1**: the same as §8.3, except that the sum `d + prev` is replaced by ±32767 only when it overflows 16 bits: a
  sum of exactly −32768 is kept, where PowerPC gives −32767. The byte is the same; the next `prev` differs by 1.
- **6:1**: different rounding and no output clamp. The state keeps halves, `p` = older >> 1 and `q` = newer >> 1:

```
code6_68k(code, adjust, deltas):
    old   = level
    level = wrap16(old + adjust[code] - (old >> 5));  if level < 0: level = 0
    d = deltas[(old >> 4) & $7F][code]
    s = d + pred;  v = (s > 32767) ? 32767 : (s < -32768) ? -32767 : s
    if ((d XOR last) & $8000) == 0:  fac = min(fac + 506, 32767)
    else:  f = fac - 314;  fac = (f < -32768) ? -32767 : f
    pred = wrap16(((v * fac * 2) & $FFFFFFFF) >> 16)
    last = v >> 1
    h = v >> 1
    e = wrap16(p - h) >> 2
    emit byte(wrap16(p + q - e))
    emit byte(wrap16(e + h + q))
    p = q
    q = h
```

The two 6:1 expanders differ on about 0.25 % of output bytes. The Sound Manager on a PowerPC Mac gives the PowerPC
result: the 68k model differs from its output on 119 to 267 bytes of each MACE 6 sample, the PowerPC model on none
[Verified]. ClassicMac follows PowerPC.

### 8.7 Adjustment tables

Signed 16-bit, indexed by the code [Code]:

```
T3 = [ -13,   8,  76, 222, 222,  76,   8, -13 ]      3-bit codes
T2 = [ -18, 140, 140, -18 ]                          2-bit codes
```

### 8.8 Delta tables

Signed 16-bit; the row is `(old level >> 4) & $7F`, the column is the code [Code]. Apple's numbers, as they stand in
SoundLib, `gpch` 666 and the ROM.

`T3D` (3-bit codes):

```
row         c0      c1      c2      c3      c4      c5      c6      c7
  0         37     116     206     330    -331    -207    -117     -38
  1         39     121     216     346    -347    -217    -122     -40
  2         41     127     225     361    -362    -226    -128     -42
  3         42     132     235     377    -378    -236    -133     -43
  4         44     137     245     392    -393    -246    -138     -45
  5         46     144     256     410    -411    -257    -145     -47
  6         48     150     267     428    -429    -268    -151     -49
  7         51     157     280     449    -450    -281    -158     -52
  8         53     165     293     470    -471    -294    -166     -54
  9         55     172     306     490    -491    -307    -173     -56
 10         58     179     319     511    -512    -320    -180     -59
 11         60     187     333     534    -535    -334    -188     -61
 12         63     195     348     557    -558    -349    -196     -64
 13         66     205     364     583    -584    -365    -206     -67
 14         69     214     380     609    -610    -381    -215     -70
 15         72     223     396     635    -636    -397    -224     -73
 16         75     233     414     663    -664    -415    -234     -76
 17         79     244     433     694    -695    -434    -245     -80
 18         82     254     453     725    -726    -454    -255     -83
 19         86     265     472     756    -757    -473    -266     -87
 20         90     278     495     792    -793    -496    -279     -91
 21         94     290     516     826    -827    -517    -291     -95
 22         98     303     538     862    -863    -539    -304     -99
 23        102     316     562     901    -902    -563    -317    -103
 24        107     331     588     942    -943    -589    -332    -108
 25        112     345     614     983    -984    -615    -346    -113
 26        117     361     641    1027   -1028    -642    -362    -118
 27        122     377     670    1074   -1075    -671    -378    -123
 28        127     394     701    1123   -1124    -702    -395    -128
 29        133     411     732    1172   -1173    -733    -412    -134
 30        139     430     764    1224   -1225    -765    -431    -140
 31        145     449     799    1280   -1281    -800    -450    -146
 32        152     469     835    1337   -1338    -836    -470    -153
 33        159     490     872    1397   -1398    -873    -491    -160
 34        166     512     911    1459   -1460    -912    -513    -167
 35        173     535     951    1523   -1524    -952    -536    -174
 36        181     558     993    1590   -1591    -994    -559    -182
 37        189     584    1038    1663   -1664   -1039    -585    -190
 38        197     610    1085    1738   -1739   -1086    -611    -198
 39        206     637    1133    1815   -1816   -1134    -638    -207
 40        215     665    1183    1895   -1896   -1184    -666    -216
 41        225     695    1237    1980   -1981   -1238    -696    -226
 42        235     726    1291    2068   -2069   -1292    -727    -236
 43        246     759    1349    2161   -2162   -1350    -760    -247
 44        257     792    1409    2257   -2258   -1410    -793    -258
 45        268     828    1472    2357   -2358   -1473    -829    -269
 46        280     865    1538    2463   -2464   -1539    -866    -281
 47        293     903    1606    2572   -2573   -1607    -904    -294
 48        306     944    1678    2688   -2689   -1679    -945    -307
 49        319     986    1753    2807   -2808   -1754    -987    -320
 50        334    1030    1832    2933   -2934   -1833   -1031    -335
 51        349    1076    1914    3065   -3066   -1915   -1077    -350
 52        364    1124    1999    3202   -3203   -2000   -1125    -365
 53        380    1174    2088    3344   -3345   -2089   -1175    -381
 54        398    1227    2182    3494   -3495   -2183   -1228    -399
 55        415    1281    2278    3649   -3650   -2279   -1282    -416
 56        434    1339    2380    3811   -3812   -2381   -1340    -435
 57        453    1398    2486    3982   -3983   -2487   -1399    -454
 58        473    1461    2598    4160   -4161   -2599   -1462    -474
 59        495    1526    2714    4346   -4347   -2715   -1527    -496
 60        517    1594    2835    4540   -4541   -2836   -1595    -518
 61        540    1665    2961    4741   -4742   -2962   -1666    -541
 62        564    1740    3093    4953   -4954   -3094   -1741    -565
 63        589    1818    3232    5175   -5176   -3233   -1819    -590
 64        615    1898    3375    5405   -5406   -3376   -1899    -616
 65        643    1984    3527    5647   -5648   -3528   -1985    -644
 66        671    2072    3683    5898   -5899   -3684   -2073    -672
 67        701    2164    3848    6161   -6162   -3849   -2165    -702
 68        733    2261    4020    6438   -6439   -4021   -2262    -734
 69        766    2362    4199    6724   -6725   -4200   -2363    -767
 70        800    2467    4386    7024   -7025   -4387   -2468    -801
 71        836    2578    4583    7339   -7340   -4584   -2579    -837
 72        873    2692    4786    7664   -7665   -4787   -2693    -874
 73        912    2813    5001    8008   -8009   -5002   -2814    -913
 74        952    2938    5223    8364   -8365   -5224   -2939    -953
 75        995    3070    5457    8739   -8740   -5458   -3071    -996
 76       1039    3207    5701    9129   -9130   -5702   -3208   -1040
 77       1086    3350    5956    9537   -9538   -5957   -3351   -1087
 78       1134    3499    6220    9960   -9961   -6221   -3500   -1135
 79       1185    3655    6497   10404  -10405   -6498   -3656   -1186
 80       1238    3818    6788   10869  -10870   -6789   -3819   -1239
 81       1293    3989    7091   11355  -11356   -7092   -3990   -1294
 82       1351    4166    7407   11861  -11862   -7408   -4167   -1352
 83       1411    4352    7738   12390  -12391   -7739   -4353   -1412
 84       1474    4547    8084   12946  -12947   -8085   -4548   -1475
 85       1540    4750    8444   13522  -13523   -8445   -4751   -1541
 86       1609    4962    8821   14126  -14127   -8822   -4963   -1610
 87       1680    5183    9215   14756  -14757   -9216   -5184   -1681
 88       1756    5415    9626   15415  -15416   -9627   -5416   -1757
 89       1834    5657   10057   16104  -16105  -10058   -5658   -1835
 90       1916    5909   10505   16822  -16823  -10506   -5910   -1917
 91       2001    6173   10975   17574  -17575  -10976   -6174   -2002
 92       2091    6448   11463   18356  -18357  -11464   -6449   -2092
 93       2184    6736   11974   19175  -19176  -11975   -6737   -2185
 94       2282    7037   12510   20032  -20033  -12511   -7038   -2283
 95       2383    7351   13068   20926  -20927  -13069   -7352   -2384
 96       2490    7679   13652   21861  -21862  -13653   -7680   -2491
 97       2601    8021   14260   22834  -22835  -14261   -8022   -2602
 98       2717    8380   14897   23854  -23855  -14898   -8381   -2718
 99       2838    8753   15561   24918  -24919  -15562   -8754   -2839
100       2965    9144   16256   26031  -26032  -16257   -9145   -2966
101       3097    9553   16982   27193  -27194  -16983   -9554   -3098
102       3236    9979   17740   28407  -28408  -17741   -9980   -3237
103       3380   10424   18532   29675  -29676  -18533  -10425   -3381
104       3531   10890   19359   31000  -31001  -19360  -10891   -3532
105       3688   11375   20222   32382  -32383  -20223  -11376   -3689
106       3853   11883   21125   32767  -32768  -21126  -11884   -3854
107       4025   12414   22069   32767  -32768  -22070  -12415   -4026
108       4205   12967   23053   32767  -32768  -23054  -12968   -4206
109       4392   13546   24082   32767  -32768  -24083  -13547   -4393
110       4589   14151   25157   32767  -32768  -25158  -14152   -4590
111       4793   14783   26280   32767  -32768  -26281  -14784   -4794
112       5007   15442   27452   32767  -32768  -27453  -15443   -5008
113       5231   16132   28678   32767  -32768  -28679  -16133   -5232
114       5464   16851   29957   32767  -32768  -29958  -16852   -5465
115       5708   17603   31294   32767  -32768  -31295  -17604   -5709
116       5963   18389   32691   32767  -32768  -32692  -18390   -5964
117       6229   19210   32767   32767  -32768  -32768  -19211   -6230
118       6507   20067   32767   32767  -32768  -32768  -20068   -6508
119       6797   20963   32767   32767  -32768  -32768  -20964   -6798
120       7101   21899   32767   32767  -32768  -32768  -21900   -7102
121       7418   22876   32767   32767  -32768  -32768  -22877   -7419
122       7749   23897   32767   32767  -32768  -32768  -23898   -7750
123       8095   24964   32767   32767  -32768  -32768  -24965   -8096
124       8456   26078   32767   32767  -32768  -32768  -26079   -8457
125       8833   27242   32767   32767  -32768  -32768  -27243   -8834
126       9228   28457   32767   32767  -32768  -32768  -28458   -9229
127       9639   29727   32767   32767  -32768  -32768  -29728   -9640
```

`T2D` (2-bit codes):

```
row         c0      c1      c2      c3
  0         64     216    -217     -65
  1         67     226    -227     -68
  2         70     236    -237     -71
  3         74     246    -247     -75
  4         77     257    -258     -78
  5         80     268    -269     -81
  6         84     280    -281     -85
  7         88     294    -295     -89
  8         92     307    -308     -93
  9         96     321    -322     -97
 10        100     334    -335    -101
 11        104     350    -351    -105
 12        109     365    -366    -110
 13        114     382    -383    -115
 14        119     399    -400    -120
 15        124     416    -417    -125
 16        130     434    -435    -131
 17        136     454    -455    -137
 18        142     475    -476    -143
 19        148     495    -496    -149
 20        155     519    -520    -156
 21        162     541    -542    -163
 22        169     564    -565    -170
 23        176     590    -591    -177
 24        185     617    -618    -186
 25        193     644    -645    -194
 26        201     673    -674    -202
 27        210     703    -704    -211
 28        220     735    -736    -221
 29        230     767    -768    -231
 30        240     801    -802    -241
 31        251     838    -839    -252
 32        262     876    -877    -263
 33        274     914    -915    -275
 34        286     955    -956    -287
 35        299     997    -998    -300
 36        312    1041   -1042    -313
 37        326    1089   -1090    -327
 38        341    1138   -1139    -342
 39        356    1188   -1189    -357
 40        372    1241   -1242    -373
 41        388    1297   -1298    -389
 42        406    1354   -1355    -407
 43        424    1415   -1416    -425
 44        443    1478   -1479    -444
 45        462    1544   -1545    -463
 46        483    1613   -1614    -484
 47        505    1684   -1685    -506
 48        527    1760   -1761    -528
 49        551    1838   -1839    -552
 50        576    1921   -1922    -577
 51        601    2007   -2008    -602
 52        628    2097   -2098    -629
 53        656    2190   -2191    -657
 54        686    2288   -2289    -687
 55        716    2389   -2390    -717
 56        748    2496   -2497    -749
 57        781    2607   -2608    -782
 58        816    2724   -2725    -817
 59        853    2846   -2847    -854
 60        891    2973   -2974    -892
 61        930    3104   -3105    -931
 62        972    3243   -3244    -973
 63       1016    3389   -3390   -1017
 64       1061    3539   -3540   -1062
 65       1108    3698   -3699   -1109
 66       1158    3862   -3863   -1159
 67       1209    4035   -4036   -1210
 68       1264    4216   -4217   -1265
 69       1320    4403   -4404   -1321
 70       1379    4599   -4600   -1380
 71       1441    4806   -4807   -1442
 72       1505    5019   -5020   -1506
 73       1572    5244   -5245   -1573
 74       1642    5477   -5478   -1643
 75       1715    5722   -5723   -1716
 76       1792    5978   -5979   -1793
 77       1872    6245   -6246   -1873
 78       1955    6522   -6523   -1956
 79       2043    6813   -6814   -2044
 80       2134    7118   -7119   -2135
 81       2229    7436   -7437   -2230
 82       2329    7767   -7768   -2330
 83       2432    8114   -8115   -2433
 84       2541    8477   -8478   -2542
 85       2655    8854   -8855   -2656
 86       2773    9250   -9251   -2774
 87       2897    9663   -9664   -2898
 88       3026   10094  -10095   -3027
 89       3162   10546  -10547   -3163
 90       3303   11016  -11017   -3304
 91       3450   11508  -11509   -3451
 92       3604   12020  -12021   -3605
 93       3765   12556  -12557   -3766
 94       3933   13118  -13119   -3934
 95       4108   13703  -13704   -4109
 96       4292   14315  -14316   -4293
 97       4483   14953  -14954   -4484
 98       4683   15621  -15622   -4684
 99       4892   16318  -16319   -4893
100       5111   17046  -17047   -5112
101       5339   17807  -17808   -5340
102       5577   18602  -18603   -5578
103       5826   19433  -19434   -5827
104       6086   20300  -20301   -6087
105       6358   21205  -21206   -6359
106       6642   22152  -22153   -6643
107       6938   23141  -23142   -6939
108       7248   24173  -24174   -7249
109       7571   25252  -25253   -7572
110       7909   26380  -26381   -7910
111       8262   27557  -27558   -8263
112       8631   28786  -28787   -8632
113       9016   30072  -30073   -9017
114       9419   31413  -31414   -9420
115       9839   32767  -32768   -9840
116      10278   32767  -32768  -10279
117      10737   32767  -32768  -10738
118      11216   32767  -32768  -11217
119      11717   32767  -32768  -11718
120      12240   32767  -32768  -12241
121      12786   32767  -32768  -12787
122      13356   32767  -32768  -13357
123      13953   32767  -32768  -13954
124      14576   32767  -32768  -14577
125      15226   32767  -32768  -15227
126      15906   32767  -32768  -15907
127      16615   32767  -32768  -16616
```

---

## 9. IMA 4:1

`'ima4'` is IMA ADPCM [Author] in Apple's packet layout, decoded by the `ima4` `sdec` component (68k `sift` −16589,
PowerPC `nift` −16589); the two agree everywhere but on invalid step indexes (§9.3) [Code].

### 9.1 Packets

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | preamble: the predictor in the upper 9 bits, the step index in the low 7 |
| +2 | 32 | u8[] | 64 samples, 4 bits each, **low nibble first** |

[Code][Verified].

- A packet is 34 bytes per channel; channels alternate by packet, L R L R …, each with its own state [Code][Verified].
  The component handles 1 or 2 channels only [Code].
- Preamble: predictor = sign-extend16(word & $FF80) (the low 7 bits zero); step index = word & $7F, not clamped
  [Code].
- `numFrames` counts packets; the sound has 64 × `numFrames` frames [Code][Verified].
- A trailing partial packet is never decoded [Code].

### 9.2 When the preamble is read

The component decodes in batches of min(remaining packets, maxFrames / 64), and `maxFrames` is at most 1024, so a
batch is **16 packets**. It looks at a preamble only for the first packet of each batch, and uses it only when it
differs from the running state [Code][Verified]:

```
per channel: pred = 0, index = 0
for packet p = 0, 1, … of the channel:
    if p mod 16 == 0:
        pp = sign-extend16(preamble & $FF80);  pi = preamble & $7F
        if not (pi == index and |pp - pred| <= 127):
            pred = pp
            index = pi
    # the preambles of the other 15 packets are skipped unread
    for each of the 32 data bytes b:
        nibble(b & $0F)
        nibble(b >> 4)
```

- The running state starts at 0 when the component opens and is reset by `StopSource` [Code].
- On streams from Apple's own compressor this gives the same result as reading every preamble; on spliced data, a
  decoder that reads every preamble differs [Code].

### 9.3 Nibbles

```
nibble(n):
    step = STEP[index]
    d = step >> 3
    if n & 4: d += step
    if n & 2: d += step >> 1
    if n & 1: d += step >> 2
    if n & 8: d = -d
    pred  = clamp(pred + d, -32768, 32767)
    index = clamp(index + INDEX[n], 0, 88)
    emit pred
```

- The difference is built by shifts that truncate, as the IMA recommendation gives it [Author][Code]. The 68k code
  uses a precomputed 89 × 16 table of these values; a multiplying form would differ in 404 of its 1424 entries
  [Code].
- The predictor is clamped before the index is updated [Code].
- A preamble index above 88 is used once, for the next nibble, before it is clamped; Apple's code then reads past its
  table, into memory that differs between 68k and PowerPC [Code]. ClassicMac uses `STEP[88]` for that nibble.
- Output: 16-bit signed samples [Code][Verified]. Asked for 8-bit output, the component gives
  `((pred >> 8) & $FF) XOR $80` [Code]. ClassicMac writes 16-bit.

Tables [Author][Code] (Apple's are the standard ones):

```
STEP = [     7,     8,     9,    10,    11,    12,    13,    14,    16,    17,
            19,    21,    23,    25,    28,    31,    34,    37,    41,    45,
            50,    55,    60,    66,    73,    80,    88,    97,   107,   118,
           130,   143,   157,   173,   190,   209,   230,   253,   279,   307,
           337,   371,   408,   449,   494,   544,   598,   658,   724,   796,
           876,   963,  1060,  1166,  1282,  1411,  1552,  1707,  1878,  2066,
          2272,  2499,  2749,  3024,  3327,  3660,  4026,  4428,  4871,  5358,
          5894,  6484,  7132,  7845,  8630,  9493, 10442, 11487, 12635, 13899,
         15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 ]        indexes 0–88

INDEX = [ -1, -1, -1, -1, 2, 4, 6, 8,  -1, -1, -1, -1, 2, 4, 6, 8 ]           nibbles 0–15
```

---

## 10. µ-law

`'ulaw'` is G.711 µ-law [Author], one byte per sample, decoded by an `sdec` component to 16-bit [Code]:

```
u = (NOT code) & $FF
t = (((u & $0F) << 3) + $84) << ((u >> 4) & 7)
sample = (u & $80) ? ($84 - t) : (t - $84)
```

[Author][Verified]. The samples range over ±32 124; $FF and $7F give 0.

- `numFrames` counts samples [Verified].
- Channels interleave by sample [Fitted?] (only mono was verified).

---

## 11. ClassicMac's output

### 11.1 Files

| Resource | Output |
| --- | --- |
| A sampled sound ClassicMac decodes | `.wav` (§11.2) and `.json` (§11.3) |
| Commands only (no flagged `bufferCmd`/`soundCmd`), or a header that cannot be read | `.json` alone |
| A sampled sound in a format not decoded | nothing from the decoder (`sound.codec`); the exporter writes the resource's data as `.bin` |
| Not a `'snd '` (format neither 1 nor 2, or too short for its lists) | nothing from the decoder; exported as `.bin` |

### 11.2 WAV

A RIFF WAVE file [Doc] (Microsoft's specifications): `RIFF` size `WAVE`, then the chunks `fmt `, `fact` (float only),
`smpl` (when needed) and `data`, in that order. All values little-endian.

Samples:

| Source | WAV samples |
| --- | --- |
| 8-bit `'raw '` | copied (WAV's 8-bit samples are offset binary too) |
| 8-bit `'twos'`, `'sowt'` | each byte XOR $80 |
| 16/24/32-bit `'twos'`, `'in24'`, `'in32'` | byte order reversed |
| 16-bit `'sowt'` | copied |
| 16/24/32-bit `'raw '` | byte order reversed, then the sign bit flipped |
| `'fl32'`, `'fl64'` | byte order reversed; IEEE float |
| MACE | 8-bit, as the Sound Manager gives it (§8.5) |
| IMA 4:1, µ-law | 16-bit |

The rate is the header's rate **rounded to whole hertz** (at least 1): 22254.545 Hz becomes 22255. The exact rate
is in the JSON.

`fmt ` chunk, 16 bytes, or 40 when the samples are float, wider than 16 bits, or in more than 2 channels:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 2 | u16 | format tag: 1 (PCM), or $FFFE (`WAVE_FORMAT_EXTENSIBLE`) |
| +2 | 2 | u16 | channels |
| +4 | 4 | u32 | rate (rounded) |
| +8 | 4 | u32 | bytes per second: rate × block align |
| +12 | 2 | u16 | block align: channels × bytes per sample |
| +14 | 2 | u16 | bits per sample: 8 × bytes per sample |
| +16 | 2 | u16 | extensible only: extra size = 22 |
| +18 | 2 | u16 | valid bits per sample = bits per sample |
| +20 | 4 | u32 | channel mask = 0 (no speaker positions) |
| +24 | 16 | GUID | subformat: 1 (PCM) or 3 (IEEE float), followed by `00 00 00 00 10 00 80 00 00 AA 00 38 9B 71` |

`fact` chunk (float only): 4 bytes, the number of frames.

`smpl` chunk, written when the loop lies inside the sound (a loop as the Sound Manager counts one, §7, and `loopEnd`
≤ the decoded frame count) or the base note is not 60 (a base note of 0 counts as 60):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +0 | 4 | u32 | manufacturer = 0 |
| +4 | 4 | u32 | product = 0 |
| +8 | 4 | u32 | sample period in nanoseconds: round(10⁹ / rounded rate) |
| +12 | 4 | u32 | MIDI unity note: the base note |
| +16 | 4 | u32 | pitch fraction = 0 |
| +20 | 4 | u32 | SMPTE format = 0 |
| +24 | 4 | u32 | SMPTE offset = 0 |
| +28 | 4 | u32 | number of loops: 1 or 0 |
| +32 | 4 | u32 | sampler data = 0 |
| +36 | 24 | | the loop, when there is one: cue point ID 0, type 0 (forward), start = `loopStart`, end = `loopEnd` − 1 (WAV's end is inclusive), fraction 0, play count 0 (for ever) |

`data` chunk: the samples, followed by a pad byte when their length is odd.

### 11.3 JSON sidecar

What WAV cannot hold. All numbers are JSON numbers; `$`-prefixed strings are 8-digit hex.

| Field | Meaning |
| --- | --- |
| `format` | 1 or 2 |
| `synthesizers` | format 1 only: `[{ "id": …, "initOptions": "$000000C0" }, …]` |
| `referenceCount` | format 2 only |
| `commands` | every command, in order: `command` (the code without the flag), `name` (§3.3, when known), `dataOffset` (`true`, present only when the flag is set), `param1`, `param2` |
| `sound` | present when a sampled sound was found: |
| `sound.header` | `"standard"`, `"extended"` or `"compressed"` |
| `sound.headerOffset` | where the header was read (for format 2, the offset §4 computes) |
| `sound.sampleRate` | the exact rate in hertz, rounded to 6 decimals |
| `sound.sampleRateFixed` | the rate as stored, `$56EE8BA3` |
| `sound.channels` | channels |
| `sound.sampleSize` | bits per sample of the decoded sound (8 for MACE, 16 for IMA 4:1 and µ-law) |
| `sound.sampleFormat` | the format after `compressionID` is applied (§5.5): `raw `, `twos`, `MAC3`, … |
| `sound.compressionId`, `sound.packetSize` | compressed headers only, as stored |
| `sound.frames` | `numFrames` or `length` as stored: packets per channel for the codecs |
| `sound.loopStart`, `sound.loopEnd`, `sound.baseNote` | as stored |

### 11.4 Decoded samples in the viewer

The desktop app decodes a sound to a `DecodedSound`: the samples as 32-bit floats in [−1, 1], channels interleaved,
with the channel count and the **exact** rate (not rounded). The conversion from the WAV samples above:

| Width | Float |
| --- | --- |
| 8-bit | (b − 128) / 128 |
| 16-bit | s / 32768 |
| 24-bit | (s << 8) / 2³¹ |
| 32-bit integer | s / 2³¹ |
| 32-bit float | as is |
| 64-bit float | rounded to 32-bit |

The viewer draws one waveform lane per channel, shows the rate, channels, size, length, loop and base note, and
plays the sound at its true pitch, converted by linear interpolation to the output device's 48 kHz stereo (a mono
sound on both sides; of more than two channels, the first two).

---

## 12. Diagnostics

| Code | Severity | Meaning | The Sound Manager |
| --- | --- | --- | --- |
| `sound.unknown-format` | Error | `format` is neither 1 nor 2; not read | `SndPlay` fails with −206 [Code] |
| `sound.short` | Error | the resource ends inside its synthesizer list; not read | not traced |
| `sound.short` | Error | the resource ends inside its command list; the whole commands before it are kept | not traced |
| `sound.short` | Error | the resource ends inside a 64-byte extended or compressed header; no sound | not traced |
| `sound.short` | Warning | the header counts more sample bytes than the resource holds; the samples are cut | not traced |
| `sound.several-sounds` | Info | more than one flagged `bufferCmd`/`soundCmd`; the first is decoded | `SndPlay` runs every command in order [Code] |
| `sound.header-offset` | Info | a format 2 command points elsewhere than 6 + 8 × `numCommands`; the header there is read | `SndPlay` does the same; `GetSoundHeaderOffset` trusts `param2` [Code] |
| `sound.bad-offset` | Error | the header's offset is negative or leaves fewer than 22 bytes in the resource; no sound | not traced |
| `sound.sample-pointer` | Warning | `samplePtr` is not 0; the samples after the header are read | takes `samplePtr` as the samples' address [Code] |
| `sound.no-rate` | Warning | the rate is 0; the WAV says 1 Hz | not traced |
| `sound.bad-header` | Error | `encode` is not $00, $FE or $FF; no sound | −206 [Code] |
| `sound.bad-header` | Error | 0 or more than 64 channels; no sound | more than 2 channels in an extended header: −206 [Code] |
| `sound.bad-header` | Error | an extended header's `sampleSize` is not 8, 16, 24 or 32; no sound | a size below 8 is raised to 8 by `SetSoundHeader` [Code] |
| `sound.bad-header` | Error | a `compressionID` other than 0, 3, 4, −1, −2; no sound | −223 [Code] |
| `sound.codec` | Warning | the samples are in a format ClassicMac does not decode (`'alaw'`, `'QDM2'`, …); exported raw | an `sdec` component for it, or −223 [Code] |

A sound whose header cannot be read still gives its JSON (§11.1).

---

## 13. Not covered

- **AIFF and AIFC files.** `SndStartFilePlay` parses them itself and plays them through the same `sdec`
  decompressors [Code]; ClassicMac will read them later with the codecs above.
- **Instrument playback** (§7): notes, loops and envelopes are not rendered; only the recorded samples are.
- **Square-wave and wave-table sounds**: resources for synthesizers 1 and 3 hold commands only and give the JSON
  alone.
- **Writing `'snd '`** and the MACE compressors (`Comp3to1`, `Comp6to1`, also in SoundLib) are planned for the
  editor.
