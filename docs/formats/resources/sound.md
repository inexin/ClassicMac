# Sound resources (snd)

The classic Mac OS sound resource, `'snd '`: a list of commands for a sound channel, usually one `bufferCmd` that plays
a sampled sound stored in the same resource, behind one of three sound headers. The System's alert sounds, games and
HyperCard stacks hold them; the samples are PCM or one of the codecs Sound Manager 3.5.1 ships, MACE 3:1 and 6:1,
IMA 4:1 and µ-law. ClassicMac decodes, sample for sample, what the Mac OS 9 Sound Manager plays, writes it as a WAV
file with a JSON sidecar, plays it in the viewer, and writes `'snd '` resources from samples or WAV files.

| | |
| --- | --- |
| Identified by | Resource type `'snd '`; the `u16` format 1 or 2 at +$00 |
| ClassicMac | Reads and writes; `ClassicMac.Resources.Decoders.Sound` (`SoundResource`, `SoundSamples`, `SoundImport`), the `sound.snd` decoder |
| Verified against | Sound Manager 3.5.1 on Mac OS 9.0 in SheepShaver: its own decoding of samples it compressed itself (MACE 3:1 and 6:1 mono and stereo from 8-bit sources, MACE 3:1 and 6:1 mono from 16-bit sources, IMA 4:1 mono and stereo, µ-law mono), byte for byte<br>Resources its `SetupSndHeader` wrote<br>Realmz's format 2 sounds |
| Sources | *Inside Macintosh: Sound* and `Sound.h` (Universal Interfaces 3.4); Sound Manager 3.5.1 on Mac OS 9.0 and the NewWorld ROM's 68k Sound Manager 3.2, traced in disassembly; ITU-T G.711; the IMA's recommended practices; Microsoft's WAV specifications (the output) |

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

In this document [Doc] is *Inside Macintosh: Sound* and `Sound.h`; [Code] is Sound Manager 3.5.1 on Mac OS 9.0 (the
System file's 68k Sound Manager, `gpch` 666, a relinked copy of the ROM's; SoundLib, `nlib` 666, the PowerPC code; the
`sdec` components), whose routines were also run in an emulator against the models given here, with no mismatch; "the
Sound Manager" is that version on a PowerPC Mac. Pseudocode works on 32-bit signed integers; `>>` is an arithmetic
shift. A **frame** is one sample per channel; a **packet** is a codec's smallest unit per channel (§2.5). Sound
Manager result codes: −205 `badChannel`, −206 `badFormat`, −223 `siInvalidCompression` [Doc].

## 1. Layout

### 1.1 Format 1

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 2 | format | `u16`, 1 |
| +2 | 2 | numSynths | `u16`, *n* |
| +4 | 6*n* | synthesizers | §1.3 |
| +4 + 6*n* | 2 | numCommands | `u16`, *m* |
| +6 + 6*n* | 8*m* | commands | §1.4 |
| +6 + 6*n* + 8*m* | … | sound header | Normally (§1.5) |

[Doc]. The usual resource has one synthesizer (5, sampled sound) and one command, so its header is at 20.

### 1.2 Format 2

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 2 | format | `u16`, 2 |
| +2 | 2 | refCount | `u16`, for the application's use |
| +4 | 2 | numCommands | `u16`, *m* |
| +6 | 8*m* | commands | §1.4 |
| +6 + 8*m* | … | sound header | §1.5 |

[Doc]. Format 2 was meant for HyperCard and names no synthesizer; the channel must be a sampled-sound channel.

### 1.3 Synthesizer entry

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 2 | synthesizer | `u16` resource ID: 1 `squareWaveSynth`, 3 `waveTableSynth`, 5 `sampledSynth`; 11 and 13 the old MACE 3 and MACE 6 synthesizers (`MACE3snthID`, `MACE6snthID`) |
| +2 | 4 | init options | `u32`, bit masks combined by OR (below) |

[Doc]. Init options:

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

[Doc]

### 1.4 Sound command

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 2 | cmd | `u16`: the command code in bits 0–14; bit 15 ($8000) the data-offset flag |
| +2 | 2 | param1 | `i16` |
| +4 | 4 | param2 | `i32` |

[Doc]. Command codes (`Sound.h`) [Doc]:

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

A sampled-sound resource stores `bufferCmd` as $8051 and `soundCmd` as $8050 [Doc].

### 1.5 Sound header: common fields

Every header starts with these 22 bytes [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | samplePtr | `u32`: 0 when the samples follow the header; otherwise their absolute address [Doc] [Code] |
| +$04 | 4 | length / numChannels | `u32`: standard header `length`; extended and compressed `numChannels` |
| +$08 | 4 | sampleRate | UnsignedFixed, in hertz |
| +$0C | 4 | loopStart | `u32`, in frames |
| +$10 | 4 | loopEnd | `u32`, in frames |
| +$14 | 1 | encode | $00 `stdSH`, $FF `extSH`, $FE `cmpSH` |
| +$15 | 1 | baseFrequency | The MIDI note the samples sound at (60 = middle C) |

Common rates: `rate22khz` $56EE8BA3 (22254.545 Hz, the classic Mac's hardware rate), `rate11khz` $2B7745D1
(11127.273 Hz), `rate44khz` $AC440000 [Doc].

### 1.6 Standard header (encode $00)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 22 | common fields | §1.5; +$04 is `length`, the number of frames, which is also the number of bytes |
| +$16 | length | sampleArea | One channel of 8-bit offset binary (`'raw '`, $80 silence) |

[Doc] [Code]

### 1.7 Extended header (encode $FF)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 22 | common fields | §1.5; +$04 is `numChannels` |
| +$16 | 4 | numFrames | `u32` |
| +$1A | 10 | AIFFSampleRate | extended80; not read |
| +$24 | 4 | markerChunk | Not read |
| +$28 | 4 | instrumentChunks | Not read |
| +$2C | 4 | AESRecording | Not read |
| +$30 | 2 | sampleSize | `u16`: bits per sample |
| +$32 | 2 | futureUse1 | |
| +$34 | 4 | futureUse2 | |
| +$38 | 4 | futureUse3 | |
| +$3C | 4 | futureUse4 | |
| +$40 | … | sampleArea | `numFrames` × `numChannels` × `sampleSize` / 8 bytes, channels interleaved by frame |

Layout [Doc]; which fields are read [Code].

### 1.8 Compressed header (encode $FE)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 22 | common fields | §1.5; +$04 is `numChannels` |
| +$16 | 4 | numFrames | `u32`: packets per channel (§2.5) |
| +$1A | 10 | AIFFSampleRate | extended80; not read |
| +$24 | 4 | markerChunk | Not read |
| +$28 | 4 | format | OSType: the sample format (§2.6) |
| +$2C | 4 | futureUse2 | |
| +$30 | 4 | stateVars | Not read |
| +$34 | 4 | leftOverSamples | Not read |
| +$38 | 2 | compressionID | `i16` (§2.5) |
| +$3A | 2 | packetSize | `u16`; not read |
| +$3C | 2 | snthID | `u16`; not read |
| +$3E | 2 | sampleSize | `u16`: bits per sample of the decompressed samples (for PCM, the stored ones) |
| +$40 | … | sampleArea | |

Layout [Doc]; which fields are read [Code]. `sampleSize` sits at +$3E here but at +$30 in an extended header [Doc];
reading one at the other's place is a common mistake.

## 2. Reading

### 2.1 What the Sound Manager uses

- `SndPlay` accepts formats 1 and 2 only; any other fails with −206 [Code].
- Format 1: only the first synthesizer's ID is used; its init options, and any further synthesizers, do not change how
  the resource is read. With no synthesizer the channel gets the note synthesizer, and a `bufferCmd` then fails with
  −205. All commands run in order [Code].
- Format 2: `SndPlay` never reads the command list, its flags or `refCount`. It makes the channel a sampled-sound
  channel itself (`SetChannelType` 5) and issues its own `bufferCmd` for the header after the commands (§2.3) [Code].
- No other resource type holds sounds: the Sound Manager fetches only `'snd '` (`SysBeep`, falling back to ID 1, and
  `SndStartFilePlay`). There is no `'csnd'` anywhere in the System file or the ROM [Code].

### 2.2 bufferCmd and soundCmd

- With the data-offset flag, `param2` is an offset from the start of the resource (to a sound header, for `bufferCmd`
  and `soundCmd`) [Doc] [Code]. Without it, `param2` is an absolute memory address [Code], meaningless in a file.
- `bufferCmd` plays the header's frames once, at `sampleRate` × the channel's rate multiplier. The loop points and the
  base note are ignored [Code].
- Undocumented: a `bufferCmd` whose `param1` is −3141 sets the rate from the base note, 2^((60 − `baseFrequency`)/12)
  [Code].
- `soundCmd` only installs the sampled sound as the channel's instrument; the notes come from later commands
  (`freqDurationCmd`, `freqCmd`) [Doc] [Code]. Instrument playback is where the loop and base note count (§2.7).

### 2.3 Finding the sound header

The Sound Manager has two ways of finding a resource's header, and they disagree on format 2 [Code]:

- **`SndPlay`, format 1**: runs the commands; a `bufferCmd` or `soundCmd` with the data-offset flag finds its header at
  `param2` [Doc] [Code]. With several, each plays in turn.
- **`SndPlay`, format 2**: never reads the commands. It builds its own `bufferCmd` ($0051, `param1` 0) for the header
  at 6 + 8 × `numCommands`, right after the commands, and plays it once at its recorded rate, whatever the list holds:
  no command, a `soundCmd` or garbage [Code].
- **`GetSoundHeaderOffset`** (used by `ParseSndHeader` and `SndStartFilePlay`), both formats: takes the first command
  that is exactly $8050 or $8051 and returns its `param2` unchecked. A $0051 command is missed [Code] [Verified].

Real files depend on the difference. Realmz's format 2 sounds carry a `bufferCmd` with `param2` = 20, the format 1
position, while their header is at 14. `SndPlay` plays them correctly; `GetSoundHeaderOffset` returns 20 [Verified],
and `ParseSndHeader` then finds an `encode` byte of $6E and fails with −206 [Code].

### 2.4 The header

1. Read the common fields (§1.5). Any `encode` other than $00, $FE, $FF fails with −206 [Code].
2. Standard header: always one channel of 8-bit `'raw '`, `length` frames [Doc] [Code].
3. Extended and compressed headers: `numChannels` is documented as a long, but the Sound Manager reads the word at
   +$06 [Code]. More than 2 channels in an extended header fails with −206 [Code].
4. Extended header: `SetSoundHeader` raises a `sampleSize` below 8 to 8; `ParseSndHeader` does not. Nothing else is
   checked: every size other than 8 is read as 16-bit, so 24- and 32-bit samples are misread [Code]. 8-bit samples are
   `'raw '`, 16-bit ones `'twos'` [Code].
5. Compressed header: beyond the common fields only `numFrames`, `format`, `compressionID` and `sampleSize` are read;
   `packetSize`, `snthID`, `stateVars` and `leftOverSamples` are never used [Code]. The format is §2.5.

### 2.5 compressionID, format and packets

`GetCompressionInfo` picks the sample format from `compressionID` [Code] (names [Doc]):

| compressionID | Name | Sound Manager 3.5.1 |
| --- | --- | --- |
| 0 | `notCompressed` | PCM, whatever `format` says: `sampleSize` 8 → `'raw '`, otherwise `'twos'` |
| −1 | `fixedCompression` | `format` |
| −2 | `variableCompression` | `format` |
| 1 | `twoToOne` | refused, −223 |
| 2 | `eightToThree` | refused, −223 |
| 3 | `threeToOne` | `'MAC3'` |
| 4 | `sixToOne` | `'MAC6'` |
| any other | | refused, −223 |

- `'raw '`, `'twos'`, `'MAC3'` and `'MAC6'` are built into the Sound Manager. Any other `format` is looked up as an
  `sdec` component (its `GetInfo` selector `'cmfa'`); if there is none, −223 [Code].
- `numFrames` counts packets: the sample count is `numFrames` × samples per packet [Code] [Verified].

| Format | Samples per packet | Bytes per packet per channel | Output of the Sound Manager's decoder |
| --- | --- | --- | --- |
| `'raw '`, `'twos'` | 1 | 1 for a `sampleSize` of 8, else 2 | as stored |
| `'sowt'` | 1 | 2 | 16-bit `'twos'` |
| `'MAC3'` | 6 | 2 | 8-bit offset binary |
| `'MAC6'` | 6 | 1 | 8-bit offset binary |
| `'ima4'` | 64 | 34 | 16-bit `'twos'` (or 8-bit on request) |
| `'ulaw'` | 1 | 1 | 16-bit `'twos'` |

MACE, IMA 4:1, `'sowt'` and µ-law [Code]; µ-law's counts also [Verified].

### 2.6 Sample formats

| format | Meaning | Source |
| --- | --- | --- |
| `'raw '` | 8-bit offset binary: $80 is silence, $00 the most negative value; past 8 bits, as `'twos'` | [Doc] [Code] |
| `'twos'` | two's complement, big-endian, 8 or 16 bits | [Doc] [Code] |
| `'sowt'` | 16-bit two's complement, little-endian, whatever `sampleSize` says (`sdec` −20027 always reports 2 bytes a sample) | [Code] |
| `'MAC3'` | MACE 3:1 ([mace.md](../codecs/mace.md)) | [Code] |
| `'MAC6'` | MACE 6:1 ([mace.md](../codecs/mace.md)) | [Code] |
| `'ima4'` | IMA 4:1 ADPCM ([ima4.md](../codecs/ima4.md)) | [Code] |
| `'ulaw'` | µ-law ([ulaw.md](../codecs/ulaw.md)) | [Code] |

- Multi-channel samples are interleaved by frame, by packet for the codecs [Doc] [Code].
- The Sound Manager decodes `'raw '`, `'twos'`, `'MAC3'` and `'MAC6'` itself; any other format needs an `sdec`
  component, and the System file has only `'MAC3'`, `'MAC6'`, `'ima4'`, `'ulaw'` and `'sowt'` (`thng`/`sift`/`nift`
  −16566, −16567, −16589, −16593, −20027; the `conv` sifter −16559 converts between sample sizes) [Code]. So stock
  Mac OS 9.0 plays those seven.
- The Sound Manager treats `'raw '` and `'twos'` alike past 8 bits: every `sampleSize` other than 8 is read as 16-bit
  signed big-endian, so 16-bit `'raw '` is not offset binary, and 24- or 32-bit samples are misread as 16-bit [Code].
- `'in24'`, `'in32'`, `'fl32'`, `'fl64'` and `'alaw'` appear nowhere in the System file (either fork) or the ROM: the
  Sound Manager fails them with −223 [Code]. QuickTime may register components for them; that was not checked.

### 2.7 Loops and base note

The loop points and the base note are used only for instrument playback (`soundCmd` followed by note commands), never
by `bufferCmd` [Code]:

- A note plays at 2^((note − `baseFrequency`)/12) × the recorded rate, the difference clamped to ±127 semitones
  [Code]. A `baseFrequency` of 0 has no special case: base 0 with note 60 plays 32 times faster [Code].
- A loop exists only if `loopEnd` > `loopStart` and `loopEnd` − `loopStart` > 2 [Code].
- With a loop, the attack plays frames [0, `loopEnd`), the sustain repeats [`loopStart`, `loopEnd`) while the note
  lasts, and the release plays from `loopEnd` + 1 for `numFrames` − `loopEnd` frames [Code].
- Loop points count frames [Doc].

## 3. Writing

### 3.1 What the Sound Manager writes

`SetupSndHeader` writes format 1, synthesizer 5, with init options $0380 for MACE 3 mono, $03C0 for MACE 3 stereo,
$0480 for MACE 6 mono, and $0080 or $00C0 for IMA 4:1 and µ-law [Verified]. Its compressed headers' `numFrames` count
packets: 7424 for 44 544 samples of MACE (44 544 / 6), 696 for IMA 4:1 (44 544 / 64), and 44 544 for µ-law
[Verified].

### 3.2 What ClassicMac writes

`SoundImport` makes a `'snd '` from samples or a WAV file [ClassicMac]:

1. **Resource:** format 1, one synthesizer (5; init options `initMono` $80, or `initStereo` $C0 for two channels),
   one command: `bufferCmd` with the data-offset flag ($8051), `param1` 0, `param2` 20, the header's offset.
2. **Header:** `samplePtr` 0, the samples right after it, cut to whole frames.
   - Mono 8-bit: a standard header (`encode` $00, `length` in frames).
   - Otherwise an extended header (`encode` $FF): `numChannels`, `numFrames`, the rate again as an 80-bit extended
     (`AIFFSampleRate`), `markerChunk`, `instrumentChunks` and `AESRecording` 0, `sampleSize` 8 or 16, the future-use
     fields 0.
   - `sampleRate`: the rate as UnsignedFixed, rounded, so above 0 and below 65536 Hz. `baseFrequency` 60 unless given.
   - Samples are 8-bit offset binary or 16-bit big-endian two's complement, 1 to 64 channels.
3. **From WAV** (RIFF `fmt `, `data`, `smpl`; `WAVE_FORMAT_EXTENSIBLE` read by its sub-format):
   - 8-bit PCM is kept as it is (both are offset binary);
   - 16-, 24- and 32-bit PCM become 16-bit (the top 16 bits), 32- and 64-bit float become 16-bit (× 32768, rounded
     and clamped);
   - the `smpl` chunk's unity note (at most 127) becomes the base note, its first loop the loop (its inclusive end + 1
     is `loopEnd`; an empty loop is none);
   - other chunks are ignored; a file without `fmt ` and `data`, with samples of another format, or a rate of 65536 Hz
     or more is refused.
   - The app previews a WAV file (one whose data fork starts with `RIFF` and `WAVE`, whatever its type) as the
     `'snd '` this makes, and says why when it is refused.

## 4. Variants

- Formats 1 and 2 (§1.1, §1.2) and the three headers (§1.6–§1.8) [Doc].
- The NewWorld ROM's 68k Sound Manager 3.2 decodes MACE by other code where the 68k behaviour differs:
  [mace.md §4.1](../codecs/mace.md#41-the-68k-roms-expanders) [Code: 68k ROM].
- `'in24'`, `'in32'`, `'fl32'` and `'fl64'` are QuickTime formats that the Sound Manager itself refuses (§2.6).

## 5. ClassicMac

### 5.1 Reading

- Formats 1 and 2 are read; the synthesizers or the reference count are kept for the JSON, and the sampled sound is
  decoded whatever the synthesizers say. A resource under 4 bytes is not a `'snd '` and gives nothing, without a
  diagnostic. [ClassicMac]
- Commands without the data-offset flag are ignored when looking for a sound. The header is found as `SndPlay` finds
  it (§2.3) [ClassicMac]:

  ```
  players = commands whose cmd has bit 15 set and whose code (cmd & $7FFF) is 80 or 81
  if format == 2:
      offset = 6 + 8 × numCommands      # sound.header-offset when players[0].param2 >= 0 points elsewhere
  else if players is empty:
      no sampled sound (the JSON alone, §5.2)
  else:
      offset = players[0].param2         # sound.several-sounds when there are more
  read the header at offset (§2.4)
  ```

  The format 1 choice is also `GetSoundHeaderOffset`'s [Code].
- A non-zero `samplePtr` is reported and the samples after the header read (`sound.sample-pointer`). [ClassicMac]
- An extended or compressed header of 1 to 64 channels is accepted, 0 or more refused (`sound.bad-header`). An
  extended header's `sampleSize`, and a compressed header's `'raw '` or `'twos'` size (`compressionID` 0 included),
  must be 8 or 16; others are refused rather than misread as 16-bit. A `compressionID` the Sound Manager refuses is
  refused. [ClassicMac]
- `'sowt'` is read as 16-bit whatever `sampleSize` says. `'in24'`, `'in32'`, `'fl32'` (±1.0 full scale) and `'fl64'`
  are read as big-endian 24- and 32-bit two's complement and 32- and 64-bit IEEE floats, with no Apple code traced
  for them. `'alaw'` and any other codec are not decoded (`sound.codec`). [ClassicMac]
- The samples taken: PCM `numFrames` × `numChannels` × bytes per sample; MACE, IMA 4:1 and µ-law `numFrames` ×
  `numChannels` × bytes per packet; any other format everything from +$40 to the end. Where the resource holds fewer
  bytes, the samples are cut (`sound.short`); a trailing partial frame is dropped. [ClassicMac]

### 5.2 Output files

| Resource | Output |
| --- | --- |
| A sampled sound ClassicMac decodes | `.wav` (§5.3) and `.json` (§5.4) |
| Format 1 with commands only (no flagged `bufferCmd`/`soundCmd`), or a header that cannot be read | `.json` alone |
| A sampled sound in a format not decoded | nothing from the decoder (`sound.codec`); the exporter writes the resource's data as `.bin` |
| Not a `'snd '` (format neither 1 nor 2, or too short for its lists) | nothing from the decoder; exported as `.bin` |

The decoder is `sound.snd`, version 1. [ClassicMac]

### 5.3 WAV

A RIFF WAVE file (Microsoft's specifications): `RIFF` size `WAVE`, then the chunks `fmt `, `fact` (float only), `smpl`
(when needed) and `data`, in that order, all values little-endian. [ClassicMac]

| Source | WAV samples |
| --- | --- |
| 8-bit `'raw '` | copied (WAV's 8-bit samples are offset binary too) |
| 8-bit `'twos'` | each byte XOR $80 |
| 16-bit `'raw '` and `'twos'`, `'in24'`, `'in32'` | byte order reversed |
| `'sowt'` | copied |
| `'fl32'`, `'fl64'` | byte order reversed; IEEE float |
| MACE | 8-bit, as the Sound Manager gives it ([mace.md §2.4](../codecs/mace.md#24-output)) |
| IMA 4:1, µ-law | 16-bit |

The rate is the header's rate rounded to whole hertz, at least 1: 22254.545 Hz becomes 22255. The exact rate is in the
JSON.

`fmt ` chunk, 16 bytes, or 40 when the samples are float, wider than 16 bits, or in more than 2 channels:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 2 | format tag | `u16`: 1 (PCM), or $FFFE (`WAVE_FORMAT_EXTENSIBLE`) |
| +2 | 2 | channels | `u16` |
| +4 | 4 | rate | `u32`, rounded |
| +8 | 4 | bytes per second | `u32`: rate × block align |
| +12 | 2 | block align | `u16`: channels × bytes per sample |
| +14 | 2 | bits per sample | `u16`: 8 × bytes per sample |
| +16 | 2 | extra size | Extensible only: 22 |
| +18 | 2 | valid bits per sample | = bits per sample |
| +20 | 4 | channel mask | 0 (no speaker positions) |
| +24 | 16 | subformat | GUID: 1 (PCM) or 3 (IEEE float), followed by `00 00 00 00 10 00 80 00 00 AA 00 38 9B 71` |

`fact` chunk (float only): 4 bytes, the number of frames.

`smpl` chunk, written when the loop lies inside the sound (a loop as the Sound Manager counts one, §2.7, and `loopEnd`
≤ the decoded frame count) or the base note is not 60 (0 included; it is kept as note 0):

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 4 | manufacturer | 0 |
| +4 | 4 | product | 0 |
| +8 | 4 | sample period | Nanoseconds: round(10⁹ / rounded rate) |
| +12 | 4 | MIDI unity note | The base note |
| +16 | 4 | pitch fraction | 0 |
| +20 | 4 | SMPTE format | 0 |
| +24 | 4 | SMPTE offset | 0 |
| +28 | 4 | number of loops | 1 or 0 |
| +32 | 4 | sampler data | 0 |
| +36 | 24 | loop | When there is one: cue point ID 0, type 0 (forward), start `loopStart`, end `loopEnd` − 1 (WAV's end is inclusive), fraction 0, play count 0 (for ever) |

`data` chunk: the samples, then a pad byte when their length is odd.

### 5.4 JSON sidecar

What WAV cannot hold. All numbers are JSON numbers; `$`-prefixed strings are 8-digit hex. [ClassicMac]

| Field | Meaning |
| --- | --- |
| `format` | 1 or 2 |
| `synthesizers` | Format 1 only: `[{ "id": …, "initOptions": "$000000C0" }, …]` |
| `referenceCount` | Format 2 only |
| `commands` | Every command, in order: `command` (the code without the flag), `name` (§1.4, when known), `dataOffset` (`true`, present only when the flag is set), `param1`, `param2` |
| `sound` | Present when a sampled sound was found: |
| `sound.header` | `"standard"`, `"extended"` or `"compressed"` |
| `sound.headerOffset` | Where the header was read (for format 2, the offset §2.3 gives) |
| `sound.sampleRate` | The exact rate in hertz, rounded to 6 decimals |
| `sound.sampleRateFixed` | The rate as stored, `$56EE8BA3` |
| `sound.channels` | Channels |
| `sound.sampleSize` | Bits per sample of the decoded sound (8 for MACE, 16 for IMA 4:1 and µ-law) |
| `sound.sampleFormat` | The format after `compressionID` is applied (§2.5): `raw `, `twos`, `MAC3`, … |
| `sound.compressionId`, `sound.packetSize` | Compressed headers only, as stored |
| `sound.frames` | `numFrames` or `length` as stored: packets per channel for the codecs |
| `sound.loopStart`, `sound.loopEnd`, `sound.baseNote` | As stored |

### 5.5 Decoded samples in the viewer

`SoundSamples.Decode` gives a `DecodedSound`: the samples as 32-bit floats in [−1, 1], channels interleaved, with the
channel count and the exact rate. From the WAV samples (§5.3) [ClassicMac]:

| Width | Float |
| --- | --- |
| 8-bit | (b − 128) / 128 |
| 16-bit | s / 32768 |
| 24-bit | (s << 8) / 2³¹ |
| 32-bit integer | s / 2³¹ |
| 32-bit float | as is |
| 64-bit float | rounded to 32-bit |

The viewer draws one waveform lane per channel, shows the rate, channels, size, length, loop and base note, and plays
the sound at its true pitch, converted by linear interpolation to the output device's 48 kHz stereo (a mono sound on
both sides; of more than two channels, the first two). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `sound.bad-header` | Error | `encode` is not $00, $FE or $FF | No sound; the JSON alone | −206 [Code] |
| `sound.bad-header` | Error | 0 or more than 64 channels | No sound | More than 2 channels in an extended header: −206 [Code] |
| `sound.bad-header` | Error | An extended header's `sampleSize` is not 8 or 16 | No sound | A size below 8 is raised to 8 by `SetSoundHeader`; any other is read as 16-bit [Code] |
| `sound.bad-header` | Error | A compressed header's `'raw '` or `'twos'` samples (`compressionID` 0 included) are not 8 or 16 bits | No sound | Read as 16-bit [Code] |
| `sound.bad-header` | Error | A `compressionID` other than 0, 3, 4, −1, −2 | No sound | −223 [Code] |
| `sound.bad-offset` | Error | The header's offset is negative or leaves fewer than 22 bytes in the resource | No sound | Not traced |
| `sound.codec` | Warning | The samples are in a format ClassicMac does not decode (`'alaw'`, `'QDM2'`, …) | Exports the resource raw | An `sdec` component for it (Mac OS 9.0's System file has none for these), or −223 [Code] |
| `sound.header-offset` | Info | A format 2 command points elsewhere than 6 + 8 × `numCommands` | Reads the header after the commands | `SndPlay` never reads the commands and plays that header; `GetSoundHeaderOffset` trusts `param2` [Code] |
| `sound.no-rate` | Warning | The rate is 0 | Writes 1 Hz in the WAV | Not traced |
| `sound.sample-pointer` | Warning | `samplePtr` is not 0 | Reads the samples after the header | Takes `samplePtr` as the samples' address [Code] |
| `sound.several-sounds` | Info | More than one flagged `bufferCmd`/`soundCmd` | Decodes the first | `SndPlay` runs every command in order [Code] |
| `sound.short` | Error | The resource ends inside its synthesizer list or before its command count | Not read | Not traced |
| `sound.short` | Error | The resource ends inside its command list | Keeps the whole commands before it | Not traced |
| `sound.short` | Error | The resource ends inside a 64-byte extended or compressed header | No sound | Not traced |
| `sound.short` | Warning | The header counts more sample bytes than the resource holds | Cuts the samples | Not traced |
| `sound.unknown-format` | Error | `format` is neither 1 nor 2 | Not read | `SndPlay` fails with −206 [Code] |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/SoundDecoderTests.cs`, on resources built in the test: a standard sound
  as an 8-bit WAV and its JSON; loops and base notes in `smpl`; format 2 sounds without commands and with a misleading
  offset; PCM sizes other than 8 and 16 refused; extended 16-bit stereo; compressed headers with every PCM format;
  formats not read exported raw; compression IDs; IMA 4:1 packets; sounds without samples giving JSON only; damage
  reported, not thrown.
- `SoundDecoderTests.Sound_Manager_samples_decode_as_the_Sound_Manager_does` (runs when `CLASSICMAC_CORPUS` holds the
  samples; not committed, as they were made with Apple's software): MACE 3:1 and 6:1 from 8- and 16-bit sources, mono
  and stereo, IMA 4:1 mono and stereo and µ-law mono, each byte-identical to Sound Manager 3.5.1's own decoding in
  SheepShaver, Mac OS 9.0 [Verified].
- `tests/ClassicMac.Resources.Decoders.Tests/ImportTests.cs`, `Wav_files_become_sampled_sounds`: 16-bit stereo with a
  loop and base note, 8-bit mono and float WAV files written and read back with the samples, rate, loop and base note
  written; a non-WAV file and a 96 kHz rate refused.
- Golden outputs `snd_-128.json` to `snd_-137.json` in `tests/ClassicMac.Resources.Decoders.Tests/Golden`
  (`GoldenFixtures`, WAV hashes in `golden.json`): a standard sound with a loop and base note 72, format 2, extended
  16-bit stereo, `'sowt'`, `'fl32'`, `'MAC3'`, `'MAC6'`, `'ima4'`, `'ulaw'`, and a sound of commands only.

## 8. Not covered

- AIFF and AIFF-C files are [aiff.md](../documents/aiff.md); `SndStartFilePlay` parses them itself and plays them
  through the same `sdec` decompressors [Code].
- Instrument playback (§2.7): notes, loops and envelopes are not rendered; only the recorded samples are.
- Square-wave and wave-table sounds: resources for synthesizers 1 and 3 hold commands only and give the JSON alone.
- Writing compressed sounds: the MACE compressors (`Comp3to1`, `Comp6to1`, also in SoundLib), IMA 4:1 and µ-law.
- Whether QuickTime's components play `'in24'`, `'in32'`, `'fl32'`, `'fl64'` and `'alaw'` resources.

## 9. References

1. Apple, *Inside Macintosh: Sound* (1994), chapter 2 "Sound Manager": sound resources, sound commands, the
   `SoundHeader`, `ExtSoundHeader` and `CmpSoundHeader` records. Constants as in Apple's `Sound.h` (Universal
   Interfaces 3.4).
2. ITU-T Recommendation G.711 (µ-law).
3. IMA Digital Audio Focus and Technical Working Groups, *Recommended Practices for Enhancing Digital Audio
   Compatibility in Multimedia Systems*, revision 3.00 (1992): the IMA ADPCM algorithm.
4. Microsoft, *Multimedia Programming Interface and Data Specifications 1.0* (1991), and *Multiple Channel Audio Data
   and WAVE Files* (`WAVE_FORMAT_EXTENSIBLE`): the WAV output.
5. FFmpeg, its MACE decoder. LGPL; behavioural reference only, no code or table taken. ClassicMac's MACE decoder comes
   from Apple's binaries alone ([mace.md](../codecs/mace.md)).
6. resource_dasm, its MACE decoder, derived from FFmpeg's. Licence not recorded; behavioural reference only, no code
   or table taken.
