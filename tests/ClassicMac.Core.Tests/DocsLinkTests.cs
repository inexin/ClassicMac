using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassicMac.Core.Tests;

// Every relative link in the repository's Markdown resolves: the file exists, and an anchor names one of its headings
// (GitHub's slugs). Moving or renaming a document must update its links (docs/formats/CLAUDE.md).
public partial class DocsLinkTests
{
    [Fact]
    public void Relative_links_and_anchors_resolve()
    {
        string root = RepositoryRoot();
        var broken = new List<string>();
        var slugCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in MarkdownFiles(root))
        {
            string text = StripCode(File.ReadAllText(file));
            foreach (Match link in LinkPattern().Matches(text))
            {
                string target = link.Groups[1].Value.Trim();
                if (target.Length == 0 || target.Contains("://") || target.StartsWith("mailto:", StringComparison.Ordinal))
                    continue;
                if (target.Contains(' ')) target = target[..target.IndexOf(' ')]; // a link title
                int hash = target.IndexOf('#');
                string path = hash < 0 ? target : target[..hash];
                string anchor = hash < 0 ? "" : target[(hash + 1)..];
                string resolved = path.Length == 0 ? file
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(path)));
                string where = $"{Path.GetRelativePath(root, file)}: ({target})";
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{where} → missing file");
                    continue;
                }
                if (anchor.Length == 0 || !resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                if (!slugCache.TryGetValue(resolved, out var slugs))
                    slugCache[resolved] = slugs = Slugs(File.ReadAllText(resolved));
                if (!slugs.Contains(Uri.UnescapeDataString(anchor)))
                    broken.Add($"{where} → no heading #{anchor}");
                // A link whose text names a section (§6.2) points at that section's heading (#62-…).
                Match section = SectionPattern().Match(link.Groups[0].Value);
                if (section.Success && !anchor.StartsWith(section.Groups[1].Value.Replace(".", "") + "-", StringComparison.Ordinal))
                    broken.Add($"{where} → text says §{section.Groups[1].Value}");
            }
        }
        Assert.True(broken.Count == 0, "Broken links:\n" + string.Join("\n", broken));
    }

    private static IEnumerable<string> MarkdownFiles(string root) =>
        Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Where(f =>
        {
            string relative = Path.GetRelativePath(root, f).Replace('\\', '/');
            return !relative.Split('/').Any(part => part is "bin" or "obj" or ".git" or ".claude" or "node_modules");
        });

    // GitHub's heading anchors: lower case; letters, digits, spaces, hyphens and underscores kept; spaces become
    // hyphens; a repeated slug gets -1, -2, …
    private static HashSet<string> Slugs(string markdown)
    {
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match heading in HeadingPattern().Matches(StripFences(markdown)))
        {
            string title = heading.Groups[1].Value.Trim();
            title = LinkTextPattern().Replace(title, "$1");
            var slug = new StringBuilder();
            foreach (char c in title.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_') slug.Append(c);
                else if (c == ' ') slug.Append('-');
            }
            string s = slug.ToString();
            int n = counts.GetValueOrDefault(s);
            counts[s] = n + 1;
            slugs.Add(n == 0 ? s : $"{s}-{n}");
        }
        return slugs;
    }

    private static string StripFences(string markdown) => FencePattern().Replace(markdown, "");

    private static string StripCode(string markdown) => CodeSpanPattern().Replace(StripFences(markdown), "");

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [GeneratedRegex(@"(?<!\!)\[[^\]]*\]\(([^)]*)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkTextPattern();

    [GeneratedRegex(@"^[ \t]*(```|~~~).*?^[ \t]*\1[^\n]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^\[[^\]]*§(\d+(?:\.\d+)*)[^\]]*\]")]
    private static partial Regex SectionPattern();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex CodeSpanPattern();
}
