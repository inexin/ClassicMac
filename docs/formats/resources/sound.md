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
  - the `sdec` decompressor components, the only five in the System file: `ima4` (`thng`/`sift`/`nift` −16589),
    `ulaw` (−16593), `sowt` (−20027) and the `MAC3`/`MAC6` wrappers (−16566, −16567); and the `conv` sifter
    (−16559), which converts between sample sizes;
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
8. [ClassicMac's output](#8-classicmacs-output)
9. [Writing sounds](#9-writing-sounds)
10. [Diagnostics](#10-diagnostics)
11. [Not covered](#11-not-covered)

---

## 1. Conventions

- The conventions and source tags of [README.md](../README.md) apply: big-endian values, `$` hex offsets, and one of
  **[Doc]**, **[Code]**, **[Verified]**, **[Author]**, **[Fitted]** on every rule; **[ClassicMac]** marks
  ClassicMac's own choice where a table needs a tag.
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
- Format 1: all commands run in order [Code].
- Format 2: `SndPlay` never reads the command list, its flags or `refCount`. It makes the channel a sampled-sound
  channel itself (`SetChannelType` 5) and issues its own `bufferCmd` for the header after the commands (§4) [Code].
- No other resource type holds sounds: the Sound Manager fetches only `'snd '` (`SysBeep`, falling back to ID 1, and
  `SndStartFilePlay`). There is no `'csnd'` anywhere in the System file or the ROM [Code].

ClassicMac reads formats 1 and 2, keeps the synthesizers or the reference count for the JSON (§8.3), and decodes the
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
- **`SndPlay`**, format 2: never reads the commands. It builds its own `bufferCmd` ($0051, `param1` 0) for the header
  at **6 + 8 × `numCommands`**, right after the commands, and plays it once, whatever the list holds: no command, a
  `soundCmd` or garbage [Code].
- **`GetSoundHeaderOffset`** (used by `ParseSndHeader` and by `SndStartFilePlay`), both formats: takes the first
  command that is exactly $8050 or $8051 and returns its `param2` unchecked. A $0051 command is missed
  [Code][Verified].

Real files depend on the difference. Realmz's format 2 sounds carry a `bufferCmd` with `param2` = 20, the format 1
position, while their header is at 14. `SndPlay` plays them correctly; `GetSoundHeaderOffset` returns 20 [Verified],
and `ParseSndHeader` then finds an `encode` byte of $6E and fails with −206 [Code].

ClassicMac follows `SndPlay`:

```
players = commands whose cmd has bit 15 set and whose code (cmd & $7FFF) is 80 or 81
if format == 2:
    offset = 6 + 8 × numCommands      # sound.header-offset when players[0].param2 >= 0 points elsewhere
else if players is empty:
    no sampled sound (the JSON alone, §11.1)
else:
    offset = players[0].param2
read the header at offset (§5)
```

- With several such commands the first is decoded (diagnostic `sound.several-sounds`); in format 1 `SndPlay` would
  play each in turn [Code].
- The format 1 selection (first flagged command with code 80 or 81) is the same as `GetSoundHeaderOffset`'s [Code].
- A format 2 sound plays once at its recorded rate: the command `SndPlay` builds ignores the loop and the base note
  (§3.4) [Code].

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
- `SetSoundHeader` raises a `sampleSize` below 8 to 8; `ParseSndHeader` does not. Nothing else is checked: every size
  other than 8 is read as 16-bit, so 24- and 32-bit samples are misread [Code]. ClassicMac accepts 8 and 16 and
  refuses any other size (`sound.bad-header`) rather than reproduce the misreading.
- 8-bit samples are offset binary (`'raw '`), 16-bit ones big-endian two's complement (`'twos'`) [Code].
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
| `'raw '`, `'twos'` | 1 | 1 for a `sampleSize` of 8, else 2 (§6) | as stored |
| `'sowt'` | 1 | 2 | 16-bit `'twos'` |
| `'MAC3'` | 6 | 2 | 8-bit offset binary |
| `'MAC6'` | 6 | 1 | 8-bit offset binary |
| `'ima4'` | 64 | 34 | 16-bit `'twos'` (or 8-bit on request) |
| `'ulaw'` | 1 | 1 | 16-bit `'twos'` |

MACE, IMA 4:1, `'sowt'` and µ-law [Code]; µ-law's counts also [Verified].

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
| `'raw '` | 8-bit offset binary: $80 is silence, $00 the most negative value; 16-bit: as `'twos'` | [Doc][Code] |
| `'twos'` | two's complement, big-endian, 8 or 16 bits | [Doc][Code] |
| `'sowt'` | 16-bit two's complement, little-endian, whatever `sampleSize` says (`sdec` −20027) | [Code] |
| `'in24'` | 24-bit two's complement, big-endian | [ClassicMac] |
| `'in32'` | 32-bit two's complement, big-endian | [ClassicMac] |
| `'fl32'` | 32-bit IEEE 754 float, big-endian, ±1.0 full scale | [ClassicMac] |
| `'fl64'` | 64-bit IEEE 754 float, big-endian | [ClassicMac] |
| `'MAC3'` | MACE 3:1 ([mace.md](../codecs/mace.md)) | [Code] |
| `'MAC6'` | MACE 6:1 ([mace.md](../codecs/mace.md)) | [Code] |
| `'ima4'` | IMA 4:1 ADPCM ([ima4.md](../codecs/ima4.md)) | [Code] |
| `'ulaw'` | µ-law ([ulaw.md](../codecs/ulaw.md)) | [Code] |

- Multi-channel samples are interleaved by frame (by packet, for the codecs) [Doc][Code].
- The Sound Manager decodes `'raw '`, `'twos'`, `'MAC3'` and `'MAC6'` itself; any other format needs an `sdec`
  component, and the System file has only `'MAC3'`, `'MAC6'`, `'ima4'`, `'ulaw'` and `'sowt'` [Code]. So stock
  Mac OS 9.0 plays those seven.
- `'in24'`, `'in32'`, `'fl32'`, `'fl64'` and `'alaw'` appear nowhere in the System file (either fork) or the ROM: the
  Sound Manager fails them with −223 [Code]. QuickTime may register components for them; that was not checked.
  ClassicMac reads the first four anyway, as QuickTime formats with the layouts above (its own choice; no Apple code
  for them was traced). `'alaw'` and any other codec are not decoded (`sound.codec`).
- The Sound Manager treats `'raw '` and `'twos'` alike past 8 bits: every `sampleSize` other than 8 is read as 16-bit
  signed big-endian, so 16-bit `'raw '` is **not** offset binary, and 24- or 32-bit samples are misread as 16-bit
  [Code]. ClassicMac reads 8- and 16-bit `'raw '` and `'twos'` and refuses other sizes (`sound.bad-header`).
- `'sowt'` ignores `sampleSize`: its component always reports 2 bytes per sample [Code]. ClassicMac reads it as
  16-bit.
- A trailing partial frame is dropped.

---

## 7. Loops and base note

The loop points and the base note are used **only for instrument playback** (`soundCmd` followed by note commands),
never by `bufferCmd` [Code]:

- A note plays at 2^((note − `baseFrequency`)/12) × the recorded rate, the difference clamped to ±127 semitones
  [Code]. A `baseFrequency` of 0 has **no special case**: base 0 with note 60 plays 32 times faster [Code].
- A loop exists only if `loopEnd` > `loopStart` and `loopEnd` − `loopStart` > 2 [Code].
- With a loop, the attack plays frames [0, `loopEnd`), the sustain repeats [`loopStart`, `loopEnd`) while the note
  lasts, and the release plays from `loopEnd` + 1 for `numFrames` − `loopEnd` frames [Code].
- Loop points count frames [Doc].

ClassicMac keeps them as information in the WAV's `smpl` chunk (§8.2) and the JSON, the base note as stored (0 is
note 0).

---

## 8. ClassicMac's output

### 8.1 Files

| Resource | Output |
| --- | --- |
| A sampled sound ClassicMac decodes | `.wav` (§8.2) and `.json` (§8.3) |
| Format 1 with commands only (no flagged `bufferCmd`/`soundCmd`), or a header that cannot be read | `.json` alone |
| A sampled sound in a format not decoded | nothing from the decoder (`sound.codec`); the exporter writes the resource's data as `.bin` |
| Not a `'snd '` (format neither 1 nor 2, or too short for its lists) | nothing from the decoder; exported as `.bin` |

### 8.2 WAV

A RIFF WAVE file [Doc] (Microsoft's specifications): `RIFF` size `WAVE`, then the chunks `fmt `, `fact` (float only),
`smpl` (when needed) and `data`, in that order. All values little-endian.

Samples:

| Source | WAV samples |
| --- | --- |
| 8-bit `'raw '` | copied (WAV's 8-bit samples are offset binary too) |
| 8-bit `'twos'` | each byte XOR $80 |
| 16-bit `'raw '` and `'twos'`, `'in24'`, `'in32'` | byte order reversed |
| `'sowt'` | copied |
| `'fl32'`, `'fl64'` | byte order reversed; IEEE float |
| MACE | 8-bit, as the Sound Manager gives it ([mace.md §5](../codecs/mace.md#5-output)) |
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
≤ the decoded frame count) or the base note is not 60 (0 included):

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

### 8.3 JSON sidecar

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

### 8.4 Decoded samples in the viewer

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

## 9. Writing sounds

ClassicMac's editor makes a `snd ` resource from samples or a WAV file (`SoundImport`), in the layout of §2 and §5
[ClassicMac]:

- **Resource:** format 1, one synthesizer (5, sampled sound; init options `initMono` $80, or `initStereo` $C0 for two
  channels), one command: `bufferCmd` with the data-offset bit ($8051), param1 0, param2 20, the header's offset.
- **Header:** samplePtr 0, the samples right after it.
  - Mono 8-bit: a standard header (encode $00, length in frames).
  - Otherwise an extended header (encode $FF): numChannels, numFrames, the rate again as an 80-bit extended
    (AIFFSampleRate), markerChunk/instrumentChunks/AESRecording 0, sampleSize 8 or 16, the future-use fields 0.
  - sampleRate: the rate as Fixed (rounded), so below 65536 Hz. baseFrequency 60 (middle C) unless given.
- **From WAV** (RIFF `fmt `, `data`, `smpl`; `WAVE_FORMAT_EXTENSIBLE` read by its sub-format):
  - 8-bit PCM is kept as it is (both are offset binary);
  - 16-, 24- and 32-bit PCM become 16-bit (the top 16 bits), float becomes 16-bit (× 32768, rounded and clamped);
    16-bit samples are written big-endian;
  - the `smpl` chunk's unity note becomes the base note, its first loop the loop (its inclusive end + 1 is loopEnd);
  - other chunks are ignored; a file without `fmt ` and `data`, or with samples of another format, is refused.
- Read back, the samples, rate, loop and base note are those written [Verified: ClassicMac's tests].

---

## 10. Diagnostics

| Code | Severity | Meaning | The Sound Manager |
| --- | --- | --- | --- |
| `sound.unknown-format` | Error | `format` is neither 1 nor 2; not read | `SndPlay` fails with −206 [Code] |
| `sound.short` | Error | the resource ends inside its synthesizer list; not read | not traced |
| `sound.short` | Error | the resource ends inside its command list; the whole commands before it are kept | not traced |
| `sound.short` | Error | the resource ends inside a 64-byte extended or compressed header; no sound | not traced |
| `sound.short` | Warning | the header counts more sample bytes than the resource holds; the samples are cut | not traced |
| `sound.several-sounds` | Info | more than one flagged `bufferCmd`/`soundCmd`; the first is decoded | `SndPlay` runs every command in order [Code] |
| `sound.header-offset` | Info | a format 2 command points elsewhere than 6 + 8 × `numCommands`; the header there is read | `SndPlay` never reads the commands and plays that header; `GetSoundHeaderOffset` trusts `param2` [Code] |
| `sound.bad-offset` | Error | the header's offset is negative or leaves fewer than 22 bytes in the resource; no sound | not traced |
| `sound.sample-pointer` | Warning | `samplePtr` is not 0; the samples after the header are read | takes `samplePtr` as the samples' address [Code] |
| `sound.no-rate` | Warning | the rate is 0; the WAV says 1 Hz | not traced |
| `sound.bad-header` | Error | `encode` is not $00, $FE or $FF; no sound | −206 [Code] |
| `sound.bad-header` | Error | 0 or more than 64 channels; no sound | more than 2 channels in an extended header: −206 [Code] |
| `sound.bad-header` | Error | an extended header's `sampleSize` is not 8 or 16; no sound | a size below 8 is raised to 8 by `SetSoundHeader`; any other is read as 16-bit [Code] |
| `sound.bad-header` | Error | a compressed header's `'raw '` or `'twos'` samples (`compressionID` 0 included) are not 8 or 16 bits; no sound | read as 16-bit [Code] |
| `sound.bad-header` | Error | a `compressionID` other than 0, 3, 4, −1, −2; no sound | −223 [Code] |
| `sound.codec` | Warning | the samples are in a format ClassicMac does not decode (`'alaw'`, `'QDM2'`, …); exported raw | an `sdec` component for it (Mac OS 9.0's System file has none for these), or −223 [Code] |

A sound whose header cannot be read still gives its JSON (§8.1).

---

## 11. Not covered

- **AIFF and AIFC files.** `SndStartFilePlay` parses them itself and plays them through the same `sdec`
  decompressors [Code]; ClassicMac will read them later with the codecs above.
- **Instrument playback** (§7): notes, loops and envelopes are not rendered; only the recorded samples are.
- **Square-wave and wave-table sounds**: resources for synthesizers 1 and 3 hold commands only and give the JSON
  alone.
- **Writing `'snd '`** and the MACE compressors (`Comp3to1`, `Comp6to1`, also in SoundLib) are planned for the
  editor.
