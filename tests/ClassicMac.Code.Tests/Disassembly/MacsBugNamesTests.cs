using ClassicMac.Code.Disassembly;
using static ClassicMac.Code.Tests.Disassembly.M68kDisassemblerTests;

namespace ClassicMac.Code.Tests.Disassembly;

// Hand-built procedure names in the three encodings [Doc: MacsBug Reference and Debugging Guide, "Procedure names"].
public class MacsBugNamesTests
{
    private static byte[] Code(string hex, string? text = null, string? tail = null)
    {
        var bytes = new List<byte>(Bytes(hex));
        if (text is not null) bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
        if (tail is not null) bytes.AddRange(Bytes(tail));
        return [.. bytes];
    }

    [Fact]
    public void Variable_form_after_rts()
    {
        // rts, $80|4, "Name", a pad byte to even, literal size 0.
        var names = MacsBugNames.Find(Code("4E75 84", "Name", "00 0000"));
        var name = Assert.Single(names);
        Assert.Equal(new MacsBugName(0, 2, 8, 0, "Name", MacsBugNameForm.Variable), name);
        Assert.Equal(10, name.End);
    }

    [Fact]
    public void Variable_form_without_pad_and_with_literals()
    {
        // $83 "Foo" ends at an even offset; the 4 bytes of literals follow the size word.
        var name = Assert.Single(MacsBugNames.Find(Code("4E75 83", "Foo", "0004 DEADBEEF 4E75")));
        Assert.Equal(("Foo", 6, 4), (name.Name, name.Length, name.LiteralSize));
        Assert.Equal(12, name.End);
    }

    [Fact]
    public void Variable_form_with_a_length_byte()
    {
        string text = new('A', 40);
        var name = Assert.Single(MacsBugNames.Find(Code("4E75 80 28", text, "0000")));
        Assert.Equal((text, MacsBugNameForm.Variable, 2 + 40 + 2), (name.Name, name.Form, name.Length));
    }

    [Fact]
    public void Variable_form_after_rtd_and_jmp_a0()
    {
        Assert.Equal(4, Assert.Single(MacsBugNames.Find(Code("4E74 0008 84", "Name", "00 0000"))).Offset);
        Assert.Equal(0, Assert.Single(MacsBugNames.Find(Code("4ED0 84", "Name", "00 0000"))).ReturnOffset);
    }

    [Fact]
    public void Fixed_8_form()
    {
        // "PLAYTILL" with bit 7 of the first character set.
        var name = Assert.Single(MacsBugNames.Find(Code("4E75 D0", "LAYTILL")));
        Assert.Equal(new MacsBugName(0, 2, 8, 0, "PLAYTILL", MacsBugNameForm.Fixed8), name);
    }

    [Fact]
    public void Fixed_8_form_drops_trailing_spaces()
    {
        Assert.Equal("MAIN", Assert.Single(MacsBugNames.Find(Code("4E75 CD", "AIN    "))).Name);
    }

    [Fact]
    public void Fixed_8_form_after_rtd()
    {
        var name = Assert.Single(MacsBugNames.Find(Code("4E74 0004 CD", "AIN    ")));
        Assert.Equal(new MacsBugName(0, 4, 8, 0, "MAIN", MacsBugNameForm.Fixed8), name);
    }

    // The first character's bit 7 may be clear [Doc: MacsBug Reference and Debugging Guide, "Procedure names"]; such a
    // name is upper case and the word after it does not continue the text [Fitted: Realmz 7.1.2's CODE 1, MOT32 at
    // resource offset $842E].
    [Fact]
    public void Fixed_8_form_without_bit_7()
    {
        // link a6; unlk a6; rts; "MOT32   "; link a6.
        var name = Assert.Single(MacsBugNames.Find(Bytes("4E56 0000 4E5E 4E75 4D4F 5433 3220 2020 4E56 0000")));
        Assert.Equal(new MacsBugName(6, 8, 8, 0, "MOT32", MacsBugNameForm.Fixed8), name);
        Assert.Equal(16, name.End);
    }

    [Theory]
    [InlineData("4E75", "MADTICKR", "6116 6708")]      // then bsr.s (Realmz CODE 1, $1156A)
    [InlineData("4E75", "IFILEOPE", "4E56 FFFC")]      // then link a6,#-4 (Realmz CODE 1, $838E)
    [InlineData("4E75", "_INIT   ", "4E56 0000")]      // an underscore first
    [InlineData("4E74 0008", "MAIN    ", "4E56 0000")] // after rtd
    [InlineData("4ED0", "MAIN    ", "4E50 0000")]      // after jmp (a0); link a0
    [InlineData("4E75", "MAIN    ", "")]               // the code ends
    public void Fixed_8_without_bit_7_names(string ret, string text, string tail)
    {
        var name = Assert.Single(MacsBugNames.Find(Code(ret, text, tail)));
        Assert.Equal((text.TrimEnd(' '), MacsBugNameForm.Fixed8), (name.Name, name.Form));
    }

    // Text after a return that is not a name: it goes on past 8 bytes, starts with a space or a digit, is not upper
    // case, or is spaces only [Verified: the Mac OS 9 System file (DRVR -20175, ptch -20217, PACK 11) and ResEdit].
    [Theory]
    [InlineData("Apple_Driver", "")]
    [InlineData("        ", "")]
    [InlineData(" DRVRWDEF", "")]
    [InlineData("01234567", "3839")]
    [InlineData("File_Mgr", "5F53")]
    [InlineData("prvwPICT", "5445")]
    [InlineData("JLMRTSPK", "4541")]   // upper case, but the text goes on
    [InlineData("MOT32 AB", "4E56")]   // a space inside the name
    [InlineData("mToStrin", "6700")]
    public void Fixed_8_without_bit_7_not_names(string text, string tail) =>
        Assert.Empty(MacsBugNames.Find(Code("4E75", text, tail)));

    // 16 characters, the method then the class, shown as Class.Method [Doc: MacsBug Reference and Debugging Guide,
    // "Procedure names"].
    [Fact]
    public void Fixed_16_form()
    {
        var name = Assert.Single(MacsBugNames.Find(Bytes("4E75 C4D2 4157 2020 2020 5456 6965 7720 2020")));
        Assert.Equal(new MacsBugName(0, 2, 16, 0, "TView.DRAW", MacsBugNameForm.Fixed16), name);
        Assert.Equal("ITEMS.DRAWMENU", Assert.Single(MacsBugNames.Find(Code("4E75 C4 D2", "AWMENUITEMS   "))).Name);
    }

    [Theory]
    [InlineData("4E75 84 4E61 2D65 00 0000")]   // '-' is not in the character set
    [InlineData("4E75 84 2441 4243 00 0000")]   // nor '$'
    [InlineData("4E75 80 00 0000")]             // a zero length
    [InlineData("4E71 84 4E61 6D65 00 0000")]   // after a nop, not a return
    [InlineData("4E75 C6 4142")]                // a fixed-8 name cut short
    [InlineData("4E75 C6 4142 2D44 4546 47")]   // a fixed name with '-'
    [InlineData("4E75 A0")]                     // nothing after the first character
    [InlineData("4E75 A020 2020 2020 2020")]    // a fixed-8 name of spaces
    [InlineData("4E75 A041 4243 4445 4647")]    // a fixed-8 name starting with a space
    public void Not_a_name(string hex) => Assert.Empty(MacsBugNames.Find(Bytes(hex)));

    // A word that would put the literals past the end of the code, or no word, is not a literal size: the name ends
    // at its pad byte [Fitted: Disk Copy 6.1.2, where code follows %__MAIN].
    [Theory]
    [InlineData("4E75 8346 6F6F FFFF", 4)]           // literals past the end of the code
    [InlineData("4E75 8346 6F6F 0004 AABB CC", 4)]   // by one byte
    [InlineData("4E75 84 4E61 6D65 00", 6)]          // no word after the pad
    [InlineData("4E75 83 4E61 6D", 4)]               // the name ends the code
    [InlineData("4E75 87 255F 5F4D 4149 4E 2F03 42A7 4E75", 8)]   // Disk Copy 6.1.2: move.l d3,-(sp) follows
    public void Name_without_a_literal_size(string hex, int length)
    {
        var name = Assert.Single(MacsBugNames.Find(Bytes(hex)));
        Assert.Equal((length, 0, 2 + length), (name.Length, name.LiteralSize, name.End));
    }

    [Theory]
    [InlineData("%A5Init")]
    [InlineData("TFoo.Bar")]
    [InlineData("A B")]
    public void Name_characters(string text)
    {
        string pad = (text.Length & 1) == 0 ? "00 0000" : "0000";
        var code = Code("4E75 " + (0x80 | text.Length).ToString("X2"), text, pad);
        Assert.Equal(text, Assert.Single(MacsBugNames.Find(code)).Name);
    }

    // The encoding's length and end for each length of the variable form.
    [Theory]
    [InlineData("81", 1, "0000", 4, 6)]
    [InlineData("9F", 31, "0000", 34, 36)]      // no pad byte
    [InlineData("80 20", 32, "0000", 36, 38)]
    [InlineData("80 FF", 255, "00 0000", 260, 262)]   // a pad byte after an odd end
    public void Variable_lengths(string head, int count, string tail, int length, int end)
    {
        var name = Assert.Single(MacsBugNames.Find(Code("4E75 " + head, new string('A', count), tail)));
        Assert.Equal((new string('A', count), length, end), (name.Name, name.Length, name.End));
    }

    // Code is word-aligned [Doc: M68000 Family Programmer's Reference Manual], so odd literals end at the next even
    // offset [ClassicMac].
    [Fact]
    public void Odd_literal_size()
    {
        var name = Assert.Single(MacsBugNames.Find(Bytes("4E75 8346 6F6F 0003 AABB CC 4E75")));
        Assert.Equal((3, 12), (name.LiteralSize, name.End));
        // Literals that end the code exactly are taken.
        Assert.Equal(3, Assert.Single(MacsBugNames.Find(Bytes("4E75 8346 6F6F 0003 AABB CC"))).LiteralSize);
    }

    [Fact]
    public void Read_at_an_offset()
    {
        var code = Code("4E71 84", "Name", "00 0000");
        Assert.Equal("Name", MacsBugNames.Read(code, 2, 0)?.Name);
        Assert.Null(MacsBugNames.Read(code, 0, 0));
        Assert.Null(MacsBugNames.Read(code, code.Length, 0));
    }

    [Fact]
    public void Odd_return_words_are_not_scanned()
    {
        // The return word must be at an even offset.
        Assert.Empty(MacsBugNames.Find(Code("00 4E75 84", "Name", "00 0000")));
    }

    [Fact]
    public void Several_names()
    {
        var code = Code("4E75 84", "Name", "00 0000 4E56 0000 4E75 83").Concat(Code("", "Foo", "0000")).ToArray();
        Assert.Equal(["Name", "Foo"], MacsBugNames.Find(code).Select(n => n.Name));
    }
}
