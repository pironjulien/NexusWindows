using System.Net.Http;
using System.Text;
using System.Text.Json;

// Claude's registered, disposable DevTools launches only. The generic Codex wheel path remains unchanged.
static partial class Cdp
{
    public readonly record struct DomKey(string Key, string Code, int Vk, int Modifiers, string? Text);
    static readonly Dictionary<string, (string Key, string Code, int Vk)> DomNamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = ("Enter", "Enter", 13), ["return"] = ("Enter", "Enter", 13), ["tab"] = ("Tab", "Tab", 9),
        ["escape"] = ("Escape", "Escape", 27), ["esc"] = ("Escape", "Escape", 27), ["space"] = (" ", "Space", 32),
        ["backspace"] = ("Backspace", "Backspace", 8), ["delete"] = ("Delete", "Delete", 46), ["insert"] = ("Insert", "Insert", 45),
        ["home"] = ("Home", "Home", 36), ["end"] = ("End", "End", 35), ["page_up"] = ("PageUp", "PageUp", 33),
        ["page_down"] = ("PageDown", "PageDown", 34), ["left"] = ("ArrowLeft", "ArrowLeft", 37), ["up"] = ("ArrowUp", "ArrowUp", 38),
        ["right"] = ("ArrowRight", "ArrowRight", 39), ["down"] = ("ArrowDown", "ArrowDown", 40), ["apps"] = ("ContextMenu", "ContextMenu", 93),
        ["printscreen"] = ("PrintScreen", "PrintScreen", 44), ["pause"] = ("Pause", "Pause", 19), ["capslock"] = ("CapsLock", "CapsLock", 20),
        ["numlock"] = ("NumLock", "NumLock", 144), ["scrolllock"] = ("ScrollLock", "ScrollLock", 145),
        ["plus"] = ("+", "Equal", 187), ["minus"] = ("-", "Minus", 189), ["comma"] = (",", "Comma", 188), ["period"] = (".", "Period", 190),
        ["ctrl"] = ("Control", "ControlLeft", 17), ["shift"] = ("Shift", "ShiftLeft", 16),
        ["alt"] = ("Alt", "AltLeft", 18), ["win"] = ("Meta", "MetaLeft", 91),
    };
    public static void ValidateDomKey(string spec) => _ = ParseDomKey(spec);
    public static DomKey ParseDomKey(string spec)
    {
        var text = spec.Trim();
        string[] parts = text == "+" ? ["+"] : text.EndsWith("++", StringComparison.Ordinal)
            ? text[..^2].Split('+').Append("+").ToArray() : text.Split('+');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0)) throw new ActionRefusedException("Incomplete DevTools key combination");
        var mods = 0;
        foreach (var m in parts[..^1]) mods |= m.ToLowerInvariant() switch
        { "alt" => 1, "ctrl" => 2, "win" => 4, "shift" => 8, _ => throw new ActionRefusedException("Unknown DevTools key modifier: " + m) };
        var last = parts[^1]; string key, code; int vk;
        if (DomNamed.TryGetValue(last, out var named)) (key, code, vk) = named;
        else if (last.Length > 1 && (last[0] is 'F' or 'f') && int.TryParse(last[1..], out var f) && f is >= 1 and <= 24)
            (key, code, vk) = ($"F{f}", $"F{f}", 111 + f);
        else if (last.EnumerateRunes().Count() == 1)
        {
            key = last; code = ""; vk = 0;
            if (last.Length == 1 && char.IsAsciiLetter(last[0]))
            { var upper = char.ToUpperInvariant(last[0]); key = (mods & 8) != 0 ? upper.ToString() : char.ToLowerInvariant(upper).ToString(); code = "Key" + upper; vk = upper; }
            else if (last.Length == 1 && char.IsAsciiDigit(last[0])) { code = "Digit" + last; vk = last[0]; }
            else if (last == "+") { code = "Equal"; vk = 187; }
        }
        else throw new ActionRefusedException("Unknown DevTools key: " + last);
        var character = mods == 0 && key == "Enter" ? "\r" : mods == 0 && key.EnumerateRunes().Count() == 1 ? key : null;
        return new(key, code, vk, mods, character);
    }

    const string DeepActive = "let a=document.activeElement;while(a&&a.shadowRoot&&a.shadowRoot.activeElement)a=a.shadowRoot.activeElement;";
    static string Literal(object? value) => JsonSerializer.Serialize(value);
    static JsonElement ValueOf(JsonElement result)
    {
        if (result.TryGetProperty("exceptionDetails", out var ex)) throw new ActionRefusedException("DevTools DOM preparation failed: " + ex);
        if (!result.TryGetProperty("result", out var value)) throw new ActionRefusedException("DevTools DOM result missing");
        return value;
    }
    static JsonElement Function(Session session, string objectId, string body) => ValueOf(session.Call("Runtime.callFunctionOn",
        new { objectId, functionDeclaration = "function(){" + body + "}", returnByValue = true }));
    static bool True(JsonElement value) => value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
    static string? StringValue(JsonElement value) => value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    sealed record DomDocument(int[] RuntimeId, nint NativeWindow, string Name, double[] Bounds);
    static DomDocument? PickClaudeDocument(nint window, Node? target, int[]? sourceRuntime, string op, JsonElement request)
    {
        using var tree = ComUia.Shared.ReadTree(window);
        var documents = tree.Nodes.Where(n => n.Role == "document" && n.FrameworkId == "Chrome" && n.AutomationId.Equals("rootwebarea", StringComparison.OrdinalIgnoreCase)).ToList();
        documents = documents.Where(n => !n.Ancestors.Any(i => documents.Contains(tree.Nodes[i]))).ToList();
        if (documents.Count <= 1) return null; // the existing active window-title proof covers the single-view route
        var runtime = target?.RuntimeId ?? sourceRuntime;
        Node? source = runtime != null ? tree.Nodes.FirstOrDefault(n => n.RuntimeId.SequenceEqual(runtime)) : null;
        if (source == null && op == "key")
        {
            var focused = tree.Nodes.Where(n => n.Focused).ToList();
            var leaves = focused.Where(n => !focused.Any(other => other.Ancestors.Contains(n.Index))).ToList();
            if (leaves.Count == 1) source = leaves[0];
        }
        List<Node> candidates;
        if (source != null) candidates = documents.Where(n => n.RuntimeId.SequenceEqual(source.RuntimeId) || source.Ancestors.Contains(n.Index)).ToList();
        else if (request.TryGetProperty("x", out var x) && request.TryGetProperty("y", out var y))
            candidates = documents.Where(n => n.Bounds is [var left, var top, var width, var height] && x.GetInt32() >= left && x.GetInt32() < left + width && y.GetInt32() >= top && y.GetInt32() < top + height).ToList();
        else candidates = [];
        if (candidates.Count != 1) throw new ActionRefusedException("Multiview DevTools needs one exact UIA root document for the target/focus; no input sent");
        var doc = candidates[0];
        if (doc.Name.Length == 0 || doc.Offscreen || doc.Disabled) throw new ActionRefusedException("The multiview document is unnamed, hidden or disabled");
        return new(doc.RuntimeId.ToArray(), doc.NativeWindow, doc.Name, doc.Bounds.ToArray());
    }
    static string ReadDocumentTitle(nint window, DomDocument document)
    {
        using var tree = ComUia.Shared.ReadIdentity(window, document.RuntimeId, document.NativeWindow);
        var doc = tree.Nodes.FirstOrDefault(n => n.RuntimeId.SequenceEqual(document.RuntimeId));
        if (doc == null || doc.Role != "document" || doc.FrameworkId != "Chrome" || !doc.AutomationId.Equals("rootwebarea", StringComparison.OrdinalIgnoreCase) || doc.Disabled || doc.Offscreen || !doc.Bounds.SequenceEqual(document.Bounds))
            throw new ActionRefusedException("The multiview document identity or rectangle changed");
        return doc.Name;
    }
    public static int PickDocumentSurface(IReadOnlyList<Win32.Rect> surfaces, double[] bounds, double width, double height, double ratio)
    {
        if (bounds.Length != 4 || !bounds.All(double.IsFinite)) throw new ActionRefusedException("Multiview document rectangle unavailable");
        var matches = new List<int>();
        for (var i = 0; i < surfaces.Count; i++)
        {
            var r = surfaces[i]; var tolerance = Math.Ceiling(ratio);
            if (Math.Abs(bounds[0] - r.Left) > tolerance || Math.Abs(bounds[1] - r.Top) > tolerance || Math.Abs(bounds[2] - r.Width) > tolerance || Math.Abs(bounds[3] - r.Height) > tolerance) continue;
            try { PickSurface([r], r.Left, r.Top, width, height, ratio); matches.Add(i); } catch (BackgroundNotApplicableException) { }
        }
        if (matches.Count != 1) throw new ActionRefusedException("Multiview document does not match exactly one page-sized rendered surface; no input sent");
        return matches[0];
    }
    static (Session Session, Win32.Rect Surface, double Ratio, bool Multiview) OpenClaudePage(nint window, int port, int ownerPid, DomDocument? document, Action guard)
    {
        guard();
        if (!OwnsPort((uint)ownerPid, Listeners(port))) throw new ActionRefusedException("DevTools listener is no longer owned by the registered launch");
        if (!Win32.IsWindowEnabled(window) || Win32.IsIconic(window)) throw new ActionRefusedException("DevTools target is disabled or minimized; observe its dialog first");
        string url;
        using (var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) })
        using (var targets = JsonDocument.Parse(http.GetStringAsync($"http://127.0.0.1:{port}/json/list").GetAwaiter().GetResult()))
            url = PickPage(targets.RootElement, document?.Name ?? Win32.Title(window), port);
        var session = new Session(url);
        try
        {
            Prove(session.Evaluate, FreshEvaluator(url), document != null ? () => ReadDocumentTitle(window, document) : () => Win32.Title(window));
            var values = session.Evaluate("JSON.stringify([innerWidth,innerHeight,devicePixelRatio])");
            var metrics = JsonSerializer.Deserialize<double[]>(StringValue(values) ?? "null");
            var surfaces = Surfaces(window);
            if (metrics is not { Length: 3 }) throw new ActionRefusedException("Page metrics unavailable");
            var index = document != null ? PickDocumentSurface(surfaces, document.Bounds, metrics[0], metrics[1], metrics[2]) :
                PickSurface(surfaces, surfaces.Count > 0 ? surfaces[0].Left : 0, surfaces.Count > 0 ? surfaces[0].Top : 0, metrics[0], metrics[1], metrics[2]).Index;
            var surface = surfaces[index];
            guard();
            return (session, surface, metrics[2], document != null);
        }
        catch { session.Dispose(); throw; }
    }

    // The objectId is pinned on this connection, and its actual backendNodeId is read. Before each input the exact
    // object must still be connected and be the deep DOM focus; no page selector or stale UIA focus can stand in for it.
    static (string ObjectId, int BackendNodeId) PinFocus(Session session, string? focusScript, bool editable, bool secretSafeKey)
    {
        var expression = focusScript ?? "(()=>{" + DeepActive + "return a;})()";
        var result = ValueOf(session.Call("Runtime.evaluate", new { expression, returnByValue = false }));
        if (!result.TryGetProperty("objectId", out var id) || !result.TryGetProperty("subtype", out var subtype) || subtype.GetString() != "node")
            throw new ActionRefusedException("No unique DOM field/focus under the target; no input sent");
        var objectId = id.GetString()!;
        var node = session.Call("DOM.describeNode", new { objectId }).GetProperty("node");
        var backend = node.GetProperty("backendNodeId").GetInt32();
        CheckPinned(session, objectId, backend, editable, secretSafeKey);
        return (objectId, backend);
    }
    static void CheckPinned(Session session, string objectId, int backend, bool editable, bool secretSafeKey = false)
    {
        var state = Function(session, objectId, DeepActive + "return JSON.stringify({connected:this.isConnected,focused:a===this,password:this.type==='password',editable:!this.readOnly&&!this.disabled&&(this.isContentEditable||this.tagName==='INPUT'||this.tagName==='TEXTAREA')});");
        using var data = JsonDocument.Parse(StringValue(state) ?? "null");
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.GetProperty("connected").GetBoolean() || !root.GetProperty("focused").GetBoolean())
            throw new ActionRefusedException("DOM target/focus changed; no further input sent");
        if (!secretSafeKey && root.GetProperty("password").GetBoolean()) throw new ActionRefusedException("Password fields are refused; no input sent");
        if (editable && !root.GetProperty("editable").GetBoolean()) throw new ActionRefusedException("DOM target is not writable; no input sent");
        if (session.Call("DOM.describeNode", new { objectId }).GetProperty("node").GetProperty("backendNodeId").GetInt32() != backend)
            throw new ActionRefusedException("DOM backend identity changed; no further input sent");
    }
    static string FocusAt(double x, double y, bool editable) => "(()=>{" +
        "const up=n=>n.parentElement||(n.parentNode&&n.parentNode.host)||null;let hit=document.elementFromPoint(" + Literal(x) + "," + Literal(y) + ");if(!hit)return null;" +
        "while(hit.shadowRoot){const d=hit.shadowRoot.elementFromPoint(" + Literal(x) + "," + Literal(y) + ");if(!d||d===hit)break;hit=d;}" +
        "const box=n=>n.tagName==='INPUT'||n.tagName==='TEXTAREA';let el=null;for(let n=hit;n&&!el;n=up(n))if(box(n))el=n;" +
        "if(!el&&hit.isContentEditable){el=hit;for(let p=up(el);p&&p.isContentEditable;p=up(p))el=p;}" +
        (editable ? "if(!el)return null;" : "if(!el){for(let n=hit;n;n=up(n))if(n.matches&&n.matches('select,button,a[href],[tabindex]')){el=n;break;}}el=el||hit;") +
        "if(el.type==='password'||el.disabled||el.readOnly)return null;el.focus({preventScroll:true});" + DeepActive +
        "if(a!==el)return null;if(el.isContentEditable){const r=el.getRootNode(),s=(r.getSelection&&r.getSelection())||document.getSelection();if(!s||!s.anchorNode||!el.contains(s.anchorNode))return null;}return el;})()";
    const string SelectAllBody = "if(this.select)this.select();else{const r=this.getRootNode(),s=(r.getSelection&&r.getSelection())||document.getSelection();if(!s)return false;const range=document.createRange();range.selectNodeContents(this);s.removeAllRanges();s.addRange(range);}return true;";
    const string AppendBody = "if(this.setSelectionRange)this.setSelectionRange(this.value.length,this.value.length);else if(this.isContentEditable){const r=this.getRootNode(),s=(r.getSelection&&r.getSelection())||document.getSelection();if(!s)return false;const range=document.createRange();range.selectNodeContents(this);range.collapse(false);s.removeAllRanges();s.addRange(range);}return true;";
    static string SelectMutation(string option) => "if(!this.isConnected||this.disabled||this.tagName!=='SELECT')return false;const wanted=" + Literal(option.Trim().ToLowerInvariant()) + ";const hits=[...this.options].filter(o=>o.text.trim().toLowerCase()===wanted);if(hits.length!==1||hits[0].disabled||hits[0].parentElement?.disabled)return false;this.value=hits[0].value;this.dispatchEvent(new Event('input',{bubbles:true}));this.dispatchEvent(new Event('change',{bubbles:true}));return true;";

    static void DispatchDomKey(DomKey key, Action<object, Action?> send, Func<bool> sent, Action<object> cleanup, Action validate)
    {
        var releaseAttempted = false;
        var release = new { type = "keyUp", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.Vk, modifiers = key.Modifiers };
        try
        {
            send(new { type = "rawKeyDown", key = key.Key, code = key.Code, windowsVirtualKeyCode = key.Vk, modifiers = key.Modifiers }, validate);
            if (key.Text != null) send(new { type = "char", text = key.Text, key = key.Key, code = key.Code, windowsVirtualKeyCode = key.Vk, modifiers = key.Modifiers }, validate);
            releaseAttempted = true;
            send(release, null); // intentional focus transition does not prevent release
        }
        catch
        {
            // Cleanup only: the same already-started combination, never another DOWN or consumed grant.
            if (sent() && !releaseAttempted) { try { releaseAttempted = true; cleanup(release); } catch { } }
            throw;
        }
    }

    public static Outcome ClaudeInput(nint window, int port, int ownerPid, string op, Node? target, int[]? sourceRuntime, JsonElement request, Action guard, Action started, Action finish)
    {
        var priorDpi = Win32.SetThreadDpiAwarenessContext(-4);
        try { return ClaudeInputPixels(window, port, ownerPid, op, target, sourceRuntime, request, guard, started, finish); }
        finally { if (priorDpi != 0) Win32.SetThreadDpiAwarenessContext(priorDpi); }
    }
    static Outcome ClaudeInputPixels(nint window, int port, int ownerPid, string op, Node? target, int[]? sourceRuntime, JsonElement request, Action guard, Action started, Action finish)
    {
        var document = PickClaudeDocument(window, target, sourceRuntime, op, request);
        var opened = OpenClaudePage(window, port, ownerPid, document, guard);
        using var session = opened.Session;
        var surface = opened.Surface; var ratio = opened.Ratio;
        var sent = false; var prepared = false;
        string Text(string key, string fallback = "") => request.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        int Int(string key, int fallback = 0) => request.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        (double X, double Y) Point(int x, int y)
        {
            if (x < surface.Left || y < surface.Top || x >= surface.Right || y >= surface.Bottom) throw new ActionRefusedException("DOM point is outside the proven page surface; nothing sent");
            return ((x - surface.Left) / ratio, (y - surface.Top) / ratio);
        }
        (double X, double Y) TargetPoint()
        {
            if (target?.Bounds is not [var x, var y, var w, var h] || w <= 0 || h <= 0) throw new ActionRefusedException("DOM target has no current rectangle");
            var left = Math.Max(x, surface.Left); var top = Math.Max(y, surface.Top); var right = Math.Min(x + w, surface.Right); var bottom = Math.Min(y + h, surface.Bottom);
            if (right <= left || bottom <= top) throw new ActionRefusedException("DOM target is offscreen; ScrollIntoView and look again");
            return Point((int)Math.Round((left + right) / 2), (int)Math.Round((top + bottom) / 2));
        }
        void Send(string method, object parameters, Action? validate = null)
        {
            guard(); EmergencyStop.ThrowIfStopped(); Geometry(); validate?.Invoke();
            if (!sent) started(); // the contextual grant is consumed once, before the first event of this gesture
            guard(); Geometry(); validate?.Invoke();
            sent = true;
            session.Call(method, parameters);
        }
        void Geometry()
        {
            var current = Surfaces(window);
            if (!opened.Multiview && current.Count != 1 || current.Count(r => r.Left == surface.Left && r.Top == surface.Top && r.Right == surface.Right && r.Bottom == surface.Bottom) != 1)
                throw new ActionRefusedException("Proven page surface moved or changed; no further input sent");
            if (document != null) _ = ReadDocumentTitle(window, document);
            if (request.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.Array)
            {
                var expected = frame.EnumerateArray().Select(v => v.GetInt32()).ToArray(); var now = Capture.Area(window);
                if (expected.Length != 4 || now is not { } r || expected[0] != r.Left || expected[1] != r.Top || expected[2] != r.Width || expected[3] != r.Height)
                    throw new ActionRefusedException("Window frame changed since observation; no further input sent");
            }
        }
        try
        {
            if (op is "setValue" or "typeText" or "focus" || op == "key")
            {
                DomKey? key = op == "key" ? ParseDomKey(Text("key")) : null;
                var safeKey = key is { Modifiers: 0 or 8, Vk: 9 or 13 or 27 };
                var point = target != null ? TargetPoint() : ((double X, double Y)?)null;
                Geometry();
                if (point != null) { guard(); prepared = true; Program.InputStarted = true; } // DOM focus can dispatch app events
                var pinned = PinFocus(session, point is { } p ? FocusAt(p.X, p.Y, op is "setValue" or "typeText") : null, op is "setValue" or "typeText", safeKey);
                void Validate() { Geometry(); CheckPinned(session, pinned.ObjectId, pinned.BackendNodeId, op is "setValue" or "typeText", safeKey); }
                if (op == "focus") return new("background:devtools", "DOM focus (backend identity read back)", "applied");
                if (key is { } k)
                {
                    DispatchDomKey(k, (parameters, validate) => Send("Input.dispatchKeyEvent", parameters, validate), () => sent,
                        release => { guard(); session.Call("Input.dispatchKeyEvent", release); }, Validate);
                    return new("background:devtools", "Input.dispatchKeyEvent (DOM focus/backend proven)", "sent");
                }
                var text = Text(op == "setValue" ? "value" : "text");
                if (op == "setValue")
                {
                    guard(); Validate(); prepared = true; Program.InputStarted = true;
                    var selected = Function(session, pinned.ObjectId, SelectAllBody);
                    if (!True(selected)) throw new ActionRefusedException("DOM field selection could not be proved; no text sent");
                }
                else
                {
                    // Historical Claude 'type' appends; use the DOM caret, never UIA Value (which skips editor events).
                    guard(); Validate(); prepared = true; Program.InputStarted = true;
                    if (!True(Function(session, pinned.ObjectId, AppendBody))) throw new ActionRefusedException("DOM append caret could not be proved; no text sent");
                }
                if (text.Length > 0) Send("Input.insertText", new { text }, Validate);
                else if (op == "setValue")
                {
                    Send("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = "Delete", code = "Delete", windowsVirtualKeyCode = 46 }, Validate);
                    Send("Input.dispatchKeyEvent", new { type = "keyUp", key = "Delete", code = "Delete", windowsVirtualKeyCode = 46 });
                }
                return new("background:devtools", "Input.insertText (native editor input events)", "sent");
            }
            var at = target != null ? TargetPoint() : Point(Int("x"), Int("y"));
            if (op == "select")
            {
                var option = Text("option");
                var script = "(()=>{let el=document.elementFromPoint(" + Literal(at.X) + "," + Literal(at.Y) + ");while(el&&el.shadowRoot){const c=el.shadowRoot.elementFromPoint(" + Literal(at.X) + "," + Literal(at.Y) + ");if(!c||c===el)break;el=c;}if(!el||el.tagName!=='SELECT'||el.disabled)return null;const wanted=" + Literal(option.Trim().ToLowerInvariant()) + ";const hits=[...el.options].filter(o=>o.text.trim().toLowerCase()===wanted);if(hits.length!==1||hits[0].disabled)return null;return el;})()";
                var result = ValueOf(session.Call("Runtime.evaluate", new { expression = script, returnByValue = false }));
                if (!result.TryGetProperty("objectId", out var selectId)) throw new ActionRefusedException("Select option missing, disabled or duplicated; nothing sent");
                var id = selectId.GetString()!;
                guard(); Geometry(); started(); guard(); Geometry(); sent = true;
                var changed = Function(session, id, SelectMutation(option));
                if (!True(changed)) throw new InvalidOperationException("DOM select changed before assignment; inspect without replay");
                return new("background:devtools", "DOM select exact option + input/change", "sent");
            }
            var action = op == "point" ? Text("action", "click") : "click";
            if (action == "wheel")
            {
                var delta = ScrollPlan.DevTools(Int("deltaY"), Int("deltaX"));
                Send("Input.dispatchMouseEvent", new { type = "mouseWheel", x = at.X, y = at.Y, deltaX = delta.Item1, deltaY = delta.Item2 });
            }
            else if (action == "hover") Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at.X, y = at.Y });
            else if (action == "drag")
            {
                var end = Point(Int("toX"), Int("toY"));
                // An HTML5 drag is intercepted (never the system drag loop, which follows the real mouse) and dropped by
                // Input.dispatchDragEvent at the end, like Cdp.Drag.
                session.Call("Input.setInterceptDrags", new { enabled = true });
                try
                {
                    Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at.X, y = at.Y });
                    Send("Input.dispatchMouseEvent", new { type = "mousePressed", x = at.X, y = at.Y, button = "left", buttons = 1, clickCount = 1 });
                    for (var i = 1; i <= 12; i++) Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at.X + (end.X - at.X) * i / 12, y = at.Y + (end.Y - at.Y) * i / 12, button = "left", buttons = 1 });
                    session.Evaluate("0");
                    var intercepted = session.Events.FirstOrDefault(e => e.Method == "Input.dragIntercepted");
                    if (intercepted.Method is not null)
                        foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                            Send("Input.dispatchDragEvent", new { type, x = end.X, y = end.Y, data = intercepted.Params.GetProperty("data") });
                    Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x = end.X, y = end.Y, button = "left", buttons = 0, clickCount = 1 });
                }
                finally { try { session.Call("Input.setInterceptDrags", new { enabled = false }); } catch (Exception) { } }
            }
            else if (action == "click")
            {
                var button = Text("button", "left");
                if (button is not ("left" or "right" or "middle")) throw new ActionRefusedException("Invalid DOM mouse button");
                Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x = at.X, y = at.Y });
                for (var i = 1; i <= Math.Clamp(Int("count", 1), 1, 2); i++)
                {
                    Send("Input.dispatchMouseEvent", new { type = "mousePressed", x = at.X, y = at.Y, button, clickCount = i });
                    Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x = at.X, y = at.Y, button, clickCount = i });
                }
            }
            else throw new ActionRefusedException("Unknown DevTools pointer action");
            return new("background:devtools", "Input.dispatchMouseEvent " + action, "sent");
        }
        catch (Exception error) when (sent || prepared)
        { throw new AfterInputException("DevTools input interrupted after dispatch; outcome unknown, do not replay: " + error.Message, error); }
        catch (Exception error) when (error is not (ActionRefusedException or EmergencyStopException))
        { throw new ActionRefusedException("Registered DevTools route unavailable before input; no fallback sent: " + error.Message); }
        finally { finish(); }
    }
    public static int ClaudeInputSelfTest()
    {
        using var input = JsonDocument.Parse(Console.In.ReadToEnd());
        if (input.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (input.RootElement.TryGetProperty("scenario", out var scenario)) return DomKeyTraceSelfTest(scenario.GetString()!);
            if (input.RootElement.TryGetProperty("surfaces", out var surfaces))
            {
                try
                {
                    var rectangles = surfaces.EnumerateArray().Select(s => s.EnumerateArray().Select(v => v.GetInt32()).ToArray()).Select(s => new Win32.Rect { Left = s[0], Top = s[1], Right = s[2], Bottom = s[3] }).ToList();
                    var b = input.RootElement.GetProperty("bounds").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    var inner = input.RootElement.GetProperty("inner").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(new { index = PickDocumentSurface(rectangles, b, inner[0], inner[1], input.RootElement.GetProperty("ratio").GetDouble()) }));
                }
                catch (ActionRefusedException e) { Console.WriteLine(JsonSerializer.Serialize(new { error = e.Message })); }
                return 0;
            }
            if (input.RootElement.TryGetProperty("scripts", out _))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { focusEditable = FocusAt(16, 24, true), focusAny = FocusAt(16, 24, false), selectAll = SelectAllBody, append = AppendBody, select = SelectMutation("Bleu") })); return 0;
            }
        }
        var rows = new List<object>();
        foreach (var value in input.RootElement.EnumerateArray())
        { try { var key = ParseDomKey(value.GetString()!); rows.Add(new { key = key.Key, code = key.Code, vk = key.Vk, modifiers = key.Modifiers, text = key.Text }); } catch (Exception e) { rows.Add(new { error = e.Message }); } }
        Console.WriteLine(JsonSerializer.Serialize(rows)); return 0;
    }

    // Real Session/WebSocket, no GUI. Independent server records exactly the events actually received.
    static int DomKeyTraceSelfTest(string scenario)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = new Thread(() =>
        {
            try
            {
                using var client = listener.AcceptTcpClient(); using var stream = client.GetStream(); Handshake(stream);
                while (ReadFrame(stream) is { } bytes)
                {
                    using var message = JsonDocument.Parse(bytes);
                    var type = message.RootElement.GetProperty("params").GetProperty("type").GetString()!;
                    received.Add(type);
                    if (scenario == "ack-lost" && type == "rawKeyDown") { client.Client.Close(); break; }
                    WriteFrame(stream, JsonSerializer.SerializeToUtf8Bytes(new { id = message.RootElement.GetProperty("id").GetInt32(), result = new { } }));
                }
            }
            catch (Exception) { }
            finally { listener.Stop(); }
        }) { IsBackground = true }; server.Start();
        var started = 0; var dispatched = 0; var cleanups = 0; var hasSent = false; var focused = true; var blocked = false;
        string outcome;
        using (var session = new Session($"ws://127.0.0.1:{port}/devtools/page/dom-key-test"))
        {
            void Guard()
            { if (scenario == "account-before" || blocked) throw new ActionRefusedException("account/window identity guard"); }
            void Send(object parameters, Action? validate)
            {
                Guard(); validate?.Invoke();
                if (!hasSent) { started++; if (scenario == "grant-refused") throw new ActionRefusedException("contextual grant refused"); }
                Guard(); validate?.Invoke(); hasSent = true; dispatched++;
                session.Call("Input.dispatchKeyEvent", parameters);
                if (scenario == "focus-after-down") focused = false;
                if (scenario == "account-after-down") blocked = true;
            }
            try
            {
                DispatchDomKey(ParseDomKey("enter"), Send, () => hasSent, release =>
                { Guard(); cleanups++; session.Call("Input.dispatchKeyEvent", release); }, () =>
                { if (!focused) throw new ActionRefusedException("DOM focus moved after DOWN"); });
                outcome = "acknowledged";
            }
            catch (Exception) { outcome = hasSent ? "unknown" : "refused-before-input"; }
        }
        server.Join(3000);
        Console.WriteLine(JsonSerializer.Serialize(new { outcome, started, dispatched, cleanups, received, automaticReplay = false, gui = false })); return 0;
    }
}
