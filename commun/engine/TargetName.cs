// Target names compared as the user reads them: a no-break space (U+00A0) or a narrow no-break space (U+202F) counts
// as an ordinary space on both sides. French UIs put one before ":" (Chrome's « Dossier : » field, 04/11): typed with a
// plain space, the name never matched ("Semantic target count is 0").
internal static class TargetName
{
    internal static string Fold(string? value) => (value ?? "").Replace(' ', ' ').Replace(' ', ' ');
    internal static bool Same(string? actual, string? wanted) => actual == wanted || Fold(actual) == Fold(wanted);
    internal static bool Contains(string? actual, string? part) => Fold(actual).Contains(Fold(part), StringComparison.OrdinalIgnoreCase);
}
