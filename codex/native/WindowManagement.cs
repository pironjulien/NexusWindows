using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Gestion des fenetres nommees. L'appelant tient le jeton et controle la pause de l'utilisateur ; Program a deja
// revalide l'application et le HWND. Une seule commande, puis des lectures : jamais de nouvelle commande sur timeout.
static class WindowManagement
{
    const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010;
    const int ShowNoActivate = 4, MinimizeNoActivate = 7, Maximize = 3;
    const int ReadbackTimeoutMs = 3000; // Meme budget que WM_CLOSE dans Program : commande asynchrone, lectures seules.
    [StructLayout(LayoutKind.Sequential)] struct Placement
    {
        public uint Length, Flags, Show;
        public Win32.Point Minimum, Maximum;
        public Win32.Rect Normal;
    }
    [DllImport("user32.dll")] static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(nint hwnd, in Placement placement);
    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern nint GetDesktopWindow();

    // Le bureau du shell reste parfois SOUS HWND_BOTTOM (banc WinForms). Seules ses identites Win32 publiques
    // sont permises : on parcourt jusqu'au bout, y compris apres le shell, sans filtrer les applications masquees.
    internal static bool AtApplicationBottom(nint hwnd, Func<nint, nint> next, nint shell, nint desktop)
    {
        var seen = new HashSet<nint> { hwnd };
        for (var cursor = next(hwnd); cursor != 0; cursor = next(cursor))
            if ((cursor != shell && cursor != desktop) || !seen.Add(cursor)) return false;
        return true;
    }

    internal readonly record struct State(bool Visible, bool Minimized, bool Maximized, bool Foreground, bool Bottom, int Left, int Top, int Width, int Height);
    internal readonly record struct Plan(string Action, int? X, int? Y, int? Width, int? Height);
    internal static Plan Prepare(JsonElement request)
    {
        string action = request.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
        if (action is not ("restore" or "minimize" or "maximize" or "bottom" or "move" or "resize" or "front"))
            throw new ActionRefusedException("Unknown window action; nothing was sent");
        if (request.TryGetProperty("foreground", out var consent) && consent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ActionRefusedException("foreground must be boolean; nothing was sent");
        int? Number(string key)
        {
            if (!request.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                throw new ActionRefusedException($"{key} must be an integer screen pixel value; nothing was sent");
            return number;
        }
        var plan = new Plan(action, Number("x"), Number("y"), Number("width"), Number("height"));
        if (action == "move" && (plan.X is null || plan.Y is null)) throw new ActionRefusedException("move requires x and y; nothing was sent");
        if (action == "resize" && (plan.Width is null || plan.Height is null || plan.Width <= 0 || plan.Height <= 0))
            throw new ActionRefusedException("resize requires positive width and height; nothing was sent");
        if (action == "front" && !(request.TryGetProperty("foreground", out var f) && f.ValueKind == JsonValueKind.True))
            throw new ActionRefusedException("front requires foreground:true to explicitly show this window; nothing was sent");
        if (action != "move" && (plan.X is not null || plan.Y is not null) || action != "resize" && (plan.Width is not null || plan.Height is not null))
            throw new ActionRefusedException("Coordinates belong to move and dimensions to resize; nothing was sent");
        return plan;
    }
    static State Read(nint hwnd)
    {
        if (!Win32.IsWindow(hwnd) || !Win32.GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Window state is unavailable");
        return new(Win32.IsWindowVisible(hwnd), Win32.IsIconic(hwnd), IsZoomed(hwnd), Win32.GetForegroundWindow() == hwnd,
            AtApplicationBottom(hwnd, h => Win32.GetWindow(h, 2 /* GW_HWNDNEXT */), GetShellWindow(), GetDesktopWindow()),
            rect.Left, rect.Top, rect.Width, rect.Height);
    }
    internal static bool Reached(Plan plan, State before, State now) => plan.Action switch
    {
        "restore" => now.Visible && !now.Minimized && !now.Maximized,
        "minimize" => now.Minimized,
        "maximize" => now.Visible && now.Maximized && !now.Minimized,
        "bottom" => now.Bottom,
        "front" => now.Visible && !now.Minimized && now.Foreground,
        "move" => now.Left == plan.X && now.Top == plan.Y && now.Width == before.Width && now.Height == before.Height,
        "resize" => now.Width == plan.Width && now.Height == plan.Height && now.Left == before.Left && now.Top == before.Top,
        _ => false,
    };
    public static object Act(nint hwnd, JsonElement request, Action started)
    {
        var previousDpi = Win32.SetThreadDpiAwarenessContext(-4 /* PER_MONITOR_AWARE_V2 */);
        try { return ActInPixels(hwnd, request, started); }
        finally { if (previousDpi != 0) Win32.SetThreadDpiAwarenessContext(previousDpi); }
    }
    static object ActInPixels(nint hwnd, JsonElement request, Action started)
    {
        var plan = Prepare(request);
        var before = Read(hwnd);
        if (!Win32.IsWindowEnabled(hwnd)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open); nothing was sent");
        if ((plan.Action is "move" or "resize") && (before.Minimized || before.Maximized))
            throw new ActionRefusedException("Restore the window before moving or resizing it; nothing was sent");
        if (Reached(plan, before, before)) return Result(plan, before, before, true, true);
        // Toute preparation qui peut echouer precede le point d'envoi, y compris la lecture du placement.
        var placement = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        if (plan.Action == "restore" && !GetWindowPlacement(hwnd, ref placement))
            throw new ActionRefusedException("Window placement is unavailable; nothing was sent");
        EmergencyStop.ThrowIfStopped();
        started();
        bool accepted;
        switch (plan.Action)
        {
            case "restore":
                placement.Show = ShowNoActivate;
                accepted = SetWindowPlacement(hwnd, placement);
                if (accepted) accepted = SetWindowPos(hwnd, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
                break;
            case "minimize": accepted = ShowWindowAsync(hwnd, MinimizeNoActivate); break;
            case "maximize": accepted = ShowWindowAsync(hwnd, Maximize); break;
            case "bottom": accepted = SetWindowPos(hwnd, 1, 0, 0, 0, 0, NoMove | NoSize | NoActivate); break;
            case "move": accepted = SetWindowPos(hwnd, 0, plan.X!.Value, plan.Y!.Value, 0, 0, NoSize | NoZOrder | NoActivate); break;
            case "resize": accepted = SetWindowPos(hwnd, 0, 0, 0, plan.Width!.Value, plan.Height!.Value, NoMove | NoZOrder | NoActivate); break;
            default:
                Physical.Activate(hwnd, () => { });
                accepted = true;
                break;
        }
        var clock = Stopwatch.StartNew();
        var now = Read(hwnd);
        while (!Reached(plan, before, now) && accepted && clock.ElapsedMilliseconds < ReadbackTimeoutMs)
        {
            EmergencyStop.ThrowIfStopped();
            Thread.Sleep(20);
            now = Read(hwnd);
        }
        return Result(plan, before, now, Reached(plan, before, now), false);
    }
    static object Result(Plan plan, State before, State now, bool reached, bool already) => new
    {
        ok = true, action = plan.Action, status = already ? "already-satisfied" : reached ? "verified" : "sent-unconfirmed",
        verified = reached, performed = !already, state = now.Minimized ? "minimized" : now.Maximized ? "maximized" : now.Visible ? "open" : "hidden",
        bounds = new { left = now.Left, top = now.Top, width = now.Width, height = now.Height },
        visible = now.Visible, minimized = now.Minimized, maximized = now.Maximized, foreground = now.Foreground, bottom = now.Bottom,
        technique = "background:win32", mechanism = plan.Action == "front" ? "SetForegroundWindow" : "window placement",
        foregroundTaken = !before.Foreground && now.Foreground,
    };

    public static int SelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        var answers = new List<object>();
        foreach (var item in input.RootElement.EnumerateArray())
        {
            try
            {
                if (item.TryGetProperty("chain", out var chain))
                {
                    var links = chain.EnumerateObject().ToDictionary(e => (nint)long.Parse(e.Name), e => (nint)e.Value.GetInt64());
                    answers.Add(new { bottom = AtApplicationBottom((nint)item.GetProperty("target").GetInt64(),
                        h => links.TryGetValue(h, out var next) ? next : 0,
                        (nint)item.GetProperty("shell").GetInt64(), (nint)item.GetProperty("desktop").GetInt64()) });
                    continue;
                }
                var plan = Prepare(item.GetProperty("request"));
                State StateOf(string key) => JsonSerializer.Deserialize<State>(item.GetProperty(key), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                answers.Add(new { reached = Reached(plan, StateOf("before"), StateOf("after")) });
            }
            catch (Exception error) { answers.Add(new { error = error.Message }); }
        }
        Console.WriteLine(JsonSerializer.Serialize(answers));
        return 0;
    }
}
