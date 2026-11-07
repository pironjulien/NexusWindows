// The public code is commented in English. Comments written in French before the profiles were merged are translated
// when their file changes: this list of files that still hold them may only shrink. A new French comment in any other
// file fails, and so does a listed file once it is clean (remove it from the list).
// Comments are read by a small C# lexer (strings, verbatim, raw and character literals are skipped, so a URL or a French
// UI text in a string is never a comment), line comments and /* */ blocks alike. Quoted words (« Nouveau chat », "OK")
// are the user's interface, not the comment's language: they are left out before the words are counted.
using System.Text;
using System.Text.RegularExpressions;

namespace NexusWindows.Tests;

public sealed partial class EnglishCommentsTests
{
    static readonly string[] StillFrench =
    [
        "commun/engine/Background.cs",
        "commun/engine/BusyIndicator.cs",
        "commun/engine/Capture.cs",
        "commun/engine/Cdp.cs",
        "commun/engine/ChromiumInput.cs",
        "commun/engine/Clipboard.cs",
        "commun/engine/ComUia.cs",
        "commun/engine/ComWorker.cs",
        "commun/engine/EmergencyStop.cs",
        "commun/engine/Explorer.cs",
        "commun/engine/ForegroundToken.cs",
        "commun/engine/GardeFous.cs",
        "commun/engine/Journal.cs",
        "commun/engine/Keys.cs",
        "commun/engine/Marks.cs",
        "commun/engine/MenuSearch.cs",
        "commun/engine/Ocr.cs",
        "commun/engine/Physical.cs",
        "commun/engine/Pointer.cs",
        "commun/engine/Presence.cs",
        "commun/engine/Program.cs",
        "commun/engine/Routines.cs",
        "commun/engine/Steam.cs",
        "commun/engine/TkInput.cs",
        "commun/engine/TkWheel.cs",
        "commun/engine/WebP.cs",
        "commun/engine/Wgc.cs",
        "commun/engine/WheelPlan.cs",
        "commun/engine/Win32.cs",
        "commun/engine/WindowManagement.cs",
        "commun/engine/WinRt.cs",
    ];

    [Fact]
    public void Comments_are_in_english_and_the_french_backlog_only_shrinks()
    {
        var root = TestEnvironment.RepositoryRoot;
        var french = new[] { "commun/engine", "commun/launcher", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.TopDirectoryOnly))
            .Where(file => Comments(File.ReadAllText(file)).Any(IsFrench))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Empty(french.Except(StillFrench));
        Assert.Empty(StillFrench.Except(french));
    }

    [Theory]
    [InlineData("// Ferme la fenêtre", true)] // short: one French word and an accented one
    [InlineData("// Repli", false)] // one word: never judged
    [InlineData("/* Vérifie l'état du champ\n   avant la frappe */", true)] // a block over two lines
    [InlineData("/** Le premier passage */", true)]
    [InlineData("// déjà fait", true)]
    [InlineData("// Close the window", false)]
    [InlineData("// The « Nouveau chat » button of the page", false)] // a quoted interface text
    [InlineData("// Codex's \"Que voulez-vous faire ?\" placeholder is empty", false)]
    [InlineData("// See https://learn.microsoft.com/fr-fr/windows/la/page", false)]
    [InlineData("var url = \"http://example.com/la/les/des\"; // a URL in a string", false)]
    [InlineData("var s = @\"// pour la fenêtre\"; // English", false)] // a comment marker inside a verbatim string
    [InlineData("var s = \"\"\"\n// avec la fenêtre\n\"\"\"; // raw string", false)]
    [InlineData("var c = '/'; // the word café came into English long ago", false)] // one loanword in a long English comment
    public void French_is_found_in_short_comments_and_blocks_and_not_in_strings_or_quotes(string code, bool french)
        => Assert.Equal(french, Comments(code).Any(IsFrench));

    // A comment is French when two of its words are French, or when it is short (at most six words) and holds one of
    // them: a single word, often an identifier or a product name, is never judged.
    static bool IsFrench(string comment)
    {
        var text = Quoted().Replace(comment, " ");
        text = Url().Replace(text, " ");
        var words = Word().Count(text);
        if (words < 2) return false;
        var french = FrenchWord().Count(text) + AccentedWord().Count(text);
        return french >= 2 || (words <= 6 && french >= 1);
    }

    // The comments of a C# source, each line comment or /* */ block as one text, outside every kind of literal.
    internal static IEnumerable<string> Comments(string code)
    {
        var comments = new List<string>();
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '/' && At(code, i + 1) == '/')
            {
                var end = code.IndexOf('\n', i);
                if (end < 0) end = code.Length;
                comments.Add(code[(i + 2)..end].TrimStart('/'));
                i = end;
            }
            else if (c == '/' && At(code, i + 1) == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) end = code.Length;
                comments.Add(code[(i + 2)..end].TrimStart('*'));
                i = Math.Min(code.Length, end + 2);
            }
            else if (c == '"' || ((c == '@' || c == '$') && (At(code, i + 1) == '"' || At(code, i + 1) is '@' or '$' && At(code, i + 2) == '"')))
                i = SkipString(code, i);
            else if (c == '\'')
            {
                var end = i + 1;
                while (end < code.Length && code[end] != '\'' && code[end] != '\n') end += code[end] == '\\' ? 2 : 1;
                i = end + 1;
            }
            else i++;
        }
        return comments;
    }

    static char At(string code, int index) => index < code.Length ? code[index] : '\0';

    // Skips a string literal starting at start (prefixes $ and @ included) and returns the index after it.
    static int SkipString(string code, int start)
    {
        var i = start;
        var verbatim = false;
        while (code[i] is '$' or '@') { verbatim |= code[i] == '@'; i++; }
        var quotes = 0;
        while (At(code, i + quotes) == '"') quotes++;
        if (quotes >= 3) // raw string: closed by as many quotes
        {
            var close = code.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
            return close < 0 ? code.Length : close + quotes;
        }
        if (quotes == 2) return i + 2; // ""
        i++;
        while (i < code.Length)
        {
            if (verbatim && code[i] == '"' && At(code, i + 1) == '"') { i += 2; continue; }
            if (!verbatim && code[i] == '\\') { i += 2; continue; }
            if (code[i] == '"' || (!verbatim && code[i] == '\n')) return i + 1;
            i++;
        }
        return code.Length;
    }

    [GeneratedRegex(@"«[^»]*»|""[^""\n]*""|“[^”]*”|`[^`\n]*`|(?<![\p{L}])'[^'\n]*'(?![\p{L}])")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\p{L}[\p{L}'’-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(le|la|les|des|une|est|sont|pour|avec|sans|dans|quand|seulement|apres|après|deja|déjà|fenetre|fenêtre|aux|du|ne|pas|qui|que|sur|ou|puis|ce|cette|ces|elle|au|mais|donc|leur|ses)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FrenchWord();

    // A word with a French accent, or a French elision (the article or pronoun before an apostrophe).
    [GeneratedRegex(@"\b\p{L}*[éèêëàâùûôîïç]\p{L}*\b|\b(?:l|d|qu|n|s|c|j)['’]\p{L}", RegexOptions.IgnoreCase)]
    private static partial Regex AccentedWord();
}
