// The public code is commented in English. Comments written in French before the profiles were merged are translated
// when their file changes: this list of files that still hold them may only shrink. A new French comment in any other
// file fails, and so does a listed file once it is clean (remove it from the list).
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
            .Where(file => File.ReadLines(file).Any(IsFrenchComment))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Empty(french.Except(StillFrench));
        Assert.Empty(StillFrench.Except(french));
    }

    static bool IsFrenchComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        if (at < 0 || (at > 0 && line[at - 1] == ':')) return false; // a URL, not a comment
        return FrenchWord().Count(line[(at + 2)..]) >= 2;
    }

    [GeneratedRegex(@"\b(le|la|les|des|une|est|pour|avec|sans|dans|quand|seulement|apres|après|deja|déjà|fenetre|fenêtre|aux|du)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FrenchWord();
}
