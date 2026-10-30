# Private UWP lifecycle fixture

This harness uses the actual Windows UWP `ApplicationT`, stock XAML controls and real AppContainer/PLM state.
It has no custom UIA provider, network/device capability, debugger enablement or access to another app's data.
Its TextBlock reports the actual application `Resuming`/`Suspending` callbacks, with its own PID.
Startup stages and caught C++/WinRT failures are recorded only in this private package's
`LocalState/startup.json`. The driver copies that diagnostic into its receipt before removing the
package; it is diagnostic evidence only and cannot qualify a lifecycle transition.
The Store CRT initializes the entry thread as MTA before `wWinMain`; the fixture keeps that apartment
and lets `Application.Start` invoke its callback in the main view context. An explicit STA initialization
on the entry thread was proven to fail with `RPC_E_CHANGED_MODE` before XAML. Microsoft documents the
callback's main-view thread in [Application.Start](https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.application.start).

`build.ps1` is build-only. It requires Developer Mode and the compiler/SDK/VCLibs already present; it changes none
of them. Sources stay here; all compiler and deployment output stays under
`~/.nexus/nexus-windows/qualification/uwp-private/<unique name>`. It emits the exact `plan.json` path.

No action below is implicit in the build. The root coordinator runs it only in the serial GUI slot:

1. `register.ps1 -Plan <plan>` is read-only preparation. `-Apply` registers only this exact loose development
   layout, with unchanged Developer Mode, no certificate, no `-AllowUnsigned` or global policy change.
2. Launch the emitted `<family>!App` through the qualified Nexus application launcher. Verify its fresh package
   identity, CoreWindow, real UIA TextBlock, AppContainer token, PID and birth. Move only its window behind.
3. `python -I -B probe.py --plan <plan>` is an independent read-only OS oracle. Freeze its main PID and the exact
   **string** `creation_filetime`, not a lossy JavaScript integer. Prefer a natural state3; do not poll Nexus look
   while preparing suspension, because it intentionally wakes the app.
4. If a deterministic suspension is needed, `probe.py --action suspend --apply --expect-pid <pid>
   --expect-birth <creation_filetime> --plan <plan>` uses the public API once, only for this unique owned fixture.
   It requires an actual AppContainer, unchanged executable/manifest and exact main PID/birth. It must attest3.
5. Invoke the final Nexus engine's real `look` once. Positive proof requires state1 from the independent probe,
   unchanged PID/birth, the native result's `storeResume.requested:true`, fresh content/frame, and the XAML text's
   incremented resume counter. Record the original foreground independently and check it was preserved.
   A read-only polling observation may wait for text readiness, but must never be mistaken for another Resume.
6. Close only this private fixture normally (its `Close private fixture` button calls `Application.Exit`).
   In failure cleanup, explicit `probe.py --action resume --apply ...` may restore only this owned package;
   its receipt says `controller_origin:private-fixture-control-not-Nexus-engine` and is never engine evidence.
   Finally `register.ps1 -Plan <plan> -Remove -Apply` removes only the exact private development identity.

No registration, launch, suspension, image, or successful lifecycle is established merely by compiling this harness.
