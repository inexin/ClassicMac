using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.Ppc
{
    /// <summary>
    /// A PEF relocation opcode [Doc: Mac OS Runtime Architectures, ch. 8, "Relocation Instruction Set"]. The comment on
    /// each gives its encoding (bits from the top of the first word) and the short mnemonic.
    /// </summary>
    public enum PefRelocationOpcode
    {
        /// <summary><c>00 skip:8 count:6</c> (DDAT, RelocBySectDWithSkip): skip <c>skip</c> words, then add sectionD to <c>count</c> words.</summary>
        BySectDWithSkip,
        /// <summary><c>010 0000 n:9</c> (CODE, RelocBySectC): add sectionC to n + 1 words.</summary>
        BySectC,
        /// <summary><c>010 0001 n:9</c> (DATA, RelocBySectD): add sectionD to n + 1 words.</summary>
        BySectD,
        /// <summary><c>010 0010 n:9</c> (DESC, RelocTVector12): n + 1 times, add sectionC, add sectionD, skip a word.</summary>
        TVector12,
        /// <summary><c>010 0011 n:9</c> (DSC2, RelocTVector8): n + 1 times, add sectionC, add sectionD.</summary>
        TVector8,
        /// <summary><c>010 0100 n:9</c> (VTBL, RelocVTable8): n + 1 times, add sectionD, skip a word.</summary>
        VTable8,
        /// <summary><c>010 0101 n:9</c> (SYMR, RelocImportRun): add the next n + 1 imports, one per word.</summary>
        ImportRun,
        /// <summary><c>011 0000 i:9</c> (SYMB, RelocSmByImport): add import i; the next import is i + 1.</summary>
        ByImport,
        /// <summary><c>011 0001 i:9</c> (CDIS, RelocSmSetSectC): sectionC = section i's address.</summary>
        SetSectC,
        /// <summary><c>011 0010 i:9</c> (DTIS, RelocSmSetSectD): sectionD = section i's address.</summary>
        SetSectD,
        /// <summary><c>011 0011 i:9</c> (SECN, RelocSmBySection): add section i's address.</summary>
        BySection,
        /// <summary><c>1000 b:12</c> (DELTA, RelocIncrPosition): move on b + 1 bytes.</summary>
        IncrPosition,
        /// <summary><c>1001 c:4 r:8</c> (RPT, RelocSmRepeat): run the c + 1 instructions before it r + 1 more times.</summary>
        Repeat,
        /// <summary><c>101000 o:26</c> (LABS, RelocSetPosition, two words): move to offset o.</summary>
        SetPosition,
        /// <summary><c>101001 i:26</c> (LSYM, RelocLgByImport, two words): add import i; the next import is i + 1.</summary>
        LgByImport,
        /// <summary><c>101100 c:4 r:22</c> (LRPT, RelocLgRepeat, two words): run the c + 1 instructions before it r more times.</summary>
        LgRepeat,
        /// <summary><c>101101 0000 i:22</c> (LSEC 0, RelocLgBySection, two words): add section i's address.</summary>
        LgBySection,
        /// <summary><c>101101 0001 i:22</c> (LSEC 1, RelocLgSetSectC, two words): sectionC = section i's address.</summary>
        LgSetSectC,
        /// <summary><c>101101 0010 i:22</c> (LSEC 2, RelocLgSetSectD, two words): sectionD = section i's address.</summary>
        LgSetSectD,
    }

    /// <summary>A decoded relocation instruction.</summary>
    /// <param name="Opcode">The opcode.</param>
    /// <param name="Position">The index of its first word in the header's instructions.</param>
    /// <param name="Operand">Skip words (BySectDWithSkip), import or section index, byte offset (SetPosition), or the number of instructions a repeat runs; 0 otherwise.</param>
    /// <param name="Count">Words to relocate, run length, bytes to move (IncrPosition) or repeat count, as the instruction does them (the stored +1 applied); 0 otherwise.</param>
    public readonly record struct PefRelocationInstruction(PefRelocationOpcode Opcode, int Position, uint Operand, uint Count)
    {
        /// <summary>The short mnemonic (DDAT, CODE, DATA, DESC, DSC2, VTBL, SYMR, SYMB, CDIS, DTIS, SECN, DELTA, RPT, LABS, LSYM, LRPT, LSEC).</summary>
        public string Mnemonic => Opcode switch
        {
            PefRelocationOpcode.BySectDWithSkip => "DDAT",
            PefRelocationOpcode.BySectC => "CODE",
            PefRelocationOpcode.BySectD => "DATA",
            PefRelocationOpcode.TVector12 => "DESC",
            PefRelocationOpcode.TVector8 => "DSC2",
            PefRelocationOpcode.VTable8 => "VTBL",
            PefRelocationOpcode.ImportRun => "SYMR",
            PefRelocationOpcode.ByImport => "SYMB",
            PefRelocationOpcode.SetSectC => "CDIS",
            PefRelocationOpcode.SetSectD => "DTIS",
            PefRelocationOpcode.BySection => "SECN",
            PefRelocationOpcode.IncrPosition => "DELTA",
            PefRelocationOpcode.Repeat => "RPT",
            PefRelocationOpcode.SetPosition => "LABS",
            PefRelocationOpcode.LgByImport => "LSYM",
            PefRelocationOpcode.LgRepeat => "LRPT",
            _ => "LSEC",
        };
    }

    /// <summary>What a fixup adds: a section's address or an import's.</summary>
    public enum PefFixupTarget
    {
        /// <summary>A section's address.</summary>
        Section,
        /// <summary>An imported symbol's address.</summary>
        Import,
    }

    /// <summary>One relocated word.</summary>
    /// <param name="Section">The section the word is in.</param>
    /// <param name="Offset">The word's offset in the section (2-byte aligned in places, not always 4).</param>
    /// <param name="Opcode">The instruction that made it.</param>
    /// <param name="Target">Whether a section or an import was added.</param>
    /// <param name="TargetIndex">The section or import index.</param>
    /// <param name="Amount">The address added.</param>
    public readonly record struct PefFixup(int Section, long Offset, PefRelocationOpcode Opcode, PefFixupTarget Target,
        int TargetIndex, uint Amount);

    /// <summary>
    /// The PEF relocation machine [Doc: Mac OS Runtime Architectures, ch. 8, "Relocations"]. It starts with
    /// relocAddress at the start of the section, importIndex 0, sectionC the address of section 0 and sectionD that of
    /// section 1; "add" adds a 32-bit value to the word at relocAddress and moves on 4 bytes.
    /// </summary>
    public static class PefRelocator
    {
        /// <summary>
        /// Decodes a relocation header's instruction words. An undefined opcode or a two-word instruction cut short is
        /// reported (<c>pef.relocation-bad-opcode</c>, <c>pef.relocation-truncated</c>) and decoding stops there.
        /// </summary>
        public static IReadOnlyList<PefRelocationInstruction> Decode(IReadOnlyList<ushort> words, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(words);
            ArgumentNullException.ThrowIfNull(diagnostics);
            var result = new List<PefRelocationInstruction>(words.Count);
            for (int k = 0; k < words.Count;)
            {
                int w = words[k];
                PefRelocationInstruction? one = null;
                if (w >> 14 == 0) one = new(PefRelocationOpcode.BySectDWithSkip, k, (uint)(w >> 6) & 0xFF, (uint)w & 0x3F);
                else if (w >> 13 == 2)
                {
                    int sub = (w >> 9) & 0xF;
                    uint run = (uint)(w & 0x1FF) + 1;
                    if (sub <= 5) one = new((PefRelocationOpcode)((int)PefRelocationOpcode.BySectC + sub), k, 0, run);
                }
                else if (w >> 13 == 3)
                {
                    int sub = (w >> 9) & 0xF;
                    uint index = (uint)w & 0x1FF;
                    if (sub <= 3) one = new((PefRelocationOpcode)((int)PefRelocationOpcode.ByImport + sub), k, index, 0);
                }
                else if (w >> 12 == 8) one = new(PefRelocationOpcode.IncrPosition, k, 0, (uint)(w & 0xFFF) + 1);
                else if (w >> 12 == 9) one = new(PefRelocationOpcode.Repeat, k, (uint)((w >> 8) & 0xF) + 1, (uint)(w & 0xFF) + 1);

                if (one is { } single)
                {
                    result.Add(single);
                    k++;
                    continue;
                }

                int top = w >> 10;
                PefRelocationOpcode? opcode = top switch
                {
                    0x28 => PefRelocationOpcode.SetPosition,
                    0x29 => PefRelocationOpcode.LgByImport,
                    0x2C => PefRelocationOpcode.LgRepeat,
                    0x2D => ((w >> 6) & 0xF) switch
                    {
                        0 => PefRelocationOpcode.LgBySection,
                        1 => PefRelocationOpcode.LgSetSectC,
                        2 => PefRelocationOpcode.LgSetSectD,
                        _ => null,
                    },
                    _ => null,
                };
                if (opcode is not { } op)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-bad-opcode",
                        $"Relocation word {k} (0x{w:X4}) is not a defined instruction; relocation stopped there."));
                    break;
                }
                if (k + 1 >= words.Count)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-truncated",
                        $"Relocation word {k} (0x{w:X4}) starts a two-word instruction at the end of the list."));
                    break;
                }
                uint second = words[k + 1];
                result.Add(op switch
                {
                    PefRelocationOpcode.SetPosition or PefRelocationOpcode.LgByImport =>
                        new(op, k, ((uint)(w & 0x3FF) << 16) | second, 0),
                    PefRelocationOpcode.LgRepeat => new(op, k, (uint)((w >> 6) & 0xF) + 1, ((uint)(w & 0x3F) << 16) | second),
                    _ => new(op, k, ((uint)(w & 0x3F) << 16) | second, 0),
                });
                k += 2;
            }
            return result;
        }

        /// <summary>
        /// Runs a relocation header's instructions. With an <paramref name="image"/> (the section's
        /// <see cref="PefContainer.GetImage"/>, copied) each fixup is applied to it; without one they are only listed.
        /// Section addresses come from <paramref name="sectionAddresses"/> and import addresses from
        /// <paramref name="importAddress"/> (both 0 when null). Problems — a word outside the section, an import or
        /// section index out of range, a repeat with too few instructions before it — are reported as
        /// <c>pef.relocation-*</c> diagnostics; out-of-range words are skipped and an unrunnable repeat stops the run.
        /// </summary>
        /// <returns>Every fixup made, in order.</returns>
        public static IReadOnlyList<PefFixup> Run(PefContainer container, PefRelocationHeader header, byte[]? image,
            IReadOnlyList<uint>? sectionAddresses, Func<int, uint>? importAddress, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(container);
            ArgumentNullException.ThrowIfNull(header);
            ArgumentNullException.ThrowIfNull(diagnostics);
            var instructions = Decode(header.Instructions, diagnostics);
            int section = header.SectionIndex;
            if ((uint)section >= (uint)container.Sections.Count)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-bad-section",
                    $"A relocation header names section {section}; the container has {container.Sections.Count}."));
                return [];
            }
            long length = image is not null ? image.Length : container.Sections[section].TotalLength;
            var machine = new Machine(container, section, length, image, sectionAddresses, importAddress,
                container.Loader?.ImportedSymbols.Count ?? 0, diagnostics);

            for (int i = 0; i < instructions.Count && !machine.Stopped; i++)
            {
                var instruction = instructions[i];
                if (instruction.Opcode is PefRelocationOpcode.Repeat or PefRelocationOpcode.LgRepeat)
                {
                    int blocks = (int)instruction.Operand;
                    if (blocks > i)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-repeat-at-start",
                            $"Relocation word {instruction.Position} repeats {blocks} instructions; only {i} come before it. Relocation stopped there."));
                        break;
                    }
                    for (int j = i - blocks; j < i; j++)
                        if (instructions[j].Opcode is PefRelocationOpcode.Repeat or PefRelocationOpcode.LgRepeat)
                        {
                            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-nested-repeat",
                                $"Relocation word {instruction.Position} repeats another repeat. Relocation stopped there."));
                            machine.Stop();
                            break;
                        }
                    for (uint t = 0; t < instruction.Count && !machine.Stopped; t++)
                        for (int j = i - blocks; j < i && !machine.Stopped; j++)
                            machine.Execute(instructions[j]);
                }
                else
                {
                    machine.Execute(instruction);
                }
            }
            machine.Finish();
            return machine.Fixups;
        }

        private sealed class Machine
        {
            private readonly PefContainer container;
            private readonly int section;
            private readonly long length;
            private readonly BigEndianWriter? writer;
            private readonly BigEndianReader? reader;
            private readonly IReadOnlyList<uint>? sectionAddresses;
            private readonly Func<int, uint>? importAddress;
            private readonly int importCount;
            private readonly ICollection<Diagnostic> diagnostics;
            private readonly long maxFixups;
            private long position;
            private int importIndex;
            private int sectionC, sectionD = 1;
            private long outOfRange = -1, outOfRangeCount;

            public Machine(PefContainer container, int section, long length, byte[]? image, IReadOnlyList<uint>? sectionAddresses,
                Func<int, uint>? importAddress, int importCount, ICollection<Diagnostic> diagnostics)
            {
                this.container = container;
                this.section = section;
                this.length = length;
                this.sectionAddresses = sectionAddresses;
                this.importAddress = importAddress;
                this.importCount = importCount;
                this.diagnostics = diagnostics;
                if (image is not null)
                {
                    writer = new BigEndianWriter(image);
                    reader = new BigEndianReader(image);
                }
                // Each word is relocated about once; far more fixups than words means a runaway repeat.
                maxFixups = length / 2 + 4096;
            }

            public List<PefFixup> Fixups { get; } = [];

            public bool Stopped { get; private set; }

            public void Stop() => Stopped = true;

            private uint Address(int index) =>
                sectionAddresses is not null && (uint)index < (uint)sectionAddresses.Count ? sectionAddresses[index] : 0;

            private bool ValidSection(int index, PefRelocationInstruction instruction)
            {
                if ((uint)index < (uint)container.Sections.Count) return true;
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-bad-section",
                    $"Relocation word {instruction.Position} names section {index}; the container has {container.Sections.Count}."));
                return false;
            }

            private void Add(PefRelocationOpcode opcode, PefFixupTarget target, int index, uint amount)
            {
                if (Stopped) return;
                if (position < 0 || position > length - 4)
                {
                    if (outOfRange < 0) outOfRange = position;
                    if (++outOfRangeCount > maxFixups) Stopped = true;
                }
                else
                {
                    if (writer is not null && reader is not null)
                        writer.WriteUInt32At((int)position, unchecked(reader.ReadUInt32At((int)position) + amount));
                    Fixups.Add(new PefFixup(section, position, opcode, target, index, amount));
                    if (Fixups.Count > maxFixups)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-runaway",
                            $"Section {section}'s relocations make more fixups than it has words; relocation stopped."));
                        Stopped = true;
                    }
                }
                position += 4;
            }

            private void AddSection(PefRelocationOpcode opcode, int index) =>
                Add(opcode, PefFixupTarget.Section, index, Address(index));

            private void AddImport(PefRelocationOpcode opcode, long index, PefRelocationInstruction instruction)
            {
                if (index >= importCount)
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-bad-import",
                        $"Relocation word {instruction.Position} names import {index}; there are {importCount}."));
                    position += 4;
                    return;
                }
                Add(opcode, PefFixupTarget.Import, (int)index, importAddress?.Invoke((int)index) ?? 0);
            }

            public void Execute(PefRelocationInstruction instruction)
            {
                var op = instruction.Opcode;
                switch (op)
                {
                    case PefRelocationOpcode.BySectDWithSkip:
                        position += 4L * instruction.Operand;
                        for (uint n = 0; n < instruction.Count; n++) AddSection(op, sectionD);
                        break;
                    case PefRelocationOpcode.BySectC:
                        for (uint n = 0; n < instruction.Count; n++) AddSection(op, sectionC);
                        break;
                    case PefRelocationOpcode.BySectD:
                        for (uint n = 0; n < instruction.Count; n++) AddSection(op, sectionD);
                        break;
                    case PefRelocationOpcode.TVector12:
                        for (uint n = 0; n < instruction.Count; n++)
                        {
                            AddSection(op, sectionC);
                            AddSection(op, sectionD);
                            position += 4;
                        }
                        break;
                    case PefRelocationOpcode.TVector8:
                        for (uint n = 0; n < instruction.Count; n++)
                        {
                            AddSection(op, sectionC);
                            AddSection(op, sectionD);
                        }
                        break;
                    case PefRelocationOpcode.VTable8:
                        for (uint n = 0; n < instruction.Count; n++)
                        {
                            AddSection(op, sectionD);
                            position += 4;
                        }
                        break;
                    case PefRelocationOpcode.ImportRun:
                        for (uint n = 0; n < instruction.Count; n++) AddImport(op, importIndex++, instruction);
                        break;
                    case PefRelocationOpcode.ByImport:
                    case PefRelocationOpcode.LgByImport:
                        AddImport(op, instruction.Operand, instruction);
                        importIndex = (int)Math.Min(instruction.Operand + 1, int.MaxValue);
                        break;
                    case PefRelocationOpcode.SetSectC:
                    case PefRelocationOpcode.LgSetSectC:
                        if (ValidSection((int)instruction.Operand, instruction)) sectionC = (int)instruction.Operand;
                        break;
                    case PefRelocationOpcode.SetSectD:
                    case PefRelocationOpcode.LgSetSectD:
                        if (ValidSection((int)instruction.Operand, instruction)) sectionD = (int)instruction.Operand;
                        break;
                    case PefRelocationOpcode.BySection:
                    case PefRelocationOpcode.LgBySection:
                        if (ValidSection((int)instruction.Operand, instruction)) AddSection(op, (int)instruction.Operand);
                        else position += 4;
                        break;
                    case PefRelocationOpcode.IncrPosition:
                        position += instruction.Count;
                        break;
                    case PefRelocationOpcode.SetPosition:
                        position = instruction.Operand;
                        break;
                }
            }

            public void Finish()
            {
                if (outOfRangeCount > 0)
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "pef.relocation-out-of-range",
                        $"{outOfRangeCount} relocation(s) of section {section} fall outside its 0x{length:X} bytes (the first at 0x{outOfRange:X}); they were skipped."));
            }
        }
    }
}
