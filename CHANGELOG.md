# Changelog

All notable changes to NexusWindows are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases up to 30 October 2026 were made in separate repositories for each profile (Claude 3.7.4, Codex 3.7.4,
Antigravity 3.6.0), since removed; those versions remain on PyPI (`nexus-windows-claude`, `nexus-windows-antigravity`)
and npm (`nexus-windows-codex`).

## [3.17.0] - 2026-11-07

### Removed
- The Node.js adapter of the Codex profile (`codex/lib`, `codex/bin`, its REPL guide and `entry.mjs`, the vendored
  TOML reader, the `nexus-windows-codex` npm package files, `codex/.mcp.json`, its own `.codex-plugin` and
  `.agents` marketplace, `setup.ps1`, `install.ps1` and the `node --test` suites). Codex already ran the native
  engine through the common plugin (`plugin/.codex-plugin`, `NexusWindows.exe --mcp --agent codex`) and
  `--register codex`; the npm package stays frozen at 3.7.4 and is no longer updated (it is not marked deprecated
  on npm). An older `npx -y nexus-windows-codex` declaration is still recognised and replaced by `--register codex`.
- Node.js from the test and release workflows, and the npm ecosystem from Dependabot. `test.ps1` is now
  `dotnet test` alone, with no Node.js anywhere.
- The `mcp-name` marker of the Codex README, left from the npm entry that no longer publishes it.
- The VSTest packages of the test project (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`): xunit.v3 runs
  natively on Microsoft.Testing.Platform, the runner global.json already selects.

### Changed
- Releases ship the engine compiled ahead of time (Native AOT): `engine\NexusWindows.exe` is one 14 MB executable
  instead of the 78 MB self-contained engine in 193 files, and it answers `initialize` in about 50 ms instead of
  125 ms; the x64 package drops from 38 MB to 7 MB. `commun/package.ps1` refuses an engine that still carries
  `NexusWindows.dll`, its `.deps.json` or `.runtimeconfig.json`. Installed by `get.ps1`, it passes `test.ps1 -Full`,
  the live capture and DevTools title probes on test windows and the three-profile WPF test.
- `test.ps1 -Engine <NexusWindows.exe>` (`NEXUS_TEST_ENGINE`) runs the process tests against a given engine, such as
  the Native AOT engine of a package or of an installation, instead of the one built for the tests.
- The Claude Desktop extensions (`.mcpb`) are packed by `commun/package.ps1` itself, a ZIP of the manifest, the icon
  and the engine as before, after checking the manifest's required fields and entry point, instead of
  `npx @anthropic-ai/mcpb pack`: no Node.js or npm is left in the build or release.
- The DOM scripts the engine injects into Chromium pages (deep focus, select mutation) are executed by an embedded
  ECMAScript engine (Jint) inside xUnit instead of `node --test`, which was skipped in silence where Node.js was
  absent, the CI included.
- xunit.v3 4.0.2.
- The engine's Win32 calls are source-generated (`LibraryImport`, 265 of 269) instead of marshalled at run time,
  `StringBuilder` buffers included (character arrays now); the four remaining `DllImport` pass a structure that
  carries a delegate or an inline string (window class registration, process snapshot).
  The packaged app activation manager is a source-generated COM interface (`GeneratedComInterface`) instead of a
  runtime callable wrapper, checked against the real activation manager by an xUnit test.
- Every project builds under one analysis policy (`Directory.Build.props`): the .NET 10 recommended rules, code style
  enforced in the build and warnings as errors, with a shared `.editorconfig` that lists the rules left out and why.
  Its findings are fixed: culture-independent parsing and formatting of routine stamps, notification times and
  numbers, ordinal comparisons of window classes and arguments, cleanup results discarded explicitly, a fixture that
  no longer throws from `finally`, cached JSON options.
- The engine project is `NexusWindows.Engine` (`codex/native/NexusWindows.Engine.csproj`) instead of the old Codex
  profile's `NexusCodex.UIA`; installers and releases still publish it as `NexusWindows.exe`, so installed paths,
  running processes and the AI apps' declarations are unchanged. Its project and manifest comments are in English.
- The release workflow calls NexusSystem's shared development and promotion workflows at v1.6.54 (pinned by commit)
  instead of v1.6.40, with `mcp-registry` in place of the deprecated `mcp-package` input.
- The real-process checks of the shared emergency stop (stop file, guard, refusals) and foreground token (exclusion,
  deferral, release when the holder ends or dies) moved from the Node suites to xUnit
  (`SharedProtocolProcessTests`); the foreground token cases hold the PC's real lock and run with `test.ps1 -Full`.
- `Directory.Build.props` takes the user profile from .NET instead of `$(USERPROFILE)`, with no hard-coded backslash:
  Dependabot restores on Linux, where `USERPROFILE` is unset, so the build folder became `/.nexus` (denied) and its
  NuGet updates silently found nothing. Output folders on Windows are unchanged.
- The engine reaches the shell's automation objects (Explorer navigation, the Start menu's AppsFolder, the desktop
  launch broker) through a source-generated `IDispatch` interface (`ShellDispatch`) instead of the C# `dynamic`
  binder and runtime callable wrappers, and enumerates windows through an `UnmanagedCallersOnly` callback instead of
  a marshalled delegate (an exception in the callback stops the enumeration and is rethrown, as before). Both are
  trimming and Native AOT compatible; xUnit reads the real AppsFolder, shell windows and desktop broker.
- The engine's JSON is source-generated (`EngineJsonContext`, metadata mode) instead of reflection-based: its 165
  anonymous objects are named records (`JsonShapes.cs`) with the same property names, order and types, and every
  `JsonSerializer` call and `JsonArray` element goes through `EngineJson`, which keeps each caller's options (naming,
  encoder, indentation, case-insensitive reading). The serialized documents are unchanged.
- The last four runtime-marshalled Win32 calls (window class registration of the activity indicator and of the
  presence window, process snapshot) are blittable and source-generated: `UnmanagedCallersOnly` window procedures
  and an inline character buffer instead of a delegate and a `ByValTStr` string. `RuntimeReceipt` finds the engine's
  code file without `Assembly.Location`.
- The engine builds with `IsAotCompatible`: the trimming, single-file and Native AOT analyzers run in every build
  (warnings as errors) and report nothing. Measured on win-x64: the self-contained engine is 78.2 MB in 193 files
  and answers `initialize` and `tools/list` in about 125 ms; trimmed with ReadyToRun, 36 MB in 63 files and about
  160 ms; Native AOT, 13.5 MB in one executable and about 50 to 60 ms, with the MCP stdio, launcher, process and
  table tests passing against it.
- CodeQL runs from a workflow file (`.github/workflows/codeql.yml`, `actions` and `csharp`, default queries, actions
  pinned by commit) instead of the repository's default setup, so OpenSSF Scorecard credits the SAST check for
  commits pushed without pull requests.
- `get.ps1`, `commun/native-runtime.ps1` and the stable launcher accept a single-file Native AOT engine
  (`NexusWindows.exe` alone, checked against its hash) as well as the framework-dependent one, whose
  `NexusWindows.dll`, `.deps.json` and `.runtimeconfig.json` must now be recorded together (a partial set is refused).
  The README's Development section says what `test.ps1 -Full` must show before a stable promotion.
- The Claude and Antigravity READMEs name the server `nexus-windows`, its plugin name in the NexusSystem catalog and
  its MCP registry name, instead of `nexus-windows-claude` and `nexus-windows-antigravity`; the release file
  `nexus-windows-claude.mcpb` keeps its name.

## [3.16.28] - 2026-11-06

### Fixed
- A window whose UI Automation provider cannot describe its whole tree in one cached response is read level by level
  instead of failing. The « Signaler un problème » form of the Windows Feedback Hub (a WinUI 3 window hosting a
  WebView2) failed every `look` and `act`, image mode and image points included, with « Malformed Cacheresponse
  pTreeStructure String (missing closing parens) »; the whole form is now listed (fields, category, buttons) and
  its fields are set in the background. An element whose children still cannot be read keeps its place with the
  flag `unreadable`, and the look's summary points to the image and image points for that part.

## [3.16.27] - 2026-11-06

### Changed
- A capture stays lossless WebP up to 3 MB instead of 500 KB (shared Nexus image rule of 2026-11-06): measured the
  same day, Claude, Codex and Antigravity took every image up to 6.8 MB, through their own image viewer and through
  an MCP tool result, and read 16 small codes per image almost without error. The heaviest real screen measured
  (a photo wallpaper at 2000×1125) weighs 1.76 MB lossless, so screens are no longer sent lossy. Above 3 MB the
  fallback of 3.16.26 still applies (2 then 3 low bits dropped, then JPEG at the same size).

## [3.16.26] - 2026-11-06

The three editions follow the shared Nexus image rule updated on 2026-11-06 for a screenshot over 500 KB.

### Changed
- A capture over 500 KB goes through lossless WebP with 2, then 3 low bits per colour dropped (never more), then JPEG
  at quality 82, 72 or 60, all at the same pixel size; the image is made smaller only when none fits. Before, it
  dropped 2 bits and then shrank the image. Measured offline with Windows OCR on real screens: a photo wallpaper
  2560×1440 was sent at 1228×690 with 2 % of its words still readable, now at 2000×1125 in JPEG 82 (152 KB) with 69 %.
  Screens that fit 500 KB are unchanged (lossless, or 2 bits dropped).
- A JPEG capture is sent as `image/jpeg` with a `.jpg` file; an exported capture (`output_path`) takes the extension
  of the format actually written.

## [3.16.25] - 2026-11-05

The limits left by 3.16.24 on Chrome, fixed and measured on the shared bench (family « chrome », the evening's real
gestures, three profiles).

### Fixed
- A click in a field of a Chromium page in the background gives it the focus: the page publishes its focus change
  only at an accessibility request, so the field read « not focused » (« Focus without activation not read back »).
  The keys that follow the click in the same act go to that field.
- Alt shortcuts (alt+1, alt+n…) reach a Chromium window in the background, as system key messages with the Alt
  context bit under the simulated activation. Alt+Tab, Esc, Space and F4, and the Windows key, stay with Windows.
- A dialog that a click in Chrome opens and that activates itself (the Windows Open dialog of a file field) gives the
  user's foreground back at once: 16 ms on the bench instead of 1.3 s.
- Enter aimed at the address bar right after a set goes through: the field takes the focus first, in the background.
- The file name then « Ouvrir » in one act: a batch whose every step names a control with its own window handle is no
  longer refused because the dialog's file list refreshed.
- A control aimed as a button also finds a split button (the Open dialog's « Ouvrir »).
- A click on a page control after which nothing shows reveals the covered page once and reads it again (never a
  second click): an effect the hidden page did not publish is seen.

## [3.16.24] - 2026-11-05

Six failures met while removing an extension in chrome://extensions and changing the image of a Chrome Web
Store listing (pages that NexusBrowser cannot drive), reproduced on a Chrome of its own (shared bench, family
« chrome ») and fixed at their cause.

### Fixed
- A page loaded in a Chrome or Edge window covered by the user's windows is readable in the background: the window,
  when no pixel of it is visible, goes through a minimize and a restore without activation, its animation off, and
  stays where it was, maximized or not (« publication: no-document » until a pass in front). Chrome may activate
  itself on the restore while the user has been idle a long time: the foreground is given back at once. Browsers
  only, never an app or a dialog.
- Text reaches the « Nom du fichier » of a Windows Open dialog in the background, through its own Edit: typing on the
  combo box was refused (« needs a native Edit »), and on its field (« this part of the combo box has no action »)
  because the field, read without a place on screen, was first scrolled into view.
- Keys in separate steps (alt+n, ctrl+a, ctrl+v) go on like one key sequence: the batch stopped after the first,
  « sent-unconfirmed ». A key not delivered still stops it.
- Enter typed in a browser's address bar is no longer taken for a click on the page's outward button (« Envoyer pour
  examen »): a key reaches one surface, the browser's toolbar or one page.
- A click on a web page's control with foreground allowed is a real click: it went through UI Automation Invoke,
  which some pages ignore, even with foreground:true. In the background, a click after which nothing changes in the
  page or the app within 1.2 s says so, with the way to click for real.
- Indices stay usable from one act to the next (an act with then="none" made the next one refuse « This index was not
  displayed in the previous look »), and a page that changes its title no longer gives new indices to the window and
  its page, nor refuses the next act (« Interface structure changed »).
- A control right under a window with a window handle of its own (the Open dialog's file name, its Ouvrir button) is
  checked on itself, not on the whole window: its file list refreshing refused the typing.
- Repeated controls of a flat list (chrome://extensions: every card's « Supprimer », « Détails », switch) carry the
  label of their row, shown by look and part of their identity: an index meant for one extension can no longer reach
  the neighbouring one when the page re-binds its cards. « Supprimer (NexusBrowser) » names that very control.

## [3.16.23] - 2026-11-05

### Added
- A key an app refuses in the background goes by the real keyboard in a brief foreground lease, without asking:
  only while the user is not typing or moving the mouse, his window given back at once (measured 0.2 to 0.7 s).
- Editing keys (Backspace, Delete, Home, End, arrows, Ctrl+A, C, V, X, Y, Z) leave the background in a Chromium
  app: its editor does not apply them while its window is inactive (Codex, measured). Command keys (Enter, Ctrl+K)
  still go in the background.

### Fixed
- Every first simulated activation of a process waits for a quiet moment again: Codex running for an hour still came
  in front for 17 ms (3.16.20 had dropped the wait for processes older than a minute).
- A key aimed at an unfocused Chromium field no longer tries a background focus that is never allowed: with
  foreground:true it was refused inside the foreground lease, after the window had been brought in front.

## [3.16.22] - 2026-11-05

### Fixed
- Replacing text in a Chromium field (type with replace:true) empties it by its UIA value, then types: Codex ignores
  posted keys, so Ctrl+A then Delete left its text and the replacement never started.
- A value postcondition on an editable field showing its placeholder as its value (a cleared Codex field) holds for
  an empty value; a label whose text is its own name keeps its exact comparison.

## [3.16.21] - 2026-11-05

### Fixed
- Text reaches Antigravity's message field in the background: a contenteditable with role combobox and a text pattern
  is a text target (it was refused, "needs a native Edit control"; a <select> still is not).
- An app launched by its own shortcut (Antigravity) shows its windows: the launch found no window and a window
  maximized across both screens was left so. The shortcut keeps its own arguments.

## [3.16.20] - 2026-11-05

The user keeps working while an agent works: asking « OK » of Codex and Antigravity hidden in the notification area
took 64 s and 14 calls; Codex now takes three calls and about 2 s, with no foreground taken.

### Fixed
- Text in a Chromium app (Codex, Claude, Antigravity, Chrome) is no longer stopped by the user's keyboard or mouse in
  another window: only bringing that window in front stops it, or the user's first input when it already was.
  Messages to a window that is not in front no longer wait for a quiet moment either, except a process's first
  simulated activation in its first minute, the only one that can take the foreground.
- An empty field showing its placeholder as its value (Codex's « Travaillez avec Codex ») counts as empty: text
  waited its whole 170 s budget for « placeholder + text », the real cause of the 53 s for 8 characters.
- A field that shows no change for 5 s stops the text at once instead of at the end of the 170 s budget.
- An app's window hidden in the notification area is brought back behind the user's windows, at its own place and
  without activation, when it is named (it was « No window matches »); an app that destroyed its window (Antigravity)
  is reported as running without a window, to be launched.
- A reused window that comes back maximized across several screens (Antigravity, 5138 px) is put back at its place.
- Two controls with the same name, only one on screen, resolve to the visible one.
- A narrowed look (query, within) keeps the indices shown by the looks before it.
- foreground on a step allows its physical fallback, like foreground on the batch; it was ignored without a word.

### Changed
- Message text goes in packets of 64 characters (was 8), each still read back before the next.
- A step sent without a postcondition no longer ends a one-off batch when the next step names its own target; the
  batch then reports unverified. Routines keep stopping.
- A key aimed at a Chromium field that does not hold the focus gives it the focus in the background first.
- look query accepts « A|B ».

## [3.16.19] - 2026-11-05

### Fixed
- Launching an app by its name or path without arguments goes through the user's own Start menu shortcut when that is
  the one way they start it with arguments, as a click on it would: Antigravity keeps its `--remote-debugging-port`
  for NexusKeypad. A plain shortcut, or several different ones (browser profiles), keeps the bare executable.

## [3.16.18] - 2026-11-04

### Fixed
- `type` without `replace` into an empty single-line field that completes what is typed (Chrome's address bar in a
  new window) no longer stops at "8/20" with "Interaction budget expired": the inline suggestion shown after the typed
  characters is tolerated, as it already was with `replace:true`. Appending to a field that holds text keeps the exact
  read back.
- Typed text is judged on the field's value read back, not on the count of acknowledged characters: when the wait
  stops but the field shows the whole text (a suggestion after it tolerated), the step succeeds and the batch's next
  step (Enter) is sent; otherwise the step stays unknown and says what the field showed.
- The foreground lease (`act(foreground=true)`) and the batch's pause boundary last 30 s plus 0.75 s for each
  character of the batch's `type` and `set` text, at most the 170 s text budget, instead of a fixed 30 s.

## [3.16.17] - 2026-11-04

### Changed
- The product name is written NexusWindows, without a space, in the plugin README title, source comments and the
  offline OCR self-test phrase ("NexusWindows OCR 2026"; the check still reads "OCR 2026" and its position).

## [3.16.16] - 2026-11-04

### Fixed
- A target inside a modal box aimed at from its owner window is acted on even when the box runs in another process of
  the same application: Chrome shows its "select the extension folder" box from its file-dialog utility process, and
  the step was refused with "Target or its window is disabled". The box must still be owned by the window, visible,
  enabled and run by the same executable; another application's window is never used.
- Target names match whether they are written with an ordinary space or a no-break space (U+00A0, U+202F), on both
  sides, in `act` targets and `look` queries: Chrome's French "Dossier :" field was not found when typed as shown.

## [3.16.15] - 2026-11-04

### Changed
- Display name written NexusWindows, without a space, like the repository and the NexusSystem catalog.

## [3.16.14] - 2026-11-04

### Fixed
- The Steam startup self-check no longer races its helper child: the child writes its report aside, closes it, then
  renames it, so the engine never reads a partial report or deletes it while the child still holds it (CI failure
  "being used by another process").

## [3.16.13] - 2026-11-04

### Fixed
- A `key` step without target is no longer refused with "Keyboard focus is ambiguous" right after a `set` or `type` of
  a field: when several elements report keyboard focus (Chrome's address bar and its page), the key goes to the field
  the batch just set, typed or clicked, if it is one of them. Any other ambiguity is still refused.
- `type` no longer appends silently: a verified step says "Text appended after the field's existing text" when the
  field already held text, and `replace:true` on a `type` step replaces the whole field, like `set` but with real
  keystrokes, on every route (Chromium simulated text and DevTools, Win32 edits, UI Automation, physical fallback);
  its postcondition is then the typed text. Without it, `type` keeps its insertion at the end (rich editors,
  documents). Typing `chrome://extensions/` into Chrome's address bar had given `chrome://extensionschrome//extensions/`
  with nothing said.
- `type` with `replace:true` into a single-line field no longer stalls on an inline suggestion shown after the typed
  characters (Chrome's address bar): the typed prefix followed by the suggestion counts as typed, and a suggestion left
  after the text is reported. A 20-character URL had stopped at 8/20 when the 30 s foreground budget ran out.
- A target inside a modal dialog listed under its owner window is routed to the dialog's own window even when the owner
  has other enabled popups first (Chrome's « Sélectionner le répertoire de l'extension » was refused, "Target or its
  window is disabled"). The dialog must still be visible, enabled, owned by that window and in the same process.

## [3.16.12] - 2026-11-04

### Changed
- Product icon now shows the julienpiron.fr medallion on a transparent background at every size (no dark square), with the knot badge at the bottom of the ring kept whole.
- Same `.gitattributes` as every Nexus repository (2026-11-04): LF in Git and on disk, CRLF only for `.cmd`/`.bat`; files stored with CRLF were renormalized once.

## [3.16.11] - 2026-11-04

### Fixed
- Annotated `look` images no longer fail with a Number/Null error for UI Automation controls without their own native
  window, including Unreal Editor. Their annotation scope uses the existing numeric root-scope contract; controls
  with their own window keep their exact scope and the foreign-window guard.

## [3.16.10] - 2026-11-03

### Fixed
- `logs(errors_only=true)` no longer fills up with gestures that were simply sent without an `expect`: an `act` batch
  that ran every gesture, all succeeded but the last, and ended with that plain send is logged `ok`. A batch stopped
  before its later gestures, an earlier failure, a paused routine or a send whose outcome is truly unknown stays a
  failure, and the answer itself still says `unverified`. Records written before this version are left as they are.

## [3.16.9] - 2026-11-03

### Fixed
- Diagnostic logs also keep the engine's own fixed reasons for unverified, refused and stale gestures (no postcondition
  supplied, postcondition not observed, changed control or window identity, off-screen or disabled target, read-only
  field, password control...), so a failure says why. Only the fixed prefix is kept, never the text after it.
- The .NET test harness isolates native profile state and the shared journal in its temporary test home. Child MCP
  servers previously inherited only the locks/receipt override, so diagnostic and deliberate-refusal tests appended
  synthetic failures to the real user's logs. Existing history is left intact.

## [3.16.8] - 2026-11-03

### Fixed
- The shared plugin and Antigravity guides now teach the current observation/selector/expect contract first,
  reuse the fresh state returned by an action, and batch verified steps. Removed obsolete claims that Chromium
  file pickers always require physical input and that password fields can be filled.
- Diagnostic logs retain closed, non-sensitive categories for stale observations, changed interfaces, invalid
  postconditions and unavailable background input, plus fixed result statuses, instead of hiding every cause behind
  an exception type. Unrecognized error text, typed content and authorization values remain excluded.
- The launcher ownership test waits for its own child process to appear instead of assuming startup within 500 ms,
  and always closes its launcher on failure. The startup race could otherwise fail the suite on a busy workstation.

## [3.16.7] - 2026-11-03

### Fixed
- The skills' links to the operation-approval section of the MCP cookbook point to GitHub instead of a relative path:
  an installed skill (Antigravity plugin folder, Codex skill folder) is copied outside the repository, where the
  relative link led nowhere (the Antigravity one went one level above the repository even there).

## [3.16.6] - 2026-11-03

### Added
- Antigravity Marketplace bundle: `plugin/antigravity` passes `agy plugin validate --strict` as a self-contained
  plugin. Its `mcp_config.json` starts `scripts/start.cmd` from `${PLUGIN_ROOT}`, which runs the installed engine
  (`%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe`) or says on stderr how to install it. A `README.md` gives the
  Marketplace detail page, and the skill tells the agent how to install a missing engine from the latest release
  (`get.ps1` checked against `SHA256SUMS.txt`). NexusSystem installs keep writing their own declaration.
- Benchmark results: NexusWindows 3.16.5 and Cua Driver 0.38.0 on the same Windows background tasks, with method,
  per-task table and raw data (`docs/benchmarks/2026-11-03-cua-driver`).

### Fixed
- Garde-fous : retrait du blocage dur de la fenêtre de Codex pour les trois profils (`fenetre_autorisation_codex` supprimé des listes actives). Claude, Antigravity et Codex peuvent interagir librement avec la fenêtre de Codex / ChatGPT.

## [3.16.5] - 2026-11-02

### Fixed
- A direct semantic action on a stable control no longer fails because another part of the window changes (for
  example, a chat updating beside the project sidebar). The observed runtime identity, complete ancestry, local
  command context and application identity are revalidated before input. Replaced or ambiguous controls, changed
  local values, selection and modals still refuse. Pixel/OCR actions, keyboard commands, batches and routines keep
  their whole-window structure check.

## [3.16.4] - 2026-11-02

### Fixed
- `act(foreground=true)` on a Chromium window looked at in the background (Chrome behind other windows or minimized)
  is no longer refused every time with "Interface structure changed since look": the structure is compared with the
  look before the window is brought forward, since Chromium then publishes its page content (a larger tree, not a
  changed interface); the batch starts from a fresh inspection.

## [3.16.3] - 2026-11-02

### Fixed
- The anti-repeat guard of `act` is back, for the three profiles. The Antigravity engine had it since 16/10 (Gemini
  had resent the same batch six times); it went with the Python engines in 3.9.0, and since 3.13.0 an `act` without a
  prior look observes first, so a blind resend went through. A batch strictly identical to one already sent to the
  same window is refused, nothing sent, when that window had no `look` in between and has not changed since that
  batch (its elements, values, states and their place in the window; its pixels when its tree is too poor). A changed
  window (next page, new value) lets the same batch through. `repeat=true` repeats on purpose a batch that was
  verified (✓), never one that ended with an effect not seen (~) or a failure (✗). The refusal says what to do: look,
  then send only the steps still needed, or adapt the batch.
- Installing or updating removes the earlier engine versions (`native\<sha-256>`), the launchers set aside
  (`NexusWindows.exe.old-*`), interrupted installation folders, and the disposable browser profiles of
  `launch(devtools=true)` whose browser has closed. The installed version, every version a running session executes
  and every version a client or daemon declaration still names stay; a folder still in use is never half removed.
  On this PC 31 versions (433 MB) had piled up since 30/10.

## [3.16.2] - 2026-11-02

### Fixed
- The Claude Code and Codex guide shows what a step of `act` looks like (`{"action": "click", "target": "..."}`),
  with a complete example call; it listed the step names without ever giving their shape.

## [3.16.1] - 2026-11-01

### Fixed
- `launch` never waits on a window that Windows opens instead of the application, and never leaves one on the user's
  desktop; it answers with a refusal that says why (the three profiles, measured before and after):
  - a document whose type has no associated application is refused before anything is sent (it waited for a window
    while Windows' "choose an application" window stayed open: > 40 s, now 1.7 s);
  - an address whose scheme no application handles: the "choose an application" window that Windows opens is closed
    and the launch refused (> 40 s, now 1.9 s);
  - an error box of Explorer's titled with the target ("Windows cannot find..."): refused at once, quoting the box,
    which is closed;
  - a file named `.exe` that is not a Windows program, which Windows fails silently: refused before anything is sent
    (18 s with an unknown outcome, now 0.1 s);
  - when Windows still awaits a decision in another window after 6.18 s (administrator consent, SmartScreen), the
    shared foreground token is released and the launch reported as dispatched once with an unknown outcome.
  An ordinary launch is unchanged (3.6 s with the window attributed and the foreground kept).

## [3.16.0] - 2026-11-01

### Changed
- A step that only the real mouse or keyboard can do in this window is refused the same way for the three profiles:
  `needsForeground: true` and a message starting "Needs the foreground", which says to ask the user's agreement and
  then repeat the step with `foreground:true`. On the common banc in the background, every such refusal (WPF raw click,
  WPF keys, WPF context menu, WinForms and WPF double click, desktop drag and drop) now says so; with the foreground
  granted, each of these gestures succeeds.

## [3.15.1] - 2026-11-01

### Fixed
- A shortcut sent in the background to a window where nothing has the keyboard focus (a window never activated) goes
  to the window itself instead of being refused because "focus cannot be verified" (Ctrl+K on the Win32 and WinForms
  labs).
- A WPF menu is opened in the background by UI Automation, by its names, as WinForms menus already were, instead of
  "No Win32 menu bar".
- `launch` no longer hangs on a program or folder that only exists in a packaged app's virtualized AppData (the engine
  started by Claude Desktop, an MSIX app, sees its package's private copy of `%LOCALAPPDATA%`). Explorer, which starts
  user applications, did not see that path: it showed a modal "Windows cannot find" box on the user's desktop and its
  synchronous `ShellExecute` never returned, holding the shared foreground token, so the other two AIs got "Foreground
  token unavailable" and the next call of the same client waited too. Explorer now receives the file system's backing
  path, and the launched window is recognized by that path. On the common banc, the covered launch goes from 0/3
  (120 s timeout) to 3/3 in about 1.9 s for the three profiles, and the long page no longer waits 120 s behind it.

## [3.15.0] - 2026-11-01

### Added
- Drag and drop in the background in a Chromium or Electron page opened with a DevTools port, for the three profiles:
  the mouse is pressed, moved and released through DevTools, and an HTML5 drag (`draggable`) is intercepted and
  dropped by `Input.dispatchDragEvent` instead of opening the Windows drag loop, which follows the real mouse. On the
  common banc's new drag-and-drop task, Edge and Electron go from refused to the exact drop, with no intrusion.
  In desktop applications (Win32, WinForms, WPF) a drag is never posted: the application may start the Windows drag
  loop (OLE), which follows the real mouse and would drop under the user's cursor. It stays refused in the background
  and takes the real mouse only with `foreground:true`.

## [3.14.0] - 2026-11-01

### Changed
- The three profiles behave the same way wherever no client requires otherwise, each time with the better of the
  former behaviors (Julien's decision of 01/11; the behavior test compares these defaults too):
  - an `act` without a prior look observes the named window first, Codex included; an observation id stays optional
    and, when given, designates the window and must still be current;
  - element indices stay the same from one look to the next for the same element (Claude's numbering);
  - `look` lists 150 lines by default (up to 2000 on request, the rest summarized with its regions), adds an image
    when the tree is too poor, and numbers the unnamed controls on images (`marks="auto"`); `mode="text"` is the tree
    for the three profiles, `show="text"` the document text;
  - `act` and `launch` return the updated window in the same answer by default (`then="auto"`, `then="none"` to skip);
  - a window moved since its image keeps its image usable for points and `region` (only a resize asks for a new look);
    `region` takes the image id optionally, and a point carrying the region's own id is read in its pixels;
  - text postconditions compare with spaces normalized (line breaks, non-breaking spaces), `includes` ignoring case;
  - French and English key names and modifiers (`entrée`, `échap`, `ctl`, `maj`…) are accepted by the three profiles
    and made canonical before the guards read them;
  - a simulated Enter in the MCP server always needs its text submission scope (send and agreement checks);
  - `remember` saves a routine unless `staticUI:false`, and `remember` with no steps forgets it;
  - `windows` has one output schema (Codex's fields and the window details) for the three profiles.

## [3.13.0] - 2026-11-01

### Changed
- Typing into a modern UI Automation field (WPF, UWP/WinUI, WinForms rich controls) appends in the background for the
  three profiles: the current value is read in the action worker and extended by UIA `SetValue`, as Claude already
  did. Codex used to type it with brief foreground input (`physical:native`). A test runs the same `type` and `click`
  on the same WPF lab window for the three profiles and requires the same technique and result.
- Antigravity's `type` no longer rewrites the whole field with the value seen at the last look plus the new text: it
  appends like the other profiles, from the value read at the moment of the action (a change made since the look is
  no longer overwritten).
- A postcondition on a radio button's `checked` reads its selection, and a `value` on a text or a label reads its text,
  for the three profiles (Claude only before; Codex and Antigravity left such a step unverified).
- `windows` gives each window's process, framework (Win32, WPF, Chromium, UWP), size and state (minimized, maximized,
  foreground, hidden, taskbar or desktop surface) for the three profiles (Claude only before).
- `act(then=…, settle_ms=…)` returns the updated window in the same answer for the three profiles. Claude keeps
  `then="auto"` by default; Codex and Antigravity get it on request (`then="none"` by default, answers unchanged).
- Typing one line into a Chromium field or rich editor (Chrome, Edge, Electron) takes Claude's DOM-focus text route
  for the three profiles, read back packet by packet. At the banc, Codex and Antigravity reported a false success on
  the rich editor of Edge and Electron (the page received nothing); they now write it, like Claude. Line breaks take
  it too for the three profiles, each Enter checked against the send and agreement safeguards.
- `set` on a Chromium field writes it by UI Automation `SetValue` for the three profiles: Claude's simulated typing
  failed there at the banc (0/3) where the other two profiles succeeded.
- One set of guards for the three profiles (Julien's decisions of 01/11; the behavior test now compares them too):
  - a password field is refused to the three (Antigravity could write into one);
  - the real mouse and keyboard are used only with `foreground:true`, Codex included (it took them by default);
  - the Windows panels (`"start"`, `"quick settings"`, `"notifications"`) and one foreground lease for a whole
    `act` with `foreground:true` are open to the three (Claude only before);
  - `launch` takes Claude's route for the three: Start menu names, any URI through its Windows association, Store
    apps, documents and folders proven open, `devtools` on an isolated profile, Steam DevTools lease, `then`/`wait_s`
    (`then="none"` by default for Codex and Antigravity); Codex and Antigravity opened only executables, paths, app
    ids and `ms-settings:`;
  - a step whose `{selector, exists}` postcondition already holds answers "=" without sending anything, for the three
    (Codex and Antigravity refused it as vague);
  - without a named or bound window, the three refuse (Claude acted on the foreground window);
  - the hard blocks are the union of the three lists for each profile: secure desktop, Windows credentials window,
    password managers and Codex's own approval window;
  - `look(window="screen")` captures the whole screen for Codex too;
  - an `act` without a prior look observes the named window first for Antigravity as for Claude; Codex's contract
    still needs the look's observation id.
- Chromium text (Chrome, Edge, Electron) may take up to 170 s for the three profiles, packet by packet, each packet
  still checked (focus, presence, STOP, client cancellation); Codex and Antigravity stopped a long text at 9 s.

### Fixed
- Agents acting at the same time slowed each other down: a `type` on the WPF lab took 2.4 s alone, 10 to 11.5 s with
  three agents on three windows. The cause is in Windows, not in a NexusWindows lock: simultaneous UI Automation tree
  reads of different windows are slower than the same reads in turn (0.45 s alone, 1.9 s each for three, also with
  clients outside NexusWindows). Full tree reads now take turns across the agents (a file lock in `.nexus`, bounded at
  4.854 s, after which a read goes ahead anyway).
- A WPF window is read about a third faster (lab window: 1.1 s to 0.77 s): its MSAA state, which only tells a default
  button and costs a bridge per element, is read on the invokable elements only; the observed tree is identical.
- A text postcondition (`expect: "clic 1"`) that the window shows but its accessibility tree does not carry (a WPF
  TextBlock named by `AutomationProperties.Name`, a drawn label) is also looked for by OCR in a background capture of
  the window, before the gesture (a text already shown cannot verify it) and after. The click arrived but the step
  answered "Postcondition not observed". A text that must disappear (`gone`) is still judged on the tree.

## [3.12.0] - 2026-10-31

### Added
- `look(within=N, show="table")` reads a table: column headers, then one line per row (`max_elements` rows at most),
  through UI Automation `GridPattern`/`TablePattern` (Explorer Details view, WinForms ListView), or from the observed
  tree when the control has no grid (DataGridView: one row per row container; Task Manager: one cell per child).
- A window is held by one agent at a time during an `act`: a file lock per window in `.nexus\windows`, held for the
  whole batch like the foreground token and freed by Windows if its holder dies. Another agent waits up to 4.854 s,
  then gets ✗ "Window busy: codex (pid …, for … s)" with `retry_safe: true` and `outcome: "not_done"` (nothing sent).

### Changed
- A click at a point (`"x,y"`) on an interface that ignores posted mouse input (WPF, UWP/WinUI, Firefox) is the UI
  Automation gesture (Invoke, Toggle or SelectionItem) of the single enabled element under the point, in the
  background, instead of the brief foreground input; the read-back and the confirmation safeguards are unchanged.
  Several such elements under the point (a check box in a list item) keep the point.

### Fixed
- Codex source installer: the public catalog check accepts NexusSystem's `git-subdir` entry (this repository at a
  release tag) and verifies the copy Codex installed (version, manifest-declared MCP file and guide). It only knew the
  former in-catalog `local` source, so every check on a PC using the catalog failed.

## [3.11.0] - 2026-10-31

### Changed
- The three profiles serve the same tools and capabilities: Codex and Antigravity now also get `notifications`,
  `clipboard`, `logs`, `look(within=N)`, the skeleton list with named regions, compact listed elements and the taskbar
  and desktop as windows (by name and by id). A test fails if a tool or one of these capabilities is reserved to one AI.
- `clipboard` `write` borrows the clipboard instead of replacing it: the user's content (every memory format) is kept
  aside and given back on its own after 61.8 s, or at once with `action="restore"`; a copy made by the user meanwhile
  is kept, and a write is refused while the user's clipboard holds private content (password manager).

### Fixed
- `clipboard` `write` in 3.10.0 overwrote the user's copy for good.

## [3.10.0] - 2026-10-31

### Added
- Claude: `look(within=N)` lists only element N and its descendants. When a dense window's list is cut (chat apps,
  IDEs), the summary names its regions (`[N] list "Messages" (340)`), like agent-desktop's `--skeleton`/`--root`.
- Claude: `clipboard` tool. `read` (default) returns the copied text (security codes masked), the copied file paths
  and whether an image is there. Content that a password manager marked private (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory` = 0) is never read. `write` replaces it with text and reads it back.

### Changed
- Claude: `look` no longer lists unnamed layout wrappers (pane/group/custom with no name, id, value or focus that hold
  other elements); the summary counts them, and `detail="full"` lists everything.
- Claude: listed elements are compact in the structured result, without default values (null, false, empty) or internal
  identities (`runtimeId`, `nativeWindow`; the ancestor chain becomes `parent`). Acts still resolve against the full
  observation. On the Claude desktop window: 150 elements in about 22,000 characters instead of about 60,000.
- Docs: profile READMEs point to this repository only (release badge, family table); the Codex README installs the
  native engine only, without the npm package or the removed `NexusWindows-Codex` plugin marketplace.
- Release: a push whose version is not released yet becomes the stable release right after its development build
  (same files); "Run workflow" still promotes by hand.
- Deferred experimental native freshness reconciliation until unsupported UIA properties provide reliable proof.
  The candidate and qualification work are archived outside the repository; this experimental work is not included in 3.9.4.

### Fixed
- Claude: `look` with the id that `windows` gives for the taskbar or the desktop was refused ("No window matches").

### Security
- CodeQL (GitHub default setup) and the OpenSSF Scorecard workflow run again on every push to main.

## [3.9.8] - 2026-10-31

### Fixed
- `act` on a web element above or below the visible part of a long page (Chrome Web Store dashboard, 31/10): a target
  shown by `look` as `[N]` is now scrolled into view (UI Automation `ScrollItem.ScrollIntoView`) before input, like a
  named target, and read back visible (1.618 s at most) before the gesture; `set` + `click` on it work in one `act`.
  Before, Claude's background `set` stopped with "The observed Chromium field identity changed" and a click was
  refused as off-screen.
- A filtered `look` (`query`, `max_elements`, `show="changes"`) no longer returns the whole accessibility tree and
  document text in its structured result (about 45,000 characters on a long page, which Claude Code shows the model
  next to any image): only the listed elements, with their `[N]`, and a one-line `summary`. The tree and document
  text come with `detail="full"` or `show="text"`; `show="image"` returns no element list.
- `show="changes"` compares the whole window with the previous look, even after a narrow `query`, and returns only the
  added or changed elements; the indices shown before stay usable.
- `scroll` on an element without a name or automation id (a web page's document) uses the center of its visible part
  instead of being refused for lack of a stable selector.
- The wheel on a Chromium window without a DevTools port (the user's own browser) scrolls the page through its UI
  Automation `ScrollPattern`, read back, instead of being refused.
- An image returned by `act(then="image")` or `look` stays usable for points after the 60-second observation renewal,
  as long as the window keeps its size (a resized window still refuses the point).

## [3.9.7] - 2026-10-31

### Added
- Development builds: every push to `main` checks one version everywhere, runs `test.ps1`, builds the same release
  files and publishes them, attested, as the pre-release `vX.Y.Z-dev.N` (NexusSystem's development channel); each
  published build is then installed from its release files in an isolated profile.
- `get.ps1` honours `NEXUS_RELEASE_TAG` (a stable `vX.Y.Z` or a development `vX.Y.Z-dev.N`) to install that release
  instead of the latest; its `SHA256SUMS.txt` still verifies everything.

### Changed
- A stable release is the promotion ("Run workflow") of a tested development build: the same files, unchanged,
  become `vX.Y.Z` with this changelog's section, then `server.json` goes to the official MCP registry. Pushing a
  version tag no longer releases anything.
- The release workflow calls the shared NexusSystem workflows (`product-dev-release`, `product-promote`), pinned by
  commit SHA. Provenance is now signed by them: `gh attestation verify <file> --repo pironjulien/NexusWindows
  --signer-repo pironjulien/NexusSystem`.

## [3.9.6] - 2026-10-31

### Changed
- `plugin/` is now the single source of the NexusWindows plugin for the three AI apps: Claude Code
  (`.claude-plugin`, `.mcp.json`), Codex (`.codex-plugin`, `.codex-mcp.json`), their shared guide (`skills/`) and
  icons (`assets/`), plus Antigravity's own plugin (`antigravity/`, formerly `agy/plugins/nexus-windows`), which
  still ships with the engine. The NexusSystem catalog points to this folder at the release tag instead of keeping
  its own copies. The engine is unchanged.

## [3.9.5] - 2026-10-30

### Added
- Public `get.ps1 -UpdateOnly` maintenance installs the engine alongside running sessions without registering,
  enabling or creating any client. Optional `-RefreshExistingProfiles` refreshes an already active Antigravity
  plugin's shipped files while preserving all configuration, disabled/custom declarations and unrelated assets.
- MCP discovery and initialization expose the actual process identity and assembly SHA-256. Atomic runtime
  receipts record each session's PID, exact process birth, agent, version, loaded executable and assembly hash,
  with idle/busy/stopped lifecycle. A saved receipt alone is not proof that an AI host loaded the engine.

### Fixed
- Public and source installers share installation exclusion, preventing concurrent launcher/manifest swaps.
  Existing MCP sessions continue with their current engine; new connections use the installed version.

## [3.9.4] - 2026-10-30

### Changed
- Clarified that an existing explicit end-to-end user request or approval covers the same operation's preparation
  and application confirmation steps, without repeated questions for fresh technical tokens. Exact token binding,
  single use and expiry remain unchanged; materially changed scope or publication mode requires a separate decision.
- Updated the three profile guides and skills to prefer discovered MCP schemas, fresh observations and modern
  equality postconditions. A shared cookbook records tested background file-dialog handling, explicit UTF-8 for
  custom stdio clients, and the distinction between optional Chromium warmup retries and a genuinely blank page.

### Fixed
- Multiline value replacements no longer inherit an unrelated publish/send button as their trigger. Physical multiline
  fallbacks recheck the bound field, its focus and current outward controls before every real Enter for all three agents.
- Classic editable combo boxes, including the common file picker, expose their live edit text as the combo value.
  Empty text also replaces a stale provider value, so a background write can be verified against the same control.
  Hidden Win32 control regressions cover file paths, clearing the field, selected options and synthetic edit input.
- Chrome tab structure checks ignore only the terminal memory-usage metric in French and English, including high
  memory and localized decimal values. Actual tab titles, group names, control identities and other controls remain
  part of the strict structure hash; title text that resembles a metric in the middle is preserved.
- Release-installation fixture now represents an installed Claude Code with its existing `.claude.json`, so the
  all-client check tests the same clients as installation. Published tags can be checked again without rebuilding
  or republishing their assets.

## [3.9.2] - 2026-10-30

### Fixed
- The public installer works in Windows PowerShell 5.1 started from PowerShell 7: its built-in module directory
  takes precedence over inherited Core modules, so checksum validation and file unblocking remain available.
  Existing custom module paths are preserved. A child-process regression checks the real bootstrap and hash helper.
- Workstation source setup preserves an explicit public `nexus-windows@nexus-plugins` choice, including disablement,
  rather than recreating a personal plugin. It checks the registered catalog and exact installed cache; missing or
  changed files remain a reported repair without rewriting plugin choices or custom MCP declarations.

### Added
- Release installation check on an isolated Windows profile, including checksum verification, client declarations,
  stable-launcher MCP startup and a second installation of the published package.

## [3.9.1] - 2026-10-30

### Added
- Claude Desktop extension for ARM64 PCs, `nexus-windows-claude-arm64.mcpb`, next to the x64 one in each release.

### Fixed
- MCP registry entry: description within the registry's 100-character limit; the registry publication can be rerun
  by hand for a published release.
- Installation is documented and tested only from the release files (get.ps1 checked against `SHA256SUMS.txt`, then
  run with `-File`); test PowerShell sources run as script files instead of encoded command lines.

## [3.9.0] - 2026-10-30

### Added
- Native release files for Windows x64 and ARM64: `NexusWindows-win-x64.zip` and `NexusWindows-win-arm64.zip`
  (self-contained engine, nothing else to install), `SHA256SUMS.txt` and a Sigstore build provenance attestation
  (`gh attestation verify <file> --repo pironjulien/NexusWindows`).
- `get.ps1`, attached to each release (run as a verified file with `-File`; `-Remove` to uninstall): installer and updater that picks the PC's
  architecture, checks the SHA-256 and declares NexusWindows to the clients present.
- Stable launcher `%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe`: every client declares this one path; it runs
  the engine version recorded in `installation.json` after checking its SHA-256, passes stdin, stdout and the exit
  code through, and is replaced safely while sessions run (they keep their version, the next one gets the new one).
- `NexusWindows.exe --register claude|codex|antigravity|all [--check] [--remove]`: declarations for Claude Code (or
  its nexus-windows plugin), Claude Desktop, Codex (`config.toml`) and Antigravity (plugin), replacing the Python
  installers; an entry of your own or a disabled one is never changed.
- Claude Desktop extension `nexus-windows-claude.mcpb` built on the native engine, published to the MCP registry as
  `io.github.pironjulien/nexus-windows`.
- xUnit test suite (`tests/`, `dotnet test`), and secret (gitleaks) and workflow (zizmor) checks in the CI.

### Changed
- The native engine is the only engine: `test.ps1` and the CI run `dotnet test` and the Codex adapter's
  `node --test`; the engine's `--*-selftest` options became xUnit tests.

### Removed
- The Python engines and their tooling (Claude `src/`, Antigravity `client/`, shared `garde_fous.py`, pyproject,
  uv.lock, Python tests, bench and lab) and the uv-based setup scripts. Earlier PyPI packages stay available but are
  no longer updated.

## [3.8.0] - 2026-10-30

### Changed
- One repository for the three profiles (`claude/`, `codex/`, `agy/`) and their shared policy (`commun/`), replacing
  NexusWindows-Claude, NexusWindows-Codex and NexusWindows-Antigravity. Installation goes through NexusSystem.
- The separate PyPI, MCP registry and directory listings (`glama.json`, `server.json`) are no longer published from
  here; earlier packages stay available.

### Added
- One common C# (.NET 10) engine, `NexusWindows.exe` (`codex/native/`), serving the three profiles as MCP stdio
  servers; installed as an immutable version under `%USERPROFILE%\.nexus\nexus-windows\native\<hash>`, outside
  MSIX-virtualized AppData and the Codex plugin cache. Running sessions keep their server until their host reloads it.
- Antigravity profile: persistent semantic routines with text parameters, inspection, deletion and cross-PC
  definition sync (authorizations never saved); window-targeted WGC capture with local Windows OCR; window
  restore, minimize, maximize, bottom, front, move and resize with native state readback.

### Fixed
- Shared log in `%USERPROFILE%\.nexus\journal\windows` instead of AppData: started by Claude Desktop (an MSIX app),
  the engine wrote into a private copy of AppData other tools could not see.
- Routines: a fixed expected text is no longer exported to the routine mirror; a routine received from another PC is
  reviewed before its first replay. The shared log no longer records open document or folder names, nor the value
  of an unknown key.
- Claude profile `launch` of a Settings page: the window is described by its content, as the inventory names it,
  instead of its ApplicationFrameHost frame, so a successful launch is no longer refused as "identity changed".
- Long Chromium text input without DevTools (Claude profile): 170 s per gesture again instead of 9 s, which stopped
  2,000 characters around 650; every chunk is still read back.
- MCP stdio: `notifications/cancelled` stops the running request at its next guard, like an emergency stop (no
  replay, unknown outcome after input); a batch ended by EOF is always processed in full.
- Clicks posted in the exact DPI context of the target; Chromium rich-text focus under exact identity.
- Antigravity profile: a read right after an input is recognised as a newer observation (the repeat guard ordered
  events with a clock that only moves every ~15 ms on Windows; it now uses an event counter).
- A UIA or Win32 input whose effect is not confirmed no longer triggers another technique or a physical click;
  repeated inputs require a newer observation of the same window; `expanded` and `focused` postconditions are checked.
