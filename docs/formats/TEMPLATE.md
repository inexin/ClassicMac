# Format name

One paragraph: what the format is, where it is found, which applications made it, and what ClassicMac does with it.

| | |
| --- | --- |
| Identified by | Type and creator codes, file extensions, the signature and where it sits |
| ClassicMac | Reads, or reads and writes; the namespace and class |
| Verified against | Each original application or sample set, one per line, or "Nothing yet" |
| Sources | Apple documentation, the code traced, other readers (behaviour only) |

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

The structures, as `Offset | Size | Field | Notes` tables, each rule tagged.

## 2. Reading

How a reader finds and decodes the data, as numbered steps; the checks, and what the Mac does with bad data.

## 3. Writing

What a writer must produce, when ClassicMac writes the format. Otherwise "None."

## 4. Variants

Versions of the format, and where Mac OS 9, the 68k ROM and Mac OS 9.2.2 differ.

## 5. ClassicMac

ClassicMac's own choices: options, limits, names, how it recovers where the Mac would fail. Tagged [ClassicMac].

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |

## 7. Verification

The fixtures and samples, their paths and licences, and which rules each proves.

## 8. Not covered

What ClassicMac does not read or write, and open questions.

## 9. References

1. Numbered references, each with its licence when it is another implementation.
