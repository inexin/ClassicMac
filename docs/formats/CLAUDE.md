# Writing the format documents

These rules hold for every file under `docs/formats/`. [README.md](README.md) is the reader's guide: the index, the
reference builds, the shared conventions and the source tags. [TEMPLATE.md](TEMPLATE.md) is the skeleton to copy.

## What a document is

- An implementer's specification: enough to write a reader (and a writer, where ClassicMac writes the format) without
  reading ClassicMac's code, with the source of every rule.
- **One file per format**, in its category folder: `containers/`, `archives/`, `disk-images/`, `file-systems/`,
  `resources/`, `graphics/`, `codecs/`, `output/`. A codec used by one format stays in that format's file; it moves to
  `codecs/` when a second format uses it.
- File names are lowercase-kebab (`compact-pro.md`). A new file gets a row in the README index in the same commit.

## The skeleton

Every format file follows [TEMPLATE.md](TEMPLATE.md): the summary paragraph, the facts table, Contents, then the nine
numbered sections in order: Layout, Reading, Writing, Variants, ClassicMac, Diagnostics, Verification, Not covered,
References. A section that does not apply keeps its heading and says "None." Subsections are numbered `n.m`.

## Sources

- Ground truth is the root [CLAUDE.md](../../CLAUDE.md)'s: Apple's documentation first; where it is silent, Apple's
  code as traced in disassembly; a rule fitted to real files is marked as such.
- Every rule carries a tag from the README (`[Doc]`, `[Code: …]`, `[Verified: …]`, `[Author]`, `[Fitted]`,
  `[Reference: …]`, `[ClassicMac]`). One syntax: square brackets, plain text, an optional detail after a colon. No
  bold tags, no new tags.
- `[Fitted]` means fitted to real files with no code traced. A rule taken from another implementation is
  `[Reference: <project>]`, never `[Fitted]`.
- Private analysis material (disassembly notes, harness outputs, leaked source) is never cited or linked. Say what
  was traced (`[Code: Mac OS 9.0 IconUtils]`), not where the notes are.
- Other projects are behavioural references only. Name each under References with its licence; GPL, LGPL and AGPL
  code is never copied or ported.

## Style

- State rules, not history. "The reader used to…", "now", "formerly" belong in CHANGELOG.md.
- One rule in one place. Elsewhere, link to it; do not restate it.
- Structures are tables: `Offset | Size | Field | Notes`, offsets in hex (`+$1C`), sizes in decimal, reserved
  fields named. Algorithms are numbered steps. Prose explains; it does not carry field layouts.
- Format rules go in Layout, Reading, Writing and Variants. ClassicMac's own choices (options, limits, names, how it
  recovers) go in ClassicMac. Test data, fixtures and what they prove go only in Verification, with their paths.
- Plain words and short sentences. Name the Mac routine a rule comes from.

## Diagnostics

One table per file, columns `Code | Severity | When | ClassicMac does | The Mac does`, every code the reader emits,
sorted by code. Severity is Info, Warning or Error, spelled out.

## Cross-references

- To another document: a relative link with the section's anchor, `[hfs.md §2](../file-systems/hfs.md#2-reading)`.
- Within a document: `§6.2`, linked when it helps.
- From code comments: the repo path and section, `docs/formats/archives/stuffit5.md §2`.
- Never "section n".

## Keeping them current

- A change to how ClassicMac reads or writes a format updates its document in the same commit: the rule, a new
  diagnostic code in Diagnostics, a new fixture in Verification.
- A file moved or renamed updates every link to it (the docs link test fails otherwise).
