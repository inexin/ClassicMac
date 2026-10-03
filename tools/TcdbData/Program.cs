// Writes ClassicMac's shipped type and creator data (docs/formats/resources/finder.md §2.6) from a tab-delimited export
// of TCDB (Type/Creator Database by Ilan Szekely): src/ClassicMac.Resources.Decoders/Finder/tcdb.tsv.gz.
//
//   dotnet run --project tools/TcdbData -- "<TCDB export.txt>" [<output .tsv.gz>]
//
// The export is Mac OS Roman, one record a line ending in CR, the fields FileName, Type, Crea, Comments, Category,
// Extension, Dup, Source, SN with a header line first. A field may hold a CR, so a record ends at the CR after its
// eighth tab. Records from filext.com (another site's data, with no codes) are left out, and so are records whose Type
// or Crea is not four bytes. The export itself is never committed.
using System.IO.Compression;
using System.Text;
using ClassicMac.Core;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: TcdbData <TCDB export.txt> [<output .tsv.gz>]");
    return 2;
}

var output = args.Length == 2 ? args[1]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ClassicMac.Resources.Decoders", "Finder", "tcdb.tsv.gz");
var records = Records(File.ReadAllBytes(args[0])).ToList();
if (records.Count == 0 || Text(records[0][0]) != "FileName" || Text(records[0][8]) != "SN")
{
    Console.Error.WriteLine("The export does not start with the header FileName … SN.");
    return 1;
}

var text = new StringBuilder();
text.Append("# TCDB (Type/Creator Database) by Ilan Szekely, 1996–2003, https://www.lacikam.co.il/tcdb/\n");
text.Append("# The 2003.10 export, less filext.com's records and codes not four bytes; written by tools/TcdbData.\n");
text.Append("Type\tCreator\tFile name\tComments\tCategory\tExtension\n");
int kept = 0, filext = 0, odd = 0;
foreach (var record in records.Skip(1))
{
    if (Text(record[7]) == "http://filext.com/")
    {
        filext++;
        continue;
    }

    if (record[1].Length != 4 || record[2].Length != 4)
    {
        odd++;
        continue;
    }

    text.Append(Code(record[1])).Append('\t').Append(Code(record[2]));
    foreach (var field in new[] { 0, 3, 4, 5 })
    {
        text.Append('\t').Append(Text(record[field]));
    }
    text.Append('\n');
    kept++;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
using (var file = File.Create(output))
using (var zip = new GZipStream(file, CompressionLevel.SmallestSize))
{
    zip.Write(Encoding.UTF8.GetBytes(text.ToString()));
}

Console.WriteLine($"{kept} records written to {Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes); left out {filext} from filext.com and {odd} with odd codes.");
return 0;

// Records: nine fields each; the ninth (SN) ends at a CR, so CRs inside other fields stay in them.
static IEnumerable<byte[][]> Records(byte[] bytes)
{
    var fields = new List<byte[]>();
    var start = 0;
    for (var i = 0; i < bytes.Length; i++)
    {
        if (bytes[i] == '\t')
        {
            fields.Add(bytes[start..i]);
            start = i + 1;
        }
        else if (bytes[i] == '\r' && fields.Count == 8)
        {
            fields.Add(bytes[start..i]);
            yield return [.. fields];
            fields.Clear();
            start = i + 1;
        }
    }

    if (fields.Count > 0 || start < bytes.Length)
    {
        throw new InvalidDataException($"The export ends inside a record ({fields.Count} fields).");
    }
}

// A text field: Mac OS Roman, control characters (a CR inside a field) as spaces, padding trimmed.
static string Text(byte[] field)
{
    var decoded = MacRoman.Decode(field);
    return string.Concat(decoded.Select(c => c < ' ' ? ' ' : c)).Trim();
}

// A code: its bytes, printable ASCII as itself and \xHH for any other byte or a backslash; nothing trimmed.
static string Code(byte[] code) =>
    string.Concat(code.Select(b => b is >= 0x20 and <= 0x7E && b != '\\' ? ((char)b).ToString() : $"\\x{b:X2}"));
