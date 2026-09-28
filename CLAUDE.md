# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

ClassicMac reads, converts and edits classic Mac OS files in .NET: resource forks and the containers they travel in,
resource decoders, a CLI and an Avalonia desktop app. The project is at the planning stage; `docs/PLAN.md` is the plan
and the source of decisions (architecture, packages, phases, open questions). Read it before starting work, and update
it in the same change when a decision changes.

## Ground truth

- Apple's documentation (*Inside Macintosh*, Technical Notes, file-format notes) is the specification.
- Where it is silent or ambiguous, the answer comes from the Mac OS code that handles the format (disassembly),
  never from another implementation's guess. Other projects (see the plan's prior-art tables) are behavioural
  references only.
- A rule fitted to real data rather than read from code is marked as such in code comments and docs.
- The resource extractor in the Realmz project is inspiration, not authority; re-check anything taken from it. Realmz
  data files are a test corpus, but ClassicMac is standalone and never depends on Realmz.

## Licensing

- MIT. Code ported from MIT projects keeps a notice in `THIRD-PARTY-NOTICES.md`; GPL/LGPL projects are reference only.
- Never commit Apple source code, Apple fonts or other Apple files, or private test harnesses.

## Related repository

QuickDraw.Pict (<https://github.com/inexin/QuickDraw.Pict>, NuGet `QuickDraw.Pict`) provides PICT and image
decoding and is used from NuGet. It is planned to merge into this repo later, split into Graphics / QuickTime /
MacPaint / QuickDraw layers (see the plan).

## Conventions

- Work on `main` (no feature branches).
- A change to how a format is read or written updates its document in `docs/formats/` in the same commit.
