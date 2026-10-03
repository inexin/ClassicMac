using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ClassicMac.Resources.Decoders.Tests;

// A tiny xlsx: [Content_Types], the workbook, shared strings and sheet1. Cells are strings (shared, or inline) or doubles
// (numbers); shared strings over three characters are written as two rich-text runs, as Excel writes formatted cells.
internal static class XlsxBuilder
{
    public static byte[] Xlsx(object?[][] rows, bool inline = false)
    {
        var shared = new List<string>();
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var at = $"{(char)('A' + c)}{r + 1}";
                switch (rows[r][c])
                {
                    case double number:
                        sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{at}\"><v>{number}</v></c>");
                        break;
                    case string text when inline:
                        sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{at}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(text)}</t></is></c>");
                        break;
                    case string text:
                        shared.Add(text);
                        sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{at}\" t=\"s\"><v>{shared.Count - 1}</v></c>");
                        break;
                }
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        var strings = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        foreach (var text in shared)
        {
            strings.Append(text.Length > 3
                ? $"<si><r><t xml:space=\"preserve\">{SecurityElement.Escape(text[..3])}</t></r><r><rPr><b/></rPr><t xml:space=\"preserve\">{SecurityElement.Escape(text[3..])}</t></r></si>"
                : $"<si><t xml:space=\"preserve\">{SecurityElement.Escape(text)}</t></si>");
        }
        strings.Append("</sst>");
        return Zip(("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>"),
            ("xl/sharedStrings.xml", strings.ToString()), ("xl/worksheets/sheet1.xml", sheet.ToString()));
    }

    public static byte[] Zip(params (string Name, string Text)[] entries)
    {
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return bytes.ToArray();
    }
}
