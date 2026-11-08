using System.Diagnostics;
using System.Text.Json;

// No input has been sent: the session may try the next technique (physical input under the token).
sealed class BackgroundNotApplicableException(string message) : InvalidOperationException(message);
// Refused before any input (password, excluded application, changed identity, disabled command): no fallback.
class ActionRefusedException(string message) : InvalidOperationException(message);
// A guard rail that sent nothing on purpose (shared garde-fous policy, protected application, password control or
// clipboard, blind paste then submit, foreground input not permitted): journaled `refus`. Any other refusal before
// input (target not found, window gone, invalid argument, disabled element) is a failure, journaled `✗`.
sealed class GuardRailException(string message) : ActionRefusedException(message);
// A gesture with no background technique here but possible with the real mouse or keyboard (01/11): a uniform refusal
// for the three profiles, which says the foreground is needed and the AI must ask for the user's agreement.
sealed class ForegroundNeededException(string reason) : ActionRefusedException(
    $"Needs the foreground: {reason.TrimEnd('.')}. Ask the user's agreement, then repeat this step with foreground:true (real mouse and keyboard, foreground given back after); nothing was sent");

readonly record struct Outcome(string Technique, string Mechanism, string Effect);

// Choice of the technique by interface family, from the measurements of the NexusClaude bench (bench/RESULTS.md):
// - Native Win32: direct messages (BN_CLICKED, BM_SETCHECK, EM_REPLACESEL, CB/LB_SETCURSEL + notification,
//   CB_SHOWDROPDOWN, TCM_SETCURFOCUS, menu WM_COMMAND). Never UIA Invoke (the Win32 proxy calls
//   SetForegroundWindow), never SelectionItem.Select on a Win32/WinForms list (the selection changes without notifying
//   the application).
// - WinForms: UIA Invoke/Toggle (BN_CLICKED has no effect on a WinForms checkbox), messages for Edit/ComboBox/ListBox.
// - WPF, UWP/WinUI, Chromium/Electron: UIA COM with AutoSetFocus = FALSE (Invoke, Toggle, Value, SelectionItem, ExpandCollapse).
// Effect: "applied" = the state read back changed as asked; "none" = the state read back is unchanged (not a proof that
// nothing was sent); "sent" = transmitted with no read-back possible (the session's postcondition decides).
static partial class Program
{
    static readonly HashSet<string> EditClasses = new(StringComparer.Ordinal) { "edit", "richedit", "richedit20a", "richedit20w", "richedit50w", "richeditd2dpt" };
    const string Messages = "background:messages", Uia = "background:uia";

    static ProgramRunActionJson RunAction(string op, string mode, Node target, List<Node> nodes, nint window, JsonElement request)
    {
        InputStarted = true; // past this, any error may follow an input: unknown result
        var before = Win32.Probe();
        bool? takenUnderToken = null;
        string? giveBack = null;
        ChromiumInput.LastTaken = false; ChromiumInput.LastGiveBack = null;
        var outcome = mode == "foreground"
            ? ForegroundAction(op, target, window, request, out takenUnderToken, out giveBack)
            : BackgroundAction(op, target, nodes, window, request);
        // Chromium simulated activation: temporary taking and giving back of the foreground reported (review of 22/10).
        if (mode != "foreground" && (ChromiumInput.LastTaken || ChromiumInput.LastGiveBack is not null))
            (takenUnderToken, giveBack) = (ChromiumInput.LastTaken, ChromiumInput.LastGiveBack);
        // Posted messages are handled asynchronously: a short delay catches a window that activates itself.
        Thread.Sleep(30);
        var after = Win32.Probe();
        var targetPid = Win32.ProcessOf(window);
        var taken = takenUnderToken ?? (after.Foreground != before.Foreground && after.Foreground != 0 && Win32.ProcessOf(after.Foreground) == targetPid);
        if (mode != "foreground" && op == "click" && ChromiumInput.IsChromium(window)) WatchOpenedDialog(window, before.Foreground, before.LastInput);
        return new ProgramRunActionJson(
            ok: true, technique: outcome.Technique, mechanism: outcome.Mechanism, effect: outcome.Effect,
            foregroundTaken: taken, foregroundReturned: giveBack,
            cursorMoved: before.Cursor.X != after.Cursor.X || before.Cursor.Y != after.Cursor.Y,
            // Any input (the user's or injected) during the action: background techniques inject none.
            inputDuringAction: before.LastInput != after.LastInput,
            foreground: new ProgramRunActionJson2(before: (long)before.Foreground, after: (long)after.Foreground)
        );
    }

    // A click in a Chromium page that opens a window of its own (the Windows Open dialog of a file field, 05/11): Chrome's
    // file chooser activates itself as it appears, up to ~1.5 s later, and kept the user's foreground ~1.3 s (bench). For 3 s
    // after the click, without delaying the reply, a window owned by the clicked window, or of the same executable, that
    // takes the foreground is put back behind: the user's window gets it back at once, unless the user moved meanwhile.
    internal const int DialogWatchMs = 3000;
    static void WatchOpenedDialog(nint window, nint noted, uint input)
    {
        if (noted == 0 || Win32.ProcessOf(noted) == Win32.ProcessOf(window)) return; // the user was in that app: nothing to give back
        var exe = ProcessName(Win32.ProcessOf(window));
        new Thread(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DialogWatchMs)
                {
                    Thread.Sleep(10);
                    if (Win32.Probe().LastInput != input) return; // the user acted: his choice stands
                    var fg = Win32.GetForegroundWindow();
                    if (fg == 0 || fg == noted) continue;
                    if (!OpenedBy(fg, window, exe)) return; // another window took it: not ours to undo
                    ForegroundReturn.GiveBack(noted, fg, input);
                    return;
                }
            }
            catch (Exception) { } // a watcher never fails the action it follows
        }) { IsBackground = true, Name = "chromium-dialog-foreground" }.Start();
    }
    internal static bool OpenedBy(nint candidate, nint window, string exe)
    {
        for (var owner = Win32.GetWindow(candidate, Win32.GW_OWNER); owner != 0; owner = Win32.GetWindow(owner, Win32.GW_OWNER))
            if (owner == window) return true;
        return candidate == window || Win32.ProcessOf(candidate) == Win32.ProcessOf(window) ||
            exe.Length > 0 && string.Equals(ProcessName(Win32.ProcessOf(candidate)), exe, StringComparison.OrdinalIgnoreCase) && Win32.GetWindow(candidate, Win32.GW_OWNER) != 0;
    }

    // Fallback of native observations: UIA under the shared foreground token (may take the foreground). Once the token is
    // taken (a wait of up to 8 s), the user's pause is checked again just before Invoke or SetFocus; the active window
    // noted then is given back to them under the token, even if the action fails.
    static Outcome ForegroundAction(string op, Node target, nint window, JsonElement request, out bool? taken, out string? giveBack)
    {
        taken = null; giveBack = null;
        InputStarted = false; // waits and checks below come before any input (Codex's review of 24/10)
        if (op is not ("click" or "focus")) throw NothingSent("Only a semantic click or explicit focus has a foreground UI Automation path");
        if (ComUia.IsSynthetic(target.RuntimeId)) throw NothingSent("Combo box options read by messages have no UI Automation path");
        // Win32 list items served by MSAA: their default action is a double click through SendInput.
        if (target.FrameworkId == "Win32" && target.Role == "listitem") throw NothingSent("Win32 list items have no safe UI Automation path");
        var (purpose, call) = op == "focus" ? ("UIA SetFocus", "setFocus") : target.HasInvoke ? ("UIA Invoke (foreground)", "invoke")
            : target.Role is "edit" or "document" ? ("UIA SetFocus", "setFocus")
            : throw NothingSent("No foreground UI Automation path for this control");
        int? quietMs = request.TryGetProperty("quietMs", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : null;
        using var token = ForegroundToken.Acquire(purpose);
        JulienPause.ThrowIfActive(quietMs);
        EmergencyStop.ThrowIfStopped();
        var noted = Win32.GetForegroundWindow();
        var input = Win32.Probe().LastInput; // UIA injects no input: any change comes from the user or a peer
        InputStarted = true; // the UIA call may act
        try
        {
            return FirstCall(() => ComBridge.Call(new ProgramForegroundActionJson(op: call, windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow))) with { Technique = "foreground:uia" };
        }
        finally
        {
            Thread.Sleep(30); // an activation posted by the provider arrives asynchronously
            var now = Win32.GetForegroundWindow();
            taken = now != noted && now != 0 && Win32.ProcessOf(now) == Win32.ProcessOf(window);
            giveBack = ForegroundReturn.GiveBack(noted, window, input);
        }
    }

    static Outcome BackgroundAction(string op, Node target, List<Node> nodes, nint window, JsonElement request)
    {
        InputStarted = false;
        if (op == "click" && target.FrameworkId == "Chrome" && McpServer.ClaudeDevtoolsInScope(window))
        {
            var result = PostedUnderToken(window, "registered DevTools click", () =>
                McpServer.TryClaudeDevtoolsInput(window, op, target, request, () => InputStarted = true, out var clicked) ? clicked :
                    throw new ActionRefusedException("Registered DevTools click route changed; nothing sent"));
            ChromiumInput.LastTaken = result.Taken; ChromiumInput.LastGiveBack = result.GiveBack;
            return result.Outcome;
        }
        if (McpServer.TryClaudeDevtoolsInput(window, op, target, request, () => InputStarted = true, out var dom)) return dom;
        InputStarted = true;
        var own = target.NativeWindow;
        // type into a field named by the step replaces its content (McpServer.NormalizeStep): same routes, whole field.
        var replaceText = op == "typeText" && request.TryGetProperty("replace", out var replaceFlag) && replaceFlag.ValueKind == JsonValueKind.True;
        // Chromium text bound to the DOM focus, read back packet by packet (banc of 01/11: the only route that writes a rich
        // editor; the other one reported a false success). Typing takes it for the three profiles, line breaks included
        // (each Enter is checked by NativeTextSubmitScope). setValue goes to UIA SetValue, which writes a field where this
        // route failed (banc of 01/11).
        if (ChromiumInput.IsChromium(window) && !ComUia.IsSynthetic(target.RuntimeId) && ChromiumInput.TextTarget(target) && op == "typeText")
        {
            int? quiet = request.TryGetProperty("quietMs", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : null;
            InputStarted = false;
            return ChromiumInput.WriteClaude(window, target, Text(request, op == "setValue" ? "value" : "text"), op == "setValue" || replaceText, quiet, () => InputStarted = true,
                () => RevalidateClaudeTextTarget(window, target, request)).Outcome;
        }
        if (op == "click" && request.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number && count.GetInt32() == 2) return Open(window, target);
        // Chromium (Chrome, Edge, Electron): editable surfaces and <select> through simulated activation (ChromiumInput.cs,
        // shared recipe of the bench of 21/10); the rest follows the techniques below.
        // A window already in the foreground: the usual paths (UIA selectOption, etc.; review of 22/10).
        if (ChromiumInput.IsChromium(window) && !ChromiumInput.InForeground(window) && !ComUia.IsSynthetic(target.RuntimeId))
        {
            int? quiet = request.TryGetProperty("quietMs", out var qm) && qm.ValueKind == JsonValueKind.Number ? qm.GetInt32() : null;
            var editable = target.Role is "edit" or "document";
            // Chromium boundary: preconditions, token, the user's pause and STOP come before any input; "input left" only
            // at the first activation message (Codex's review of 24/10).
            void Started() => InputStarted = true;
            if (op == "click" && editable) { InputStarted = false; return ChromiumInput.Focus(window, target, quiet, Started).Outcome; }
            if (op == "typeText" && editable && !replaceText) { InputStarted = false; return ChromiumInput.Type(window, target, Text(request, "text"), quiet, Started).Outcome; }
            if (op == "select" && target.Role == "combobox") { InputStarted = false; return ChromiumInput.Select(window, target, Text(request, "option"), quiet, Started).Outcome; }
        }
        var framework = target.FrameworkId;
        var classic = framework is "Win32" or "WinForm";
        var cls = own != 0 ? Win32.BaseClass(own) : "";
        // Synthetic node of a classic combo box: the option picked by the combo box at its rank (namesakes told apart),
        // label checked; the field through Edit messages; the list itself has no action.
        if (ComUia.IsSynthetic(target.RuntimeId))
        {
            // Item of a WinForms list read through messages: picked at its rank, label read back (namesakes told apart).
            var listBox = target.Ancestors.Length > 0 && ComUia.IsClassicListBox(nodes[target.Ancestors[^1]]) ? nodes[target.Ancestors[^1]].NativeWindow : 0;
            if (listBox != 0)
            {
                if (op == "click" && target.RuntimeId[2] == ComUia.SyntheticItem) return ListBoxSelect(listBox, target.Name, target.RuntimeId[3]);
                throw NothingSent("A list item read by messages only supports a click (selection)");
            }
            var combo = AncestorWindow(target, nodes, "combobox");
            if (combo == 0) throw RefusedBeforeInput("Control identity changed; inspect again");
            if (op == "click" && target.RuntimeId[2] == ComUia.SyntheticItem) return ComboSelect(combo, target.Name, target.RuntimeId[3]);
            if (target.RuntimeId[2] == ComUia.SyntheticEdit && op is ("setValue" or "typeText") && cls == "edit")
                return EditReplace(own, Text(request, op == "setValue" ? "value" : "text"), replaceAll: op == "setValue" || replaceText);
            throw NothingSent("This part of the combo box has no action: choose an option or act on the combo box");
        }
        switch (op)
        {
            case "click":
                if (framework == "Win32" && cls == "button") return ButtonClick(own);
                // WinForms push button (drawn by the application: BS_OWNERDRAW, or BS_PUSHBUTTON): BN_CLICKED POSTED.
                // UIA Invoke is synchronous: a button that opens a modal dialog blocks it until the dialog closes (shared
                // bench of the three NexusWindows profiles, 21/10). Never for a checkbox or a radio button (role != button).
                if (framework == "WinForm" && cls == "button" && target.Role == "button" && (Win32.Style(own) & 0xF) is 0 or 1 or 0xB)
                    return ButtonClick(own);
                if (classic && target.Role == "listitem")
                {
                    var list = AncestorWindow(target, nodes, "listbox");
                    if (list != 0) return ListBoxSelect(list, target.Name);
                    // Item of the list (open or not) of a combo box: picked through the combo box itself.
                    var combo = AncestorWindow(target, nodes, "combobox");
                    if (combo != 0) return ComboSelect(combo, target.Name);
                    throw NothingSent("Classic list item outside a ListBox or ComboBox: UIA Select would be silent");
                }
                if (classic && target.Role == "tab" && own == 0)
                {
                    var tabs = AncestorWindow(target, nodes, "systabcontrol32");
                    if (tabs != 0 && target.Ancestors.Length > 0) return TabSelect(tabs, ComUia.Shared.ChildRank(nodes[target.Ancestors[^1]], target));
                }
                if (framework == "Win32")
                {
                    if (target.HasToggle) return UiaClick(window, target, invoke: false, select: false);
                    throw NothingSent("Win32 control without a silent background technique (UIA Invoke would take the foreground)");
                }
                if (target.Role == "listitem" && AncestorNode(target, nodes, "combobox") is { } container)
                {
                    // Option of an open UIA combo box: picked, then the list closed again.
                    return FirstCall(() => ComBridge.Call(new ProgramBackgroundActionJson(op: "selectItem", windowId: (long)window, runtimeId: target.RuntimeId, containerRuntimeId: container.RuntimeId)));
                }
                return UiaClick(window, target, invoke: true, select: framework != "WinForm");
            case "setValue":
            {
                var value = Text(request, "value");
                if (classic && EditClasses.Contains(cls)) return EditReplace(own, value, replaceAll: true);
                if (classic && cls == "combobox") return ComboSetValue(own, value);
                return UiaSetValue(window, target, value);
            }
            case "typeText":
                if (classic && EditClasses.Contains(cls)) return EditReplace(own, Text(request, "text"), replaceAll: replaceText);
                // Editable combo box (the « Nom du fichier » of a Windows Open dialog, 05/11): its own Edit, by messages.
                if (classic && cls == "combobox" && Win32.FindWindowExW(own, 0, "Edit", null) is var comboEdit && comboEdit != 0)
                    return EditReplace(comboEdit, Text(request, "text"), replaceAll: replaceText);
                if (!classic && framework != "Chrome" && target.Role is ("edit" or "document" or "combobox"))
                    return replaceText ? UiaSetValue(window, target, Text(request, "text")) : UiaAppendValue(window, target, nodes, Text(request, "text"));
                throw NothingSent("Background typing needs a native Edit control");
            case "focus":
                return FirstCall(() => ComBridge.Call(new ProgramForegroundActionJson(op: "takeFocus", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow)));
            case "secondary":
            {
                var action = Text(request, "action");
                if (action == "ScrollIntoView") return FirstCall(() => ComBridge.Call(new ProgramForegroundActionJson(op: "scrollIntoView", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow)));
                if (classic && cls == "combobox") return ComboDropDown(own, action);
                return UiaExpandCollapse(window, target, action);
            }
            case "select":
            {
                var option = Text(request, "option");
                if (classic && cls == "combobox") return ComboSelect(own, option);
                if (classic && cls == "listbox") return ListBoxSelect(own, option);
                if (classic) throw NothingSent("Classic list without a message technique: UIA Select would be silent");
                return FirstCall(() => ComBridge.Call(new ProgramBackgroundActionJson2(op: "selectOption", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow, option: option, combo: target.Role == "combobox")));
            }
            default: throw new InvalidOperationException("Unknown native operation");
        }
    }

    // Applicability refusals of BackgroundAction/ForegroundAction, raised before any call that can act (only RunAction's
    // Win32.Probe measurement comes before them): nothing left, InputStarted becomes false again (Codex's review of 24/10:
    // otherwise the client read them as an unknown outcome, without a fallback and with the accord consumed).
    static BackgroundNotApplicableException NothingSent(string message) { InputStarted = false; return new(message); }
    static ActionRefusedException RefusedBeforeInput(string message) { InputStarted = false; return new(message); }
    // First action of RunAction handed to the COM process: its refusal marked "before any reason" (beforeInput) comes
    // before any input of this request.
    static Outcome FirstCall(Func<Outcome> call)
    {
        try { return call(); }
        catch (BackgroundNotApplicableException error) when (error.Data["beforeInput"] is true) { throw NothingSent(error.Message); }
        catch (ActionRefusedException error) when (error.Data["beforeInput"] is true) { InputStarted = false; throw; }
        catch (EmergencyStopException error) when (error.Data["beforeInput"] is true) { InputStarted = false; throw; }
    }
    // Prior read of an auxiliary (option picked among read labels): its refusal comes before any input.
    static T PreInput<T>(Func<T> read)
    {
        try { return read(); }
        catch (BackgroundNotApplicableException error) { throw NothingSent(error.Message); }
    }

    static nint AncestorWindow(Node target, List<Node> nodes, string cls)
    {
        foreach (var index in target.Ancestors.Reverse())
        {
            var hwnd = nodes[index].NativeWindow;
            if (hwnd != 0 && Win32.BaseClass(hwnd) == cls) return hwnd;
        }
        return 0;
    }
    static Node? AncestorNode(Node target, List<Node> nodes, string role)
    {
        foreach (var index in target.Ancestors.Reverse()) if (nodes[index].Role == role) return nodes[index];
        return null;
    }
    static string Norm(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    internal static string Label(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Wanted option: a unique exact match, otherwise a unique case-insensitive match; never a partial match.
    internal static int MatchOption(IReadOnlyList<string?> items, string wanted)
    {
        var target = Label(wanted);
        var exact = Enumerable.Range(0, items.Count).Where(i => items[i] != null && Label(items[i]!) == target).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new BackgroundNotApplicableException("Option appears several times; it cannot be chosen safely");
        var loose = Enumerable.Range(0, items.Count).Where(i => items[i] != null && string.Equals(Label(items[i]!), target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (loose.Count == 1) return loose[0];
        throw new BackgroundNotApplicableException(loose.Count > 1 ? "Option is ambiguous" : "Option is absent from the list");
    }

    // ---- Messages Win32 directs ------------------------------------------------------------------------------
    static void NotifyParent(nint control, int code)
    {
        var parent = Win32.GetParent(control);
        if (parent == 0) throw new InvalidOperationException("Control without parent; outcome unknown");
        var id = Win32.GetDlgCtrlID(control) & 0xFFFF;
        Win32.Post(parent, Win32.WM_COMMAND, (code << 16) | id, control);
    }

    static Outcome ButtonClick(nint button)
    {
        if (!Win32.IsWindowEnabled(button)) throw NothingSent("Button is disabled");
        var kind = Win32.Style(button) & 0xF;
        if (kind == 7) throw NothingSent("Group box is not clickable");
        if (kind is 3 or 6) // BS_AUTOCHECKBOX, BS_AUTO3STATE: the box changes state then notifies, like a real click.
        {
            var state = Win32.Send(button, Win32.BM_GETCHECK) ?? throw NothingSent("Button does not answer");
            var next = (state + 1) % (kind == 6 ? 3 : 2);
            Win32.Send(button, Win32.BM_SETCHECK, next);
            NotifyParent(button, 0 /* BN_CLICKED */);
            return new(Messages, "BM_SETCHECK+BN_CLICKED", Win32.Send(button, Win32.BM_GETCHECK) == next ? "applied" : "sent");
        }
        if (kind == 9) // BS_AUTORADIOBUTTON: the other automatic radio buttons of the group are cleared.
        {
            var parent = Win32.GetParent(button);
            var others = new List<nint>();
            var current = button;
            for (var i = 0; i < 64; i++)
            {
                current = Win32.GetNextDlgGroupItem(parent, current, false);
                if (current == 0 || current == button) break;
                if (Win32.BaseClass(current) == "button" && (Win32.Style(current) & 0xF) == 9) others.Add(current);
            }
            foreach (var other in others) Win32.Send(other, Win32.BM_SETCHECK, 0);
            Win32.Send(button, Win32.BM_SETCHECK, 1);
            NotifyParent(button, 0);
            return new(Messages, "BM_SETCHECK+BN_CLICKED", Win32.Send(button, Win32.BM_GETCHECK) == 1 ? "applied" : "sent");
        }
        NotifyParent(button, 0);
        return new(Messages, "BN_CLICKED", "sent");
    }

    static List<string?> Items(nint control, uint countMsg, uint lengthMsg, uint textMsg)
    {
        var count = Win32.Send(control, countMsg) ?? throw NothingSent("List does not answer");
        if (count < 0 || count > 5000) throw NothingSent("List is too large for a message selection");
        var items = new List<string?>((int)count);
        for (var i = 0; i < count; i++) items.Add(Win32.SendGetText(control, lengthMsg, textMsg, i, true));
        return items;
    }
    static Outcome ListBoxSelect(nint list, string option, int? rank = null)
    {
        if ((Win32.Style(list) & (Win32.LBS_MULTIPLESEL | Win32.LBS_EXTENDEDSEL)) != 0) throw NothingSent("Multiple-selection list");
        if (!Win32.IsWindowEnabled(list)) throw NothingSent("List is disabled");
        // A list drawn by the application (LBS_OWNERDRAWFIXED/VARIABLE) without LBS_HASSTRINGS: LB_GETTEXT returns the
        // item's data, not its label; picking by label would be wrong (Codex's review, 21/10).
        var style = Win32.Style(list);
        if ((style & 0x30) != 0 && (style & 0x40 /* LBS_HASSTRINGS */) == 0) throw NothingSent("Owner-drawn list without strings: labels cannot be read by messages");
        var items = Items(list, Win32.LB_GETCOUNT, Win32.LB_GETTEXTLEN, Win32.LB_GETTEXT);
        if (rank is { } r && (r < 0 || r >= items.Count || items[r] != option)) throw RefusedBeforeInput("List items changed; inspect again");
        var index = rank ?? PreInput(() => MatchOption(items, option));
        Win32.Send(list, Win32.LB_SETCURSEL, index);
        if (Win32.Send(list, Win32.LB_GETCURSEL) != index) return new(Messages, "LB_SETCURSEL", "none");
        NotifyParent(list, 1 /* LBN_SELCHANGE */);
        return new(Messages, "LB_SETCURSEL+LBN_SELCHANGE", "applied");
    }
    // Choice in a Win32/WinForms combo box, list open or not. Mandatory order: if the list is open, CLOSE it first
    // (CB_SHOWDROPDOWN(FALSE), read back through CB_GETDROPPEDSTATE); that closing is a cancellation (CBN_SELENDCANCEL)
    // that restores the selection from before the opening: a choice made before it would be lost (bench measurement:
    // "Formule=" empty). Then CB_SETCURSEL read back, and CBN_SELCHANGE + CBN_SELENDOK to the parent, like a user's choice.
    static Outcome ComboSelect(nint combo, string option, int? rank = null)
    {
        if (!Win32.IsWindowEnabled(combo)) throw NothingSent("Combo box is disabled");
        var items = Items(combo, Win32.CB_GETCOUNT, Win32.CB_GETLBTEXTLEN, Win32.CB_GETLBTEXT);
        if (rank is { } r && (r < 0 || r >= items.Count || items[r] != option)) throw RefusedBeforeInput("Combo box options changed; inspect again");
        var index = rank ?? PreInput(() => MatchOption(items, option));
        var dropped = Win32.Send(combo, Win32.CB_GETDROPPEDSTATE) is > 0;
        var mechanism = "CB_SETCURSEL+CBN_SELCHANGE+CBN_SELENDOK";
        if (dropped)
        {
            EmergencyStop.ThrowIfStopped();
            Win32.Send(combo, Win32.CB_SHOWDROPDOWN, 0);
            if (Win32.Send(combo, Win32.CB_GETDROPPEDSTATE) is not 0) throw new InvalidOperationException("The combo box list did not close; outcome unknown");
            mechanism = "CB_SHOWDROPDOWN(FALSE)+" + mechanism;
        }
        EmergencyStop.ThrowIfStopped();
        Win32.Send(combo, Win32.CB_SETCURSEL, index);
        // List already closed again: a selection failure is not "without effect" (a fallback would target a closed list).
        if (Win32.Send(combo, Win32.CB_GETCURSEL) != index) return new(Messages, mechanism, dropped ? "sent" : "none");
        NotifyParent(combo, 1 /* CBN_SELCHANGE */);
        NotifyParent(combo, 9 /* CBN_SELENDOK */);
        return new(Messages, mechanism, "applied");
    }
    // Opening or closing the list of a Win32/WinForms combo box, read back through CB_GETDROPPEDSTATE.
    static Outcome ComboDropDown(nint combo, string action)
    {
        if (action is not ("Expand" or "Collapse")) throw NothingSent("Only Expand and Collapse are background secondary actions");
        if (!Win32.IsWindowEnabled(combo)) throw NothingSent("Combo box is disabled");
        var want = action == "Expand";
        var before = Win32.Send(combo, Win32.CB_GETDROPPEDSTATE) ?? throw NothingSent("Combo box does not answer");
        EmergencyStop.ThrowIfStopped();
        Win32.Send(combo, Win32.CB_SHOWDROPDOWN, want ? 1 : 0);
        var after = Win32.Send(combo, Win32.CB_GETDROPPEDSTATE);
        var effect = after is { } state && (state != 0) == want ? "applied" : after == before ? "none" : "sent";
        return new(Messages, want ? "CB_SHOWDROPDOWN(TRUE)" : "CB_SHOWDROPDOWN(FALSE)", effect);
    }
    static Outcome ComboSetValue(nint combo, string value)
    {
        var items = Items(combo, Win32.CB_GETCOUNT, Win32.CB_GETLBTEXTLEN, Win32.CB_GETLBTEXT);
        if (items.Count(i => i != null && Label(i) == Label(value)) == 1) return ComboSelect(combo, value);
        var edit = Win32.FindWindowExW(combo, 0, "Edit", null);
        if (edit != 0) return EditReplace(edit, value, replaceAll: true);
        throw NothingSent("Value is not one of the combo box options");
    }
    static Outcome EditReplace(nint edit, string text, bool replaceAll)
    {
        var style = Win32.Style(edit);
        if ((style & Win32.ES_PASSWORD) != 0) throw RefusedBeforeInput("Password controls require the dedicated supported workflow");
        if ((style & Win32.ES_READONLY) != 0) throw NothingSent("Read-only field");
        if (!Win32.IsWindowEnabled(edit)) throw NothingSent("Field is disabled");
        var multiline = (style & Win32.ES_MULTILINE) != 0;
        if (!multiline && (text.Contains('\n') || text.Contains('\r'))) throw NothingSent("A single-line field cannot receive line breaks in background");
        var payload = multiline ? Norm(text).Replace("\n", "\r\n") : text;
        var before = Norm(Win32.WindowText(edit));
        if (replaceAll && Win32.Send(edit, Win32.EM_SETSEL, 0, -1) is null) throw new BackgroundNotApplicableException("Field does not answer");
        // EM_REPLACESEL behaves like typing: EN_CHANGE, undo possible, text inserted at the caret.
        if (Win32.SendText(edit, Win32.EM_REPLACESEL, 1, payload) is null) throw new InvalidOperationException("Field stopped answering during input; outcome unknown");
        var after = Norm(Win32.SendGetText(edit, Win32.WM_GETTEXTLENGTH, Win32.WM_GETTEXT, 0, false) ?? before);
        var wanted = Norm(text);
        var effect = replaceAll
            ? after == wanted ? "applied" : after == before ? "none" : "sent"
            : after != before && after.Contains(wanted, StringComparison.Ordinal) ? "applied" : after == before ? "none" : "sent";
        return new(Messages, replaceAll ? "EM_SETSEL+EM_REPLACESEL" : "EM_REPLACESEL", effect);
    }
    static Outcome TabSelect(nint tabs, int index)
    {
        var count = Win32.Send(tabs, Win32.TCM_GETITEMCOUNT) ?? throw NothingSent("Tab control does not answer");
        if (index < 0 || index >= count) throw NothingSent("Tab not found in the control");
        // TCM_SETCURFOCUS picks the tab and notifies the parent (TCN_SELCHANGING/TCN_SELCHANGE), without a mouse.
        Win32.Send(tabs, Win32.TCM_SETCURFOCUS, index);
        return new(Messages, "TCM_SETCURFOCUS", Win32.Send(tabs, Win32.TCM_GETCURSEL) == index ? "applied" : "none");
    }

    // Win32 menu (HMENU) of the window: the command is posted as a menu choice (WM_COMMAND).
    // WinForms menu item named by its path (ancestor menu items then itself, observed names and automation ids), in the
    // bound window (menu bar or open context menu), invoked through UIA.
    static ProgramMenuByNamesJson4 MenuItemByPath(nint window, (string Name, string? AutomationId)[] path) => MenuByNames(window, path);

    // WinForms menu (MenuStrip, ContextMenuStrip) by the names of its path, command through UIA Invoke (ComWorker.MenuPath).
    static ProgramMenuByNamesJson4 MenuByNames(nint window, (string Name, string? AutomationId)[] segments)
    {
        var names = segments.Select(s => s.AutomationId is { Length: > 0 } id ? (object)new ProgramMenuByNamesJson(name: s.Name, automationId: id) : new ProgramMenuByNamesJson2(name: s.Name)).ToArray();
        var before = Win32.Probe();
        InputStarted = true;
        var outcome = ComBridge.Call(new ProgramMenuByNamesJson3(op: "menuPath", windowId: (long)window, runtimeId: Array.Empty<int>(), path: names));
        Thread.Sleep(30);
        var after = Win32.Probe();
        return new ProgramMenuByNamesJson4(
            ok: true, technique: outcome.Technique, mechanism: outcome.Mechanism, effect: outcome.Effect,
            foregroundTaken: after.Foreground != before.Foreground && after.Foreground != 0 && Win32.ProcessOf(after.Foreground) == Win32.ProcessOf(window),
            cursorMoved: before.Cursor.X != after.Cursor.X || before.Cursor.Y != after.Cursor.Y,
            inputDuringAction: before.LastInput != after.LastInput,
            foreground: new ProgramRunActionJson2(before: (long)before.Foreground, after: (long)after.Foreground)
        );
    }

    static ProgramMenuByNamesJson4 MenuCommand(nint window, string path)
    {
        // A disabled window (modal dialog open) accepts no command: posting it would get around the modality.
        if (!Win32.IsWindowEnabled(window)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
        var menu = Win32.GetMenu(window);
        if (menu == 0 || Win32.GetMenuItemCount(menu) <= 0)
        {
            // WinForms (MenuStrip) or WPF (Menu) menu: path by names, submenus expanded and the command through UIA Invoke
            // (ComWorker.MenuPath; WPF since 01/11: "No Win32 menu bar" on the shared bench).
            var cls = Win32.ClassName(window);
            if (!cls.StartsWith("WindowsForms10.", StringComparison.Ordinal) && !cls.StartsWith("HwndWrapper", StringComparison.Ordinal))
                throw new BackgroundNotApplicableException("No Win32 menu bar in this window");
            return MenuByNames(window, path.Split('>').Select(s => (s.Trim(), (string?)null)).ToArray());
        }
        static string Clean(string label)
        {
            var text = label.Split('\t')[0].Replace("&&", "\u0001").Replace("&", "").Replace('\u0001', '&');
            text = Label(text).TrimEnd('.', '…').Trim();
            return text;
        }
        var wanted = path.Split('>').Select(s => Clean(s)).ToArray();
        if (wanted.Length == 0 || wanted.Any(s => s.Length == 0)) throw new InvalidOperationException("Invalid menu path");
        // Chain: submenus opened to reach the command (parent menu, rank of the submenu, submenu), then the command itself
        // (the menu that holds it, rank).
        var items = new List<(string[] Path, uint Id, nint Menu, int Index, (nint Parent, int Index, nint Sub)[] Chain)>();
        void Walk(nint m, string[] prefix, int depth, (nint, int, nint)[] chain)
        {
            var count = Math.Min(Win32.GetMenuItemCount(m), 256);
            for (var i = 0; i < count; i++)
            {
                var state = Win32.GetMenuState(m, (uint)i, 0x400);
                if ((state & 0x800) != 0) continue; // separateur
                var label = Clean(Win32.MenuLabel(m, i));
                if (label.Length == 0) continue;
                var current = prefix.Append(label).ToArray();
                var sub = Win32.GetSubMenu(m, i);
                if (sub != 0) { if (depth < 6) Walk(sub, current, depth + 1, [.. chain, (m, i, sub)]); continue; }
                var id = Win32.GetMenuItemID(m, i);
                if (id != 0 && id != 0xFFFFFFFF) items.Add((current, id, m, i, chain));
            }
        }
        Walk(menu, [], 0, []);
        bool Same(string[] a, string[] b) => a.Length == b.Length && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
        var found = items.Where(i => Same(i.Path, wanted)).ToList();
        if (found.Count == 0) found = items.Where(i => i.Path.Length >= wanted.Length && Same(i.Path[^wanted.Length..], wanted)).ToList();
        if (found.Count == 0) throw new BackgroundNotApplicableException("Menu command not found in the Win32 menu bar");
        if (found.Count > 1) throw new BackgroundNotApplicableException("Menu path is ambiguous; give the full path");
        var item = found[0];
        // A command's greyed state is reliable only after its menu opens: the application updates it while handling
        // WM_INITMENU then WM_INITMENUPOPUP (MFC, WinForms MainMenu, Win32). These messages are sent to it as by a real
        // opening, without showing anything; without an answer, the state is "not verifiable" and nothing is posted.
        string Unverifiable(string why) => $"Menu command state cannot be verified ({why}); nothing was sent";
        if (Win32.Send(window, Win32.WM_INITMENU, menu) is null) throw new ActionRefusedException(Unverifiable("the application did not answer WM_INITMENU"));
        var opened = new List<nint>();
        try
        {
            foreach (var (_, index, sub) in item.Chain)
            {
                if (Win32.Send(window, Win32.WM_INITMENUPOPUP, sub, index & 0xFFFF) is null) throw new ActionRefusedException(Unverifiable("the application did not answer WM_INITMENUPOPUP"));
                opened.Add(sub);
            }
            // Read back after initialization: the command must have stayed in its place (dynamic menus rebuilt).
            if (Win32.GetMenuItemID(item.Menu, item.Index) != item.Id || Clean(Win32.MenuLabel(item.Menu, item.Index)) != item.Path[^1])
                throw new ActionRefusedException(Unverifiable("the menu changed while it was initialized; inspect again"));
            var state = Win32.GetMenuState(item.Menu, (uint)item.Index, 0x400);
            if (state == 0xFFFFFFFF) throw new ActionRefusedException(Unverifiable("the command is no longer in its menu"));
            if ((state & 0x3) != 0) throw new ActionRefusedException("Menu command is disabled");
        }
        finally
        {
            // Symmetric closing, as when a real drop-down menu is destroyed (no effect if the application ignores it).
            for (var i = opened.Count - 1; i >= 0; i--) Win32.Send(window, Win32.WM_UNINITMENUPOPUP, opened[i]);
        }
        EmergencyStop.ThrowIfStopped();
        var before = Win32.Probe();
        InputStarted = true;
        Win32.Post(window, Win32.WM_COMMAND, (nint)(item.Id & 0xFFFF), 0);
        Thread.Sleep(30);
        var after = Win32.Probe();
        return new ProgramMenuByNamesJson4(
            ok: true, technique: Messages, mechanism: "WM_COMMAND", effect: "sent",
            foregroundTaken: after.Foreground != before.Foreground && after.Foreground != 0 && Win32.ProcessOf(after.Foreground) == Win32.ProcessOf(window),
            cursorMoved: before.Cursor.X != after.Cursor.X || before.Cursor.Y != after.Cursor.Y,
            inputDuringAction: before.LastInput != after.LastInput,
            foreground: new ProgramRunActionJson2(before: (long)before.Foreground, after: (long)after.Foreground)
        );
    }

    // After a posted gesture: an application may activate itself while handling it (Electron on a click, WinForms on a
    // double click: shared bench of 21/10, foreground taken for 1 to 2 s and never given back). The whole span of note /
    // emit / watch / give back runs UNDER the shared foreground token (Codex's review: without it, a peer that activated
    // the target during the watch had the foreground taken back in the middle of its input). The foreground is given back
    // only if nobody touched the keyboard or mouse since the note: a deliberate click by the user, even in the target
    // application, is kept ("kept-by-julien").
    static (Outcome Outcome, bool Taken, string? GiveBack, (nint Foreground, Win32.Point Cursor, uint LastInput) Before) PostedUnderToken(nint window, string purpose, Func<Outcome> send)
    {
        using var token = ForegroundToken.Acquire(purpose);
        EmergencyStop.ThrowIfStopped();
        var before = Win32.Probe();
        Outcome outcome;
        try { outcome = send(); }
        catch when (InputStarted)
        {
            // Partial input (control destroyed between DOWN and UP...): the target may already have activated. Watch and
            // give back anyway, under the token, then the original error goes up as is, without a replay.
            Watch();
            throw;
        }
        var pid = Win32.ProcessOf(window);
        var clock = Stopwatch.StartNew();
        var taken = false;
        while (clock.ElapsedMilliseconds < 300)
        {
            var fg = Win32.GetForegroundWindow();
            if (fg != 0 && fg != before.Foreground && Win32.ProcessOf(fg) == pid) { taken = true; break; }
            Thread.Sleep(10);
        }
        if (!taken) return (outcome, false, null, before);
        return (outcome, true, ForegroundReturn.GiveBack(before.Foreground, window, before.LastInput), before);

        void Watch()
        {
            var target = Win32.ProcessOf(window);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 300)
            {
                var fg = Win32.GetForegroundWindow();
                if (fg != 0 && fg != before.Foreground && Win32.ProcessOf(fg) == target) { ForegroundReturn.GiveBack(before.Foreground, window, before.LastInput); return; }
                Thread.Sleep(10);
            }
        }
    }

    // Click or wheel at a point of a native capture (Pointer.cs): the same report as the other actions.
    static ProgramPointActionJson PointAction(nint window, JsonElement request)
    {
        int Int(string key) => request.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var frame = request.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(e => e.GetInt32()).ToArray() : [];
        var action = Text(request, "action", "click");
        var button = Text(request, "button", "left");
        // Right click: menus open BEFORE, to read the one THIS click opens (Codex's plan of 22/10).
        var rightClick = action == "click" && button == "right";
        var menusBefore = rightClick ? Win32.LinkedMenuPopups(window).ToHashSet() : [];
        Pointer.LastTrace = null;
        var (outcome, taken, giveBack, before) = PostedUnderToken(window, "posted pointer (may activate the target)", () =>
            McpServer.TryClaudeDevtoolsInput(window, "point", null, request, () => InputStarted = true, out var dom) ? dom :
                Pointer.Act(window, action, Int("x"), Int("y"), frame, button,
                    Math.Max(1, Int("count")), Int("deltaY"), Int("deltaX"), () => InputStarted = true, Int("toX"), Int("toY")));
        var after = Win32.Probe();
        return new ProgramPointActionJson(
            ok: true, technique: outcome.Technique, mechanism: outcome.Mechanism, effect: outcome.Effect,
            foregroundTaken: taken, foregroundReturned: giveBack, trace: Pointer.LastTrace,
            contextMenu: rightClick ? OpenedMenu(window, menusBefore) : null,
            cursorMoved: before.Cursor.X != after.Cursor.X || before.Cursor.Y != after.Cursor.Y,
            inputDuringAction: before.LastInput != after.LastInput,
            foreground: new ProgramRunActionJson2(before: (long)before.Foreground, after: (long)after.Foreground)
        );
    }

    // Context menu opened by ONE right click: a WinForms popup bound to the window (same process, menu role, same thread
    // or owned), absent before the click; read again until it appears (1 s at most), never a second click. Its commands
    // are then resolved in THIS popup (scope of the next observation, MenuSearch.Scope).
    internal static object? OpenedMenu(nint window, HashSet<nint> before)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var opened = Win32.LinkedMenuPopups(window).Where(h => !before.Contains(h)).ToList();
            if (opened.Count == 1)
            {
                var popup = opened[0];
                return new ProgramOpenedMenuJson(id: (long)popup, pid: Win32.ProcessOf(popup), owner: (long)Win32.GetWindow(popup, Win32.GW_OWNER), className: Win32.ClassName(popup));
            }
            if (opened.Count > 1) return new ProgramOpenedMenuJson2(ambiguous: opened.Select(h => (long)h).ToArray());
            if (clock.ElapsedMilliseconds >= 1000) return null;
            Thread.Sleep(20);
        }
    }

    // Key or shortcut in the background (Keys.cs): the same report as the other actions.
    static object KeyAction(nint window, string key, JsonElement request)
    {
        if (!Win32.IsWindowEnabled(window)) throw new ActionRefusedException("Window is disabled (a modal dialog may be open)");
        if (McpServer.ClaudeDevtoolsInScope(window))
        {
            InputStarted = false;
            if (!McpServer.TryClaudeDevtoolsInput(window, "key", null, request, () => InputStarted = true, out var dom))
                throw new ActionRefusedException("Registered DevTools key transport unavailable; no input sent");
            return new ProgramKeyActionJson(ok: true, technique: dom.Technique, mechanism: dom.Mechanism, effect: dom.Effect);
        }
        (Outcome, bool, string?, (nint Foreground, Win32.Point Cursor, uint LastInput)) Posted()
        {
            // Inactive Chromium: simulated activation (shared token and the user's pause taken by ChromiumInput).
            if (!ChromiumInput.IsChromium(window) || ChromiumInput.InForeground(window))
                return PostedUnderToken(window, "posted keys (may activate the target)", () => Keys.Send(window, key, () => InputStarted = true));
            int? quiet = request.TryGetProperty("quietMs", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : null;
            var probe = Win32.Probe();
            InputStarted = false;
            var r = ChromiumInput.SendKeys(window, key, quiet, () => InputStarted = true);
            return (r.Outcome, r.Taken, r.GiveBack, probe);
        }
        var (outcome, taken, giveBack, before) = Posted();
        var after = Win32.Probe();
        return new ProgramRunActionJson(
            ok: true, technique: outcome.Technique, mechanism: outcome.Mechanism, effect: outcome.Effect,
            foregroundTaken: taken, foregroundReturned: giveBack,
            cursorMoved: before.Cursor.X != after.Cursor.X || before.Cursor.Y != after.Cursor.Y,
            inputDuringAction: before.LastInput != after.LastInput,
            foreground: new ProgramRunActionJson2(before: (long)before.Foreground, after: (long)after.Foreground)
        );
    }

    // ---- UI Automation COM, AutoSetFocus = FALSE, in the --com-worker child process ----
    // Measurement (WPF and WinForms .NET 8 fixture, 1.3.0): once System.Windows.Automation had been used in a process (it
    // registers its client-side providers there for the whole process), a COM Invoke or Toggle from the same process
    // activated the target window despite AutoSetFocus = FALSE. Since 1.4.0 the managed client is no longer loaded at all
    // (tree read through COM); the patterns stay in the --com-worker child process (ComWorker.cs), isolated and time-bounded.
    static Outcome UiaClick(nint window, Node target, bool invoke, bool select)
    {
        var cef = target.FrameworkId == "Chrome" && ChromiumInput.IsCefHost(window);
        var order = ClickOrder(target.Role, invoke, select, cef);
        return FirstCall(() => ComBridge.Call(new ProgramUiaClickJson(op: "click", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow, order: order)));
    }
    internal static string[] ClickOrder(string role, bool invoke, bool select, bool cef)
    {
        // CEF/Steam: SelectionItem may change the accessible state without triggering the React tab. A click is Invoke, or
        // the MSAA default action when Invoke does not exist; the postcondition decides after THIS single send.
        if (cef) return (role == "checkbox" ? new[] { "toggle", "invoke", "default" } : new[] { "invoke", "default" })
            .Where(p => p != "invoke" || invoke).ToArray();
        // A checkbox is toggled (Toggle); a button is invoked; a list item is selected.
        string[] order = role is "checkbox" ? ["toggle", "invoke"] :
            role is "radio" or "listitem" or "tab" or "treeitem" ? ["select", "invoke", "toggle"] : ["invoke", "toggle", "select"];
        return order.Where(p => (p != "invoke" || invoke) && (p != "select" || select)).ToArray();
    }
    static Outcome UiaSetValue(nint window, Node target, string value) =>
        FirstCall(() => ComBridge.Call(new ProgramUiaSetValueJson(op: "setValue", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow, value: value)));
    static Outcome UiaExpandCollapse(nint window, Node target, string action)
    {
        if (action is not ("Expand" or "Collapse")) throw NothingSent("Only Expand and Collapse are background secondary actions");
        // Fallback through Invoke of a refused Expand: a proper UI Automation provider only (never the Win32/WinForms MSAA proxy).
        var invokeFallback = target.FrameworkId is not ("Win32" or "WinForm");
        return FirstCall(() => ComBridge.Call(new ProgramUiaExpandCollapseJson(op: "expandCollapse", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow, action: action, invokeFallback: invokeFallback)));
    }
    // Double click of a list or tree item (an Explorer folder or file, pass 2 of 28/10: a real double click until then):
    // its default action, UIA Invoke (open), without a mouse or the foreground, like NexusWindows for Claude. Never for a
    // Win32/WinForms item (served by MSAA, its default action is a SendInput double click) nor for a Chromium page (Invoke
    // there is a mere click): no background technique, the physical fallback stays as before.
    static Outcome Open(nint window, Node target)
    {
        if (target.FrameworkId is "Win32" or "WinForm" || ComUia.IsSynthetic(target.RuntimeId) || ChromiumInput.HasChromiumContent(window) ||
            target.Role is not ("listitem" or "treeitem" or "dataitem") || !target.HasInvoke)
            throw NothingSent("Double click: no background technique for this control (only a list or tree item with UI Automation Invoke)");
        return FirstCall(() => ComBridge.Call(new ProgramUiaClickJson(op: "click", windowId: (long)window, runtimeId: target.RuntimeId, nativeWindow: (long)target.NativeWindow, order: new[] { "invoke" })))
            with { Mechanism = "Invoke (double click: open)" };
    }
}
