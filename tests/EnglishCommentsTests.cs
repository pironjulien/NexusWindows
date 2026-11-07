// The public code is commented in English: the C# sources of the engine, its launcher and the tests, and every versioned
// PowerShell and batch script. The comments written in French before the profiles were merged were all translated on
// 07/11/2026; a French comment in any of these files fails.
// C# comments are read by a small C# lexer (strings, verbatim, raw and character literals are skipped, so a URL or a
// French UI text in a string is never a comment), line comments and /* */ blocks alike. Script comments are read the same
// way: PowerShell # comments and <# #> blocks outside its strings and here-strings, batch rem and :: lines. Quoted words
// (« Nouveau chat », "OK") are the user's interface, not the comment's language: they are left out before the words are
// counted.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusWindows.Tests;

public sealed partial class EnglishCommentsTests
{
    [Fact]
    public void Comments_are_in_english()
    {
        var root = TestEnvironment.RepositoryRoot;
        var french = new[] { "commun/engine", "commun/launcher", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.TopDirectoryOnly))
            .Where(file => Comments(File.ReadAllText(file)).Any(IsFrench))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Empty(french);
    }

    // Versioned scripts only (git ls-files): local, excluded scripts of the working folder are not the public code.
    [Fact]
    public void Script_comments_are_in_english()
    {
        var root = TestEnvironment.RepositoryRoot;
        var scripts = VersionedFiles(root, "*.ps1", "*.cmd");
        Assert.Contains("commun/native-runtime.ps1", scripts);
        var french = scripts
            .Where(file => ScriptComments(File.ReadAllText(Path.Combine(root, file)), Path.GetExtension(file)).Any(IsFrench))
            .ToList();
        Assert.Empty(french);
    }

    [Theory]
    [InlineData("# Ferme la fenêtre", ".ps1", true)]
    [InlineData("<# Vérifie l'état du champ\n   avant la frappe #>", ".ps1", true)]
    [InlineData("$a = 1 # déjà fait", ".ps1", true)]
    [InlineData("# Close the window", ".ps1", false)]
    [InlineData("throw 'Runtime commun absent : exécuter install.ps1.' # an English comment", ".ps1", false)] // a string
    [InlineData("$s = \"la fenêtre # pour le test\" # English", ".ps1", false)]
    [InlineData("$h = @'\n# avec la fenêtre\n'@ # here-string", ".ps1", false)]
    [InlineData("Write-Host https://example.com/page#la-fenetre-des-options", ".ps1", false)] // # inside a word
    [InlineData("rem Ferme la fenêtre", ".cmd", true)]
    [InlineData(":: déjà fait pour la fenêtre", ".cmd", true)]
    [InlineData("@rem Close the window\necho la fenêtre", ".cmd", false)]
    public void French_is_found_in_script_comments_and_not_in_strings(string code, string extension, bool french)
        => Assert.Equal(french, ScriptComments(code, extension).Any(IsFrench));

    static List<string> VersionedFiles(string root, params string[] patterns)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "ls-files", "-z", "--" }.Concat(patterns)) start.ArgumentList.Add(argument);
        using var git = Process.Start(start)!;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)];
    }

    // The comments of a PowerShell (.ps1) or batch (.cmd, .bat) script, outside its strings.
    internal static IEnumerable<string> ScriptComments(string code, string extension)
    {
        var comments = new List<string>();
        if (extension is ".cmd" or ".bat")
        {
            foreach (var line in code.Split('\n'))
            {
                var text = line.Trim().TrimStart('@');
                if (text.StartsWith("::", StringComparison.Ordinal)) comments.Add(text[2..]);
                else if (text.Length >= 3 && text[..3].Equals("rem", StringComparison.OrdinalIgnoreCase) && (text.Length == 3 || char.IsWhiteSpace(text[3])))
                    comments.Add(text[3..]);
            }
            return comments;
        }
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '<' && At(code, i + 1) == '#')
            {
                var end = code.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (end < 0) end = code.Length;
                comments.Add(code[(i + 2)..end]);
                i = Math.Min(code.Length, end + 2);
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(code[i - 1]) || code[i - 1] is ';' or '(' or '{' or '}' or '|'))
            {
                var end = code.IndexOf('\n', i);
                if (end < 0) end = code.Length;
                comments.Add(code[(i + 1)..end]);
                i = end;
            }
            else if (c == '@' && At(code, i + 1) is '\'' or '"' && At(code, i + 2) is '\n' or '\r')
            {
                // Here-string: closed by its quote and @ at the start of a line.
                var close = code.IndexOf("\n" + At(code, i + 1) + "@", i + 2, StringComparison.Ordinal);
                i = close < 0 ? code.Length : close + 3;
            }
            else if (c == '\'')
            {
                var end = i + 1;
                while (end < code.Length && !(code[end] == '\'' && At(code, end + 1) != '\'')) end += code[end] == '\'' ? 2 : 1;
                i = end + 1;
            }
            else if (c == '"')
            {
                var end = i + 1;
                while (end < code.Length && !(code[end] == '"' && At(code, end + 1) != '"')) end += code[end] is '`' or '"' ? 2 : 1;
                i = end + 1;
            }
            else i++;
        }
        return comments;
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
