# AIFF and AIFF-C sound files

The Audio Interchange File Format is Apple's file format for sampled sound, defined with Electronic Arts' IFF: a
`FORM` chunk holding a common chunk with the sound's shape, a sound data chunk with the samples, and optional markers,
instrument settings and text. AIFF-C adds a compression type, so a file may hold MACE, IMA 4:1, µ-law or float samples.
SoundEdit, SoundApp, QuickTime and the Sound Manager's `SndStartFilePlay` made and played them. ClassicMac reads both
into the same sampled sound a `'snd '` resource gives, so the `'snd '` codecs decode them, and converts a file to WAV
and JSON.

| | |
| --- | --- |
| Identified by | Type `'AIFF'` or `'AIFC'`; the data fork starts with `'FORM'`, a length, and `'AIFF'` or `'AIFC'`; extensions `.aif`, `.aiff`, `.aifc` |
| ClassicMac | Reads and converts; `ClassicMac.Resources.Decoders.Sound` (`AiffFile`, the `sound.aiff` document converter) |
| Verified against | Nothing yet (the Sound Manager's readers traced, not run) |
| Sources | Apple, *Audio Interchange File Format "AIFF"*, version 1.3 (1989); Apple, *AIFF-C*, draft of 26 August 1991; *Inside Macintosh: Sound*; the Sound Manager extension of Mac OS 9.0 (ParseAIFFHeader and the file-play reader), traced |

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

All numbers are big-endian. A `pstring` is a length byte and that many bytes, followed by a pad byte when the length
byte and text together are odd, so the whole is even [Doc].

### 1.1 The FORM chunk

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | ckID | `'FORM'` |
| +$04 | 4 | ckSize | `u32`, the bytes after this field |
| +$08 | 4 | formType | `'AIFF'`, or `'AIFC'` for AIFF-C |
| +$0C | | chunks | §1.2, in any order |

[Doc]

### 1.2 A chunk

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | ckID | |
| +$04 | 4 | ckSize | `u32`, the data's length, not counting this header or the pad byte |
| +$08 | ckSize | data | |
| | 0 or 1 | pad | A zero byte after odd-sized data, so every chunk starts at an even offset |

A file holds at most one of each chunk below except `'ANNO'` and `'APPL'`, which may repeat [Doc].

### 1.3 Common chunk (`'COMM'`)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | numChannels | `i16`; 1 mono, 2 stereo, more for multichannel |
| +$02 | 4 | numSampleFrames | `u32`; frames in the sound data chunk; packets for MACE and IMA 4:1 (§2) |
| +$06 | 2 | sampleSize | `i16`; bits a sample, 1 to 32 for uncompressed data |
| +$08 | 10 | sampleRate | IEEE 754 80-bit extended: a sign bit and 15-bit exponent (bias 16383), then a 64-bit mantissa with an explicit integer bit |
| +$12 | 4 | compressionType | AIFF-C only: `'NONE'`, or a codec (§4.1) |
| +$16 | | compressionName | AIFF-C only: a `pstring`, for people ("not compressed") |

[Doc]

### 1.4 Sound data chunk (`'SSND'`)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | offset | `u32`; bytes of padding before the first sample frame |
| +$04 | 4 | blockSize | `u32`; the block alignment the offset serves, usually 0 |
| +$08 | | soundData | `offset` bytes, then the sample frames |

A sample frame is one sample for each channel, interleaved. An uncompressed sample is a two's-complement integer
left-justified in whole bytes: 1 to 8 bits take one byte, 9 to 16 two, 17 to 24 three, 25 to 32 four, the unused low
bits zero. The chunk may be absent when numSampleFrames is 0 [Doc].

### 1.5 Marker chunk (`'MARK'`)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | numMarkers | `u16` |
| +$02 | | markers | Each: id `i16` (above 0), position `u32` (a sample frame: 0 is before the first), markerName `pstring` |

[Doc]

### 1.6 Instrument chunk (`'INST'`)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | baseNote | `i8`; the MIDI note of the recorded pitch |
| +$01 | 1 | detune | `i8`; cents, −50 to 50 |
| +$02 | 1 | lowNote | `i8` |
| +$03 | 1 | highNote | `i8` |
| +$04 | 1 | lowVelocity | `i8` |
| +$05 | 1 | highVelocity | `i8` |
| +$06 | 2 | gain | `i16`; decibels |
| +$08 | 6 | sustainLoop | playMode `i16` (0 none, 1 forward, 2 forward and backward), beginLoop and endLoop marker IDs `i16` |
| +$0E | 6 | releaseLoop | As sustainLoop |

[Doc]

### 1.7 Other chunks

| ckID | Data |
| --- | --- |
| `'FVER'` | AIFF-C only: the version's timestamp, `u32` $A2805140 [Doc] |
| `'NAME'`, `'AUTH'`, `'(c) '`, `'ANNO'` | Text: the name, the author, the copyright, an annotation [Doc] |
| `'COMT'`, `'MIDI'`, `'AESD'`, `'APPL'` | Comments with time stamps, MIDI data, AES channel status, application data [Doc] |

## 2. Reading

The specification's reading [Doc]:

1. The data must start with `'FORM'` and a formType of `'AIFF'` or `'AIFC'`.
2. Walk the chunks from +$0C to the end of the FORM: each next chunk is at `ckSize + (ckSize & 1)` bytes past the
   current chunk's data. Chunks of unknown types are skipped.
3. `'COMM'` gives the shape; AIFF-C's adds the compression type and name.
4. The sample rate is the extended number's value: mantissa × 2^(exponent − 16383 − 63), negated when the sign is set.
5. The samples begin `offset` bytes into the SSND data. Uncompressed, a frame is `ceil(sampleSize / 8) × numChannels`
   bytes.

The Sound Manager of Mac OS 9.0 has two readers of its own, which differ [Code: Sound Manager, Mac OS 9.0]:

| | ParseAIFFHeader | The file-play reader (SndStartFilePlay) |
| --- | --- | --- |
| FORM | Walks 8-byte chunk headers from the file's current mark to its end; `'FORM'` must be followed by `'AIFF'` or `'AIFC'` (else paramErr −50); the FORM need not come first, its size is never used, and bytes after it are still walked | `'FORM'` at offset 0 and type `'AIFF'` or `'AIFC'`, else badFileFormat −208; the FORM's size is not checked |
| Chunk walk | Skips a chunk by `((ckSize + 1) & ~1) + 8`, padded to even, to the end of the file; a repeated COMM or SSND: the last wins | FindChunk scans from offset 12 for each chunk it needs, the first match winning, and skips by the raw ckSize, not padded, so an odd-sized chunk before COMM or SSND breaks the lookup |
| Required | COMM and SSND, in any order, else −208 | COMM and SSND, else −208 |
| COMM | AIFF: exactly 26 bytes with the chunk header, so a COMM whose ckSize is not 18 misaligns the walk; the type is `'NONE'`, named "None". AIFF-C: 30 bytes, then the rest of the chunk (rounded up to even) into a 256-byte name buffer, the pstring's length unused and unchecked; an empty name becomes the type's four characters | 26 or 30 bytes; the compression name is not read |
| Format | `'NONE'` becomes `'twos'`; any other type is passed to GetCompressionInfo(−1, type, channels, size), whose error is returned | |
| Sample size | Rounded up to a multiple of 8; numChannels and sampleSize are not range-checked | Not range-checked |
| Sample count | numSampleFrames × the codec's samples per packet: for MACE and IMA 4:1, numSampleFrames counts packets | Not traced |
| Rate | UnsignedFixed, rounded to nearest: X2Fix(rate) up to 32,767.0, else X2Fix(rate − 32,767) + $7FFF0000 | UnsignedFixed, truncated: rate × 65,536 |
| SSND | The samples start right after the chunk's 16-byte header: `offset` and `blockSize` are ignored | `offset` is honoured; `blockSize` is ignored |
| FVER | Not read, so neither required nor checked | Not read |
| MARK, INST, text | Not read: neither reader uses loops or the base note | Not read |

Either reader passes on the File Manager's errors. The compression types played are those the installed `sdec`
components know: on a Mac OS 9.0 with QuickTime, `'MAC3'`, `'MAC6'`, `'ima4'`, `'ulaw'`, `'alaw'`, `'fl32'`, `'fl64'`,
`'in24'`, `'in32'`, `'sowt'` and `'twos'`; the System file alone has fewer
([sound.md §2.6](../resources/sound.md#26-sample-formats)).

## 3. Writing

None.

## 4. Variants

### 4.1 AIFF-C compression types

Each type is the `'snd '` sample format of the same name ([sound.md §2.6](../resources/sound.md#26-sample-formats)),
except `'NONE'`, which is AIFF's big-endian integers [Doc]:

| compressionType | Samples |
| --- | --- |
| `'NONE'`, `'twos'` | Big-endian integers, as AIFF |
| `'sowt'` | 16-bit little-endian integers |
| `'in24'`, `'in32'` | 24- and 32-bit big-endian integers |
| `'fl32'`, `'fl64'` | IEEE floats |
| `'MAC3'`, `'MAC6'` | MACE 3:1 and 6:1 |
| `'ima4'` | IMA 4:1 |
| `'ulaw'`, `'alaw'` | G.711 µ-law, A-law |

## 5. ClassicMac

- `AiffFile.Read` reads as the file-play reader where it is defined (§2): the FORM at offset 0, the first COMM and
  SSND, the SSND's `offset` honoured, the rate also in Fixed, truncated; the walk pads odd chunks, as the
  specification and ParseAIFFHeader do. The samples go into a `SampledSound`, the record a `'snd '` header gives, with
  the sample format of §4.1 (uncompressed samples as `'twos'`, `'in24'` or `'in32'` by their byte width), so the
  `'snd '` codecs decode it; numSampleFrames counts packets for MACE and IMA 4:1, as ParseAIFFHeader's sample count
  does. The exact rate is kept as well. [ClassicMac]
- The first `'MARK'` and `'INST'` give the loop and base note written to the WAV, though the Sound Manager ignores
  them; the text chunks are kept, several `'ANNO'` as one text, a line each. A file with no SSND and no frames is read
  as silence, where both Sound Manager readers fail with −208. [ClassicMac]
- A FORM length past the data is cut to the data; a chunk running past the end is read as far as it goes. An SSND
  shorter than numSampleFrames gives the frames that are there. [ClassicMac]
- The `sound.aiff` document converter reads files of type `'AIFF'` or `'AIFC'`, with or without a resource fork, and
  writes `sound.wav` (the samples as [sound.md §5.3](../resources/sound.md#53-wav) writes them, the exact rate rounded
  to whole hertz, the loop and base note in `smpl`) and `sound.json` (form, compression type and name, channels,
  sample size, frames, rate, sample format, the texts, the markers and the instrument). A codec not decoded gives the
  JSON alone. [ClassicMac]
- The app previews such a file as a sound, with its exact rate and its format ("AIFF", "AIFF-C ('ima4')"). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `aiff.header` | Warning | The data does not start with a FORM of type AIFF or AIFC | Reads nothing | badFileFormat −208 (file play); paramErr −50 for another FORM type (ParseAIFFHeader) [Code] |
| `aiff.no-comm` | Warning | No COMM chunk, or one under 18 bytes | Reads nothing | −208 [Code] |
| `aiff.no-ssnd` | Warning | COMM counts frames but there is no SSND | Reads no samples | −208, with or without frames [Code] |
| `aiff.short` | Warning | SSND holds fewer frames than COMM counts | Reads the frames there | Not traced |
| `aiff.truncated` | Warning | A chunk runs past the end of the FORM or the file | Reads it as far as it goes | Not traced |
| `aiff.unreadable` | Warning | The data fork cannot be read | Converts nothing | The File Manager's error [Code] |
| `sound.codec` | Warning | The compression type is not decoded (`'alaw'`, …) | Writes the JSON alone | GetCompressionInfo's error when no `sdec` component knows the type [Code] |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/AiffFileTests.cs`: frames, channels, size and rate; rates of 44,100 Hz,
  the Mac's 22,254.5454 Hz and 96,000 Hz; the SSND offset and frame count; odd chunks and their pad byte, unknown chunks
  and text chunks; each AIFF-C compression type; packets counted as frames for IMA 4:1; signed 8-bit samples; the loop
  and base note from MARK and INST; damaged files; the WAV and JSON a conversion writes.
- `tests/ClassicMac.App.Tests/SoundPreviewTests.cs`, `An_AIFF_file_previews_as_a_sound`.
- `tests/ClassicMac.Cli.Tests/ConvertTests.cs`, `An_AIFF_sound_converts_to_WAV_and_JSON`.

## 8. Not covered

- How the file-play reader bounds the samples, and the limits GetCompressionInfo puts on channels and sample size.
- `'alaw'` (G.711 A-law, played only with QuickTime's components) and the `'COMT'`, `'MIDI'`, `'AESD'` and `'APPL'`
  chunks are not decoded; the release loop is not used.
- Writing AIFF.

## 9. References

1. Apple Computer, *Audio Interchange File Format: "AIFF", A Standard for Sampled Sound Files*, version 1.3 (1989).
2. Apple Computer, *Audio Interchange File Format AIFF-C*, draft (26 August 1991).
3. Apple, *Inside Macintosh: Sound* (1994), chapter 2 "Sound Manager".
