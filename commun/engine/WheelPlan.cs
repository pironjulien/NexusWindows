using System.Text.Json;

// Direction and amount of a wheel at a point (deltaY = -120 x scrollY notches, deltaX = +120 x scrollX notches).
// Windows convention: only the VERTICAL axis is inverted (a notch down = negative delta); horizontally, a positive
// delta = to the right (WM_MOUSEHWHEEL). Codex's review of 21/10: the horizontal direction was inverted.
readonly record struct ScrollPlan(bool Vertical, bool Forward, int Notches)
{
    // UI Automation ScrollAmount: 4 = small step forward (towards the end), 1 = small step back.
    public int Amount => Forward ? 4 : 1;

    public static ScrollPlan Of(int deltaY, int deltaX)
    {
        if ((deltaY == 0) == (deltaX == 0)) throw new BackgroundNotApplicableException("Semantic scroll needs exactly one axis");
        var vertical = deltaY != 0;
        var delta = vertical ? deltaY : deltaX;
        return new(vertical, vertical ? delta < 0 : delta > 0, Math.Clamp(Math.Abs(delta) / 120, 1, 50));
    }

    // REAL end only (Codex's review: 99.9 % of a 100,000 px content still leaves 100 px).
    public bool AtEnd(double percent) => Forward ? percent >= 100 : percent <= 0;

    // DevTools: positive deltaY = down (the opposite of Windows), positive deltaX = to the right (as in Windows).
    public static (int X, int Y) DevTools(int deltaY, int deltaX) => (deltaX, -deltaY);
}

// Choice of the scrollable container among the candidates under the point (Codex's review of 21/10): the smallest area;
// at equal area, the one that descends from all the others (the deepest); otherwise ambiguous, refused. A search beyond
// Max elements is not exhaustive: refused.
static class ScrollChoice
{
    public const int Max = 5000;

    public static void CheckCount(int count)
    {
        if (count > Max) throw new BackgroundNotApplicableException($"More than {Max} scrollable elements: search not exhaustive, nothing was sent");
    }

    // Chosen index, or -1 without a candidate. isDescendant(a, b): a strictly descends from b.
    public static int Pick(IReadOnlyList<long> areas, Func<int, int, bool> isDescendant)
    {
        if (areas.Count == 0) return -1;
        var min = areas.Min();
        var tied = Enumerable.Range(0, areas.Count).Where(i => areas[i] == min).ToList();
        var deepest = tied.Where(i => tied.All(j => j == i || isDescendant(i, j))).ToList();
        if (deepest.Count == 1) return deepest[0];
        throw new BackgroundNotApplicableException("Several scrollable containers of the same size under the point, not nested: ambiguous, nothing was sent");
    }
}
