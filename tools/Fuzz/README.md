# Fuzz

Fuzzes ClassicMac's readers and writers with [libFuzzer](https://llvm.org/docs/LibFuzzer.html) through
[SharpFuzz](https://github.com/Metalnem/sharpfuzz). The nightly workflow (`.github/workflows/fuzz.yml`) runs every
target for five minutes and keeps each target's minimised corpus in the Actions cache.

## Targets

| Target | Fuzzes |
| --- | --- |
| `container` | Unwrapping any input through every container reader |
| `resource-fork` | Resource fork reading |
| `resource` | One resource through every decoder |
| `code` | 68k code: applications, code resources, disassembly |
| `pef` | PEF containers and their loader data |
| `pict` | PICT playback on both QuickDraws |
| `tiff` | TIFF images: every compression and colour model, JPEG streams put together for a stand-in decoder |
| `wav` | WAV files as the app previews them: made into a `'snd '`, read back and decoded |
| `first-aid` | First Aid's verify and repair on HFS and HFS Plus volumes |
| `ndif-write` | NDIF images made, rewritten and split, then read back |
| `hfs-edit` | Up to 24 edits on a new HFS volume, each checked by the writer's checks, First Aid and a model |
| `wrappers` | MacBinary III, AppleSingle, BinHex, resource forks, DeRez and Rez, ADC, KenCode and PackBits round trips |
| `pict-write` | `PictWriter` in every pixel format, and recordings of what the port drew |

The writer targets read their choices from the input (`FuzzReader`); a writer refusing its own output is a crash, as
is a fault a reader or decoder reports.

## Running

```
dotnet publish tools/Fuzz -c Release -o fuzz
dotnet fuzz/Fuzz.dll seeds seeds .
dotnet tool install --global SharpFuzz.CommandLine
for dll in fuzz/ClassicMac.*.dll; do sharpfuzz "$dll"; done
libfuzzer-dotnet --target_path=fuzz/Fuzz --target_arg=pict -max_total_time=300 corpus seeds/pict
```

`libfuzzer-dotnet` comes from [libfuzzer-dotnet's releases](https://github.com/Metalnem/libfuzzer-dotnet/releases)
(Windows and Ubuntu builds). Seeds are written from the uninstrumented build, before the assemblies are
instrumented.

A crash is reproduced without libFuzzer, then becomes a test:

```
dotnet run --project tools/Fuzz -- replay pict crash-1234
```
