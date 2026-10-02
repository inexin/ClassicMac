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
    public void Fixed_16_form()
    {
        // Bits 7 of the first and second characters set.
        var name = Assert.Single(MacsBugNames.Find(Code("4E75 C4 D2", "AWMENUITEMS   ")));
        Assert.Equal(new MacsBugName(0, 2, 16, 0, "DRAWMENUITEMS", MacsBugNameForm.Fixed16), name);
    }

    [Theory]
    [InlineData("4E75 84 4E61 2D65 00 0000")]   // '-' is not in the character set
    [InlineData("4E75 80 00 0000")]             // a zero length
    [InlineData("4E71 84 4E61 6D65 00 0000")]   // after a nop, not a return
    [InlineData("4E75 84 4E61 6D65")]           // no literal size word
    [InlineData("4E75 C6 4142")]                // a fixed-8 name cut short
    [InlineData("4E75 C6 4142 2D44 4546 47")]   // a fixed name with '-'
    [InlineData("4E75 A0")]                     // nothing after the first character
    public void Not_a_name(string hex) => Assert.Empty(MacsBugNames.Find(Bytes(hex)));

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
