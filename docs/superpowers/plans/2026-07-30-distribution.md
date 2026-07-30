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

**Still open: the clean-install gate**, which needs a machine that has never had the
SDK. Windows Sandbox covers this — free, built into Windows 11 Pro, and clean on
*every* run rather than clean once. It is not yet enabled here.

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

### Task D5: The public repository

**The first irreversible step.** Everything above is checkable privately.

- [ ] **Step 1: Review what is about to become public.** Every file, the whole
  commit history, and the spec documents. Nothing secret is expected — certificates
  have always been gitignored — but "expected" is not "checked", and the check is
  cheap compared to a force-push after the fact.
- [ ] **Step 2: Create the repository and push.**
- [ ] **Step 3: A README** with the download link, one editor screenshot, the
  hotkey, and a plain paragraph about the SmartScreen warning and why it appears.

**Verification:** a search of the full history for key material, tokens and
absolute paths containing the user's name, before the push and not after.

---

### Task D6: The release feed and auto-update

**Files:** `src/Snipwhiz.App/Update/`, `scripts/release.ps1`.

- [ ] **Step 1: Publish 1.0.0** to GitHub Releases.
- [ ] **Step 2: Check on start, apply on restart.** After the window is up, never
  before — spec 1 spent real effort making capture instant and an update check on
  the startup path would spend it back.
- [ ] **Step 3: Silent on failure.** No network, a rate-limited GitHub, a corporate
  proxy — all ordinary, none worth a dialog about a problem the user cannot fix and
  does not have.
- [ ] **Step 4: A quiet "restart to finish updating" affordance**, and nothing else.
  No changelog window, no "what's new", no nagging.

**Verification:** install 1.0.0, publish 1.0.1, launch, confirm it updates and
restarts into the new version — **with captures taken under 1.0.0 still present and
openable**. **Negative controls, three:** point the app at an unreachable feed and
confirm it works exactly as before with no dialog; publish a deliberately broken
1.0.2 and confirm the previous version is still on disk and runnable; and confirm
an update does not touch the library by watching the data directory across the
version bump.

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
- **No downgrade path for the library format.** Newer reads older; the reverse is
  untested, so rolling back across a schema change is unmapped territory.
