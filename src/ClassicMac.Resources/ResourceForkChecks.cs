using System;
using ClassicMac.Core;

namespace ClassicMac.Resources
{
    // What the Resource Manager checks when it opens a fork, transcribed from the disassembly: Mac OS 9's native
    // RM_vNewMap ($67A4) and RM_CheckMap ($6608), whose model predicts SheepShaver's result for all 78 test forks, and
    // the 68k ROM's vNewMap (FFC792CE) and CheckMap (FFC7A6FA), from code only. The reader stays tolerant and reads what
    // it can; these say whether the Mac would have opened the fork at all.
    internal static class ResourceForkChecks
    {
        // Why the modelled Resource Manager refuses the fork, as (error code, reason), or null. Error 0 means the ROM
        // opens it but reads memory past the map.
        public static (int Error, string Reason)? Rejects(ReadOnlyMemory<byte> fork, ResourceManagerModel model) =>
            model == ResourceManagerModel.Rom68k ? RomRejects(fork) : MacOS9Rejects(fork);

        private static (int, string)? MacOS9Rejects(ReadOnlyMemory<byte> fork)
        {
            long eof = fork.Length;
            if (eof < 0x46)
            {
                return (-39, $"it is {eof} bytes, under the 70 the Resource Manager reads first");
            }

            var header = new BigEndianReader(fork);
            long dO = header.ReadUInt32(), mO = header.ReadUInt32(), dL = header.ReadUInt32(), mL = header.ReadUInt32();
            if (mO < 0x28 || mO > eof - 0x1E || mL < 0x1E || mL > eof - 0x28 || mO + mL > eof)
            {
                return (-199, $"the map ({mL} bytes at {mO}) does not fit the {eof}-byte fork");
            }

            if (dO < 0x28 || dO > eof || dL > eof - 0x28 || dO + dL > eof)
            {
                return (-199, $"the data area ({dL} bytes at {dO}) does not fit the {eof}-byte fork");
            }

            if (dO > mO && dO < mO + mL)
            {
                return (-199, "the data area starts inside the map");
            }

            // CheckMap, in its order, on the map with the file's header in front.
            if (Math.Max((dO + dL) & 0xFFFFFFFF, (mO + mL) & 0xFFFFFFFF) > 0xFFFFFE)
            {
                return (-199, "the fork extends past $FFFFFE");
            }

            var map = new BigEndianReader(fork.Slice((int)mO, (int)mL));
            int mT = map.ReadUInt16At(24), mN = map.ReadUInt16At(26);
            if (mT >= mL)
            {
                return (-199, "the type list starts outside the map");
            }

            if (mN != 0xFFFF && (mN > mL || mT >= mN))
            {
                return (-199, $"the name-list offset {mN} is not after the type list inside the map");
            }

            if ((mT & 1) != 0)
            {
                return (-199, "the type-list offset is odd");
            }

            long nameListSize = mN == 0xFFFF ? 0 : mL - mN;
            long typeArea = mN == 0xFFFF ? mL - mT : mN - mT;
            if (mT + 2 > mL)
            {
                return (-199, "the type count lies outside the map");
            }

            long typesMinus1 = map.ReadInt16At(mT);
            if (((typesMinus1 * 20 + 20) & 0xFFFFFFFF) > typeArea)
            {
                return (-199, $"the type count ({typesMinus1 + 1}, signed) does not fit the type area");
            }

            // Reference counts add up as a signed word, so a count of $FFFF wraps to none.
            var p = mT + 2;
            short total = 0;
            for (var i = 0; i <= typesMinus1; i++, p += 8)
            {
                if (map.ReadUInt16At(p + 6) + 8L > typeArea)
                {
                    return (-199, $"type {i + 1}'s reference list lies outside the type area");
                }

                total = unchecked((short)(map.ReadUInt16At(p + 4) + total + 1));
            }
            if (total < 0)
            {
                return (-199, "the reference count is negative as a signed word");
            }

            long end = p + total * 12L;
            if (end > mL)
            {
                return (-199, $"{total} references do not fit the map");
            }

            // The references, walked contiguously from the end of the type list.
            for (var r = 0; r < total; r++)
            {
                var entry = p + r * 12;
                var name = map.ReadUInt16At(entry + 2);
                if (name != 0xFFFF && name >= nameListSize)
                {
                    return (-199, $"reference {r + 1}'s name offset lies outside the name list");
                }

                if ((map.ReadUInt32At(entry + 4) & 0xFFFFFF) > dL)
                {
                    return (-199, $"reference {r + 1}'s data offset lies past the data area");
                }
            }
            if (mN > 0 && mN < end)
            {
                return (-199, "the name list starts before the references end");
            }

            return null;
        }

        // The ROM reads the header as signed longs and checks almost nothing: that the header and map can be read, the
        // type-list offset is even, the offsets stay under $FFFFFF, and the references end before the names (or the
        // map's end).
        private static (int, string)? RomRejects(ReadOnlyMemory<byte> fork)
        {
            long eof = fork.Length;
            if (eof < 36)
            {
                return (-39, "its first 36 bytes cannot be read");
            }

            var header = new BigEndianReader(fork);
            long dO = header.ReadInt32(), mO = header.ReadInt32(), dL = header.ReadInt32(), mL = header.ReadInt32();
            if (mL < 12)
            {
                return (-50, "the map length is negative as a read count");
            }

            if (mO + 12 < 0)
            {
                return (-40, "the map offset is negative");
            }

            if (mO + mL > eof)
            {
                return (-39, "the map cannot be read");
            }

            if (mL < 0x1C)
            {
                return (0, "the map is shorter than its header");
            }

            var map = new BigEndianReader(fork.Slice((int)mO, (int)mL));
            var farthest = dO + dL - (mO + mL) >= 0 ? dO + dL : mO + mL;
            if (farthest >= 0xFFFFFF)
            {
                return (-199, "the fork extends to $FFFFFF or beyond");
            }

            if ((map.ReadByteAt(0x19) & 1) != 0)
            {
                return (-199, "the type-list offset is odd");
            }

            long mT = map.ReadInt16At(24);
            if (mT < 0 || mT > mL - 2)
            {
                return (0, "the type list lies outside the map");
            }

            var a1 = mT + 2;
            long typesMinus1 = map.ReadInt16At((int)mT);
            if (typesMinus1 >= 0)
            {
                var refs = 0;
                for (var i = 0; i <= typesMinus1; i++, a1 += 8)
                {
                    if (a1 + 6 > mL)
                    {
                        return (0, "the type list runs past the map");
                    }

                    refs = (refs + map.ReadUInt16At((int)(a1 + 4)) + 1) & 0xFFFF;
                }
                a1 += refs * 12L;
            }
            long mN = map.ReadInt16At(26);
            if (mN > 0)
            {
                return mN - a1 < 0 ? (-199, "the name list starts before the references end") : null;
            }

            return (a1 & 0xFFFFFF) > mL ? (-199, "the references end past the map") : null;
        }
    }
}
