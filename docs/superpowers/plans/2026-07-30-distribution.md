# Distribution — Implementation Plan

Spec: [`2026-07-30-distribution-design.md`](../specs/2026-07-30-distribution-design.md)

**Goal:** a person who did not build Snipwhiz can install it, use it, and receive
the next version without being asked to do anything.

---

## Decisions taken with the user before writing this

- **Public GitHub repo, releases as the feed.** Free, no infrastructure, and what
  Velopack targets natively; the app fetches the feed anonymously so no token ever
  ships inside the installer. The repo becoming public is a one-way action and was
  taken deliberately rather than by default.
- **Unsigned for now, with the signing hook in place.** Every recipient will see
  "Windows protected your PC" and have to click *More info → Run anyway*, on every
  release, because unsigned reputation is per-file and never accumulates. The
  README says so in advance, which is the difference between a warning and an
  alarm. §4.4 of the spec records what the two paid routes cost if that becomes
  intolerable.

---

## Global constraints

- **No new registry writes.** Autostart already exists and already asks. Velopack's
  shortcut creation is the only new thing touching the shell, and it is per-user.
- **No elevation, ever.** Per-user install under `%LOCALAPPDATA%`; if any step
  wants admin, that step is wrong.
- **The library is the user's data.** Nothing in this spec deletes it, including
  the uninstaller.
- **Certificates never enter the repository.** `*.pfx` and `*.snk` are already
  gitignored; that stays true even with no certificate to protect.
- **Capture stays instant.** Nothing added here may run before the tray and the
  hotkey are live.

---

## Task order

Packaging first, because until there is an installer nothing else can be checked
on a real machine — and the clean-install gate is the one that finds what a
developer machine hides. The feed and auto-update come after, because they are
meaningless without something to install. Publishing the repo is deliberately
**last of the setup steps and first of the irreversible ones**, so everything that
can be checked privately has been.

---

### Task D1: One version, and a publish that works

**Files:** `Directory.Build.props`, `src/Snipwhiz.App/Snipwhiz.App.csproj`.

- [x] **Step 1: `Directory.Build.props` holds the version**, and every project
  inherits it. Four places carry a version — assembly, installer, feed, About text
  — and four places to set it is three places to forget.
- [x] **Step 2: A self-contained publish** for `win-x64`. Not single-file: it buys
  nothing once an installer exists, and it slows startup.
- [x] **Step 3: About text reads its own assembly**, never a constant, so it cannot
  claim a version the binary is not. It strips the `+commit` suffix the SDK stamps
  onto the informational version, which is real rather than hypothetical.

**Done.** Gate passed: the publish ran with every `dotnet` entry stripped from
`PATH` and `DOTNET_ROOT` unset, against `SNIPWHIZ_ROOT` so it could not reach the
real library — checked by comparing write times rather than assumed.

**The negative control found a hole and it is now closed.** A stray `<Version>` in
any `.csproj` silently beats `Directory.Build.props` — props said 1.0.0, the csproj
said 0.0.1, the stamped assembly said 0.0.1, no MSBuild warning. The single source
is a convention, not an enforcement, so `release.ps1` compares the published
`ProductVersion` against the props version and refuses to pack through the
disagreement. That refusal was then observed happening.

**Verification:** publish, then run the published binary from a directory that is
not the build output, with no SDK on `PATH`. **Negative control:** set the props
version and the assembly attribute to different values and confirm the About text
disagrees with the installer — this is the mistake the single source exists to
prevent, and it should be demonstrable.

---

### Task D2: Velopack packaging

**Files:** `scripts/release.ps1`, `Snipwhiz.App.csproj` (Velopack package reference).

- [x] **Step 1: `vpk pack`** producing Setup.exe, a portable zip and a versioned
  `.nupkg`, into `Releases/` — already gitignored, like every other build output.
  86.5 MB Setup.exe from a 199 MB on-disk publish.
- [x] **Step 2: `VelopackApp.Build().Run()` first in `Main`.** Velopack's hooks run
  on install and uninstall by re-invoking the app with arguments; if anything else
  runs first, an install briefly starts a real app with a tray icon. This needed an
  explicit `Program.Main` and `<StartupObject>`, because WPF generates its own
  entry point from `App.xaml`.
- [x] **Step 3: A signing hook** that is a no-op with no certificate configured, so
  the decision can be made later without redoing this task. Configured by
  environment variable — `SNIPWHIZ_SIGN_TEMPLATE` or `SNIPWHIZ_SIGN_PARAMS` — so no
  certificate detail ever reaches the repository.

**Two negative controls passed, neither needing a second machine.** Removing
`VelopackApp.Build().Run()` and passing `--veloapp-install`: the process was still
running eight seconds later, having started a real tray app, which is exactly the
failure the ordering exists to prevent. And `vpk` refuses to pack a binary whose
`Main` does not call it — a guard that was already there and is better than the
comment describing it.

**Clean-install gate: PASSED**, in Windows Sandbox, via `scripts\verify-clean-install.ps1`.

It proves the app *runs* rather than merely installs. Installing only shows files
were copied; the gate presses Ctrl+Shift+2 and looks for a PNG, which is what shows
WPF started, the hotkey registered, the screen grab worked and SQLite wrote — the
set of things a missing runtime actually breaks. On a machine with no `dotnet` on
`PATH`: installed, launched itself, showed the first-run window, and captured 2,974
KB.

**The gate's first run failed, and found the bug this whole phase exists for.** See
D4 — it was not a packaging detail, it deleted the user's screenshots.

---

### Task D3: First run

**Files:** `src/Snipwhiz.App/FirstRun.xaml`, settings.

- [x] **Step 1: One window, not a wizard** — the hotkey, the autostart offer using
  the consent checkbox that already exists, and a dismiss.
- [x] **Step 2: Shown once**, recorded in settings, never again.
- [x] **Step 3: The hotkey is the headline.** It is the one thing nobody can
  discover on their own.

**It absorbed a question rather than adding one.** A first launch already showed a
"Snipwhiz is running" balloon *and* a separate PrintScreen message box. A third
interruption would have been a wizard assembled by accident, which is the one thing
step 1 says not to build — so the PrintScreen offer moved into this window and the
balloon is suppressed on first run. The offer stays hidden unless the Snipping Tool
actually holds the key, because offering to take a key nothing is using is a
question with no meaning.

Upgrades see it once too: the flag is absent from an older settings file, and the
hotkey is worth saying exactly once to someone who has been using this all along.

**Gate passed, including the control.** Fresh library shows it; restart does not;
clearing the flag by hand brings it back, so the check reads the flag rather than a
coincidence. Three tests cover the two things on the window that are not just text,
and both were watched failing: a pre-ticked autostart box, and an unconditional
PrintScreen offer. Autostart being unticked is a consent invariant rather than a
preference, which is why it is a test and not a comment.

---

### Task D4: Uninstall

- [x] **Step 1: The app goes.** Shortcuts and install directory are Velopack's own
  work. The autostart registry value is not — it goes in
  `OnBeforeUninstallFastCallback`, because a stale `Run` entry pointing at a deleted
  exe fails silently at every login forever and nothing on the machine explains why.
- [x] **Step 2: The library stays**, and the uninstaller says where it is by opening
  the folder — but only when there is something in it.

**The hook runs on a 15-second fuse**, which ruled out the obvious implementation.
Velopack calls `Environment.Exit` the moment the callback returns, terminates it at
15 seconds, and exits `-1` if it throws — so a message box saying where the library
is would hang the uninstaller behind a dialog nobody is looking at, and an
unguarded registry call would fail the uninstall. Hence a spawned Explorer window,
which outlives the process, and a catch around everything.

**Gate passed, including the control.** The real hook, invoked the way Velopack
invokes it, against a throwaway library: autostart value removed, five library
files byte-identical across the uninstall. Adding `Directory.Delete(root)` made the
gate fail, so it is reading the library rather than agreeing with itself.

The gate touches the real `HKCU` Run value because that write has no test seam, so
it saves and restores whatever was there. That turned out to matter — this machine
had autostart genuinely set.

---

**And then the real uninstall deleted every screenshot anyway.**

The local gate above passed while being blind to the actual failure. It ran the
`--veloapp-uninstall` hook, so it proved *this app's code* does not delete the
library — and Velopack deleted it a moment later, from outside the hook entirely.

The cause was a directory name. Velopack installs into `%LOCALAPPDATA%\<packId>`
and its uninstaller removes that directory whole. With a packId of `Snipwhiz` that
directory **is** the library. The spec asserted the app installed to
`%LOCALAPPDATA%\Snipwhiz\app-*`, beside the library; that was simply wrong, and
§4.6 has been corrected. The app now packs as `SnipwhizApp` and owns its own
directory, while the library keeps `%LOCALAPPDATA%\Snipwhiz`. No user data moves,
and the display name comes from `packTitle`, so nothing visible changed.

Settled before anything is published, which matters: Velopack identifies an app by
packId, so changing it after a release would orphan every existing install.

**Re-run in Sandbox: PASSED** — app directory, ARP entry, autostart value and
shortcuts all gone; capture byte-identical; database intact.

**The lesson is about the gate, not the bug.** An automated check that exercises
one layer is blind to the layers around it — the hook is the app's code, the
deletion was the installer's, and no amount of care inside the hook could have
prevented or revealed it. Only a real install and a real uninstall could.

---

### Task D5: The public repository

**The first irreversible step.** Everything above is checkable privately.

- [x] **Step 1: Review what is about to become public.** Every file, the whole
  commit history, and the spec documents. Nothing secret is expected — certificates
  have always been gitignored — but "expected" is not "checked", and the check is
  cheap compared to a force-push after the fact.
- [x] **Step 2: Create the repository and push.**
- [x] **Step 3: A README** with the download link, one editor screenshot, the
  hotkey, and a plain paragraph about the SmartScreen warning and why it appears.

**Verification:** a search of the full history for key material, tokens and
absolute paths containing the user's name, before the push and not after.

**Clean.** The full history across every branch turned up no key material, no
tokens and no absolute paths naming the user; the only matches were prose about the
redaction tool and the gitignore line that excludes certificates. `Releases/` and
`publish/` are ignored and have never been tracked, so the 86 MB artifacts were
never in a commit to find.

**The screenshot argued for a retake, twice, and the third one earned its place.**
The first showed annotations floating on an empty canvas — drawing tools over
nothing, which reads as a drawing app. The second put them on a real capture and
published the user's Start menu recents and live news headlines along with it. The
third redacts those with the editor's own blur, which is the one claim in the README
that prose cannot make: a screenshot tool whose front page shows its redaction
working is doing more than describing itself.

---

### Task D6: The release feed and auto-update

**Files:** `src/Snipwhiz.App/Update/`, `scripts/release.ps1`.

- [x] **Step 1: Publish 1.0.0** to GitHub Releases.
- [x] **Step 2: Check on start, apply on restart.** After the window is up, never
  before — spec 1 spent real effort making capture instant and an update check on
  the startup path would spend it back.
- [x] **Step 3: Silent on failure.** No network, a rate-limited GitHub, a corporate
  proxy — all ordinary, none worth a dialog about a problem the user cannot fix and
  does not have.
- [x] **Step 4: A quiet "restart to finish updating" affordance**, and nothing else.
  No changelog window, no "what's new", no nagging.

**Verification:** install 1.0.0, publish 1.0.1, launch, confirm it updates and
restarts into the new version — **with captures taken under 1.0.0 still present and
openable**. **Negative controls, three:** run with networking disabled and confirm
it works exactly as before with no dialog; publish a 1.0.2 whose payload is
corrupt and confirm the app **is still running 1.0.1** afterwards; and confirm an
update does not touch the library by watching the data directory across the
version bump.

**The second control proves a failed update, not a rollback**, and the two are
different claims — see `CONTEXT.md`. A failed update leaves you on the version you
already had, which is what a corrupt payload produces. Nothing here returns the app
to an older version after a newer one has installed.

**All four gates passed, in two Sandbox sessions, by hand.** 1.0.0 installed from a
mapped Setup.exe, two captures taken, then 1.0.1 found, downloaded as a 176 KB delta
against an 82 MB full package, and applied on exit — with both captures still in the
library afterwards and `%LOCALAPPDATA%\Snipwhiz` unchanged across the version bump.
The corrupt 1.0.2 produced nothing at all: no restart item, no dialog, no error, and
the app still reporting 1.0.1. The second session, with `<Networking>Disable</Networking>`
in the `.wsb`, behaved exactly as an ordinary launch.

**No escape hatch was added to do this.** An environment variable pointing the
updater at a local feed was written and then reverted: Sandbox's own networking
toggle is not a simulation of an unreachable feed, it *is* one, and a repository
with zero downloads is a place where publishing a broken build and deleting it costs
nothing. The shipped 1.0.0 contains no way to redirect where it fetches code from.

**The rollback control passed falsely on the first attempt, and the reason
generalises.** The packages were corrupted before upload — and `vpk upload`
regenerates `releases.win.json` from the files as it finds them, so the manifest
agreed with the corruption and the integrity check would have been satisfied by it.
Both packages had to be corrupted a *second* time and re-uploaded over the assets,
leaving the published manifest holding the first corruption's hashes. **A negative
control that goes through the same tool as the positive path is not a control** —
the tool will make it consistent. Verified by fetching the served asset and the
served manifest independently and confirming they disagree at an unchanged file size.

**Both packages, not just the delta.** Corrupting only the delta would have let
Velopack fall back to the full package and succeed, which looks identical to a
passing positive gate.

**1.0.0's tag was nearly wrong, and that is the quiet one.** The draft release
targeted `main`, which was twelve commits behind the code the artifacts were built
from — publishing it would have created a `v1.0.0` tag naming pre-visual-pass source
next to a post-visual-pass installer, on the one release anyone ever looks at
closely. `main` was fast-forwarded and the artifacts rebuilt from the pushed commit
before the tag existed.

---

## Verification summary

| Gate | Task | Needs a second machine |
|---|---|---|
| Version agreement | D1 | no |
| Clean install | D2 | **yes** |
| First run once | D3 | no |
| Uninstall leaves the library | D4 | **yes** |
| No secrets in history | D5 | no |
| Update across versions | D6 | **yes** |
| Rollback | D6 | **yes** |

**Four of seven need a machine that is not this one.** That is not incidental — it
is the whole point of the phase. A developer machine hides exactly the failures
this spec exists to prevent, and three phases of editor work have already shown
that the defects which matter are found by somebody using the thing rather than by
a test suite.

**Those four are covered by Windows Sandbox**, which ships with Windows 11 Pro and
was overlooked when this plan was written. It is a disposable Windows install with
no .NET SDK, no Visual Studio and no prior Snipwhiz, and it resets completely on
close — so it is not an approximation of the clean machine, it is a stricter one:
clean on every run rather than clean once. Networking is on by default, so the
GitHub feed is reachable and the update gates fit inside a single session.

    Enable-WindowsOptionalFeature -Online -FeatureName "Containers-DisposableClientVM" -All

**One thing it cannot check.** Sandbox cannot reboot, so autostart — the registry
value written with consent, surviving a login — stays unverified on a clean
machine. That is the honest residue of this approach and it belongs in the known
gaps rather than in a gate that quietly never ran.

---

## Known gaps, recorded rather than solved

- **No CI.** Releases are reproducible by one person on one machine. The moment
  signing is real this should move, because a signing credential on a laptop is
  precisely the thing worth moving it for.
- **No crash reporting.** When somebody says "it stopped working" there will be
  nothing to look at. Deliberate — it is a consent conversation, not a feature —
  but it is the obvious next gap once other people are running this.
- **A release that installs and then crashes has no automatic recovery.** The
  corrupt-payload control covers the update that never applies. It does not cover
  the well-formed package that applies cleanly and then dies on launch — Velopack
  will not go back on its own, and the fix is a person running an installer by hand.
  The gate that passes says less than its name suggests.
- **No downgrade path for the library format.** Newer reads older; the reverse is
  untested, so rolling back across a schema change is unmapped territory.
