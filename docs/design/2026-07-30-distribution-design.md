# Distribution — Design Spec

**Spec 3 of ~6.** Getting Snipwhiz onto machines that are not the one it was built
on, and keeping it current once it is there.

Pulled forward out of plan order. The original plan called this "thin; worth
pulling early so the tool can go on real machines for feedback", and three phases
of editor work have made the case louder than the plan did: nearly every defect
that mattered — the rail not lighting, a caption plated in its own ink, a dead
size slider, a stale crop after undo, a callout skating under the pointer — was
found by a person clicking, not by a test. There are now 324 tests and they did
not find any of those. More people clicking is the highest-yield thing available.

---

## 1. Context

Snipwhiz is a self-contained WPF app targeting `net10.0-windows10.0.22621.0`. It
has a tray host, a global hotkey, a capture overlay, a SQLite library and a
non-destructive editor with thirteen tools. It writes to `%LOCALAPPDATA%\Snipwhiz`
and, only with explicit consent, one `HKCU` registry value for autostart.

It has never been installed. It has only ever been run with `dotnet run` from the
directory it was compiled in.

**The audience is friends, family and coworkers** — people who did not build it,
will not read a README, and for whom a scary dialog is a reason to stop. That
audience is the whole design constraint here, and it is what makes the unglamorous
parts (the first-run warning, the update that happens without being asked for)
matter more than the packaging mechanics.

### Two decisions taken before writing this

- **Velopack, not MSIX, not WiX, not Inno.** Reasoning in §4.1.
- **The signing decision does not block the pipeline.** Signing is a step the build
  either has a certificate for or does not, and everything else works either way.
  §4.4 sets out what it costs and what it buys, because it is a recurring bill and
  an identity check rather than a technical choice, and it is not mine to make.

---

## 2. Scope

### In

- A **Velopack** build producing a Setup.exe, a portable zip, and versioned release
  packages with deltas.
- **In-app auto-update**: check on start, download in the background, apply on next
  launch. No update UI beyond a quiet "restart to finish updating" affordance.
- **A release feed** the app can reach, hosted on GitHub Releases.
- **Versioning** driven from one place, so the installer, the assembly, the update
  feed and the About text can never disagree.
- **A signing hook** in the build, plus documentation of what happens without one.
- **First-run behaviour**: what a person sees between double-clicking Setup.exe and
  taking their first screenshot.
- **Uninstall that actually leaves.** Including the question of what happens to the
  library.

### Out

- App Store / Microsoft Store packaging. A different identity model and a review
  queue, for an audience that is being sent a link directly.
- macOS and Linux. Velopack supports them; nothing else in this codebase does.
- Telemetry, crash reporting, licensing. Later, if ever, and each is its own
  consent conversation.
- CI. The build runs on the developer's machine for now; §8 records why that is
  the wrong long-term answer and what it costs to fix.

---

## 3. Architecture

```
  dotnet publish  ──►  publish/            (self-contained, ~90 MB)
         │
         ▼
  vpk pack  ──────►  releases/
         │             Snipwhiz-1.2.0-full.nupkg
         │             Snipwhiz-1.2.0-delta.nupkg      ← only the changed bytes
         │             Snipwhiz-Setup.exe
         │             RELEASES / releases.win.json    ← the feed
         ▼
  gh release upload ──►  GitHub Releases
                              ▲
                              │ checked on start
                        UpdateManager (in-app)
```

**Three things are deliberately not in that diagram.** There is no update server to
run, no MSI to author, and no per-machine install: Velopack installs per-user into
`%LOCALAPPDATA%\Snipwhiz`, which is why none of this needs elevation.

---

## 4. Design decisions

### 4.1 Velopack, and what it is instead of

| Option | Why not |
|---|---|
| **MSIX** | The modern Microsoft answer, and wrong for this. It wants a signing identity before it will install at all, runs the app in a container that complicates the global hotkey and the tray, and its update model assumes the Store or an enterprise feed. |
| **WiX / MSI** | Per-machine, needs elevation, and authoring is an XML dialect nobody remembers. Auto-update is a separate problem it does not solve. |
| **Inno Setup / NSIS** | Fine installers with no update story. The update story is the expensive half. |
| **ClickOnce** | Solves both, and produces an install experience from 2008. |
| **Velopack** | Installer, deltas and self-update from one command, per-user, no elevation, no server. Successor to Squirrel with the same shape and fewer sharp edges. Version 1.2.0, June 2026, actively maintained. |

**The delta packages are the reason this matters at all.** A self-contained WPF app
is roughly 90 MB. Sending that to someone every time a caption bug is fixed is not
a thing anyone will tolerate twice; a delta over an unchanged .NET runtime is a
small fraction of it. This is exactly what the original stack decision predicted —
"installer ~80–100 MB vs ~10 MB… delta updates mean only the first download is
large" — and it is now due.

### 4.2 One version number, derived from the tag

Four things carry a version: the assembly, the installer, the release feed and
whatever the app shows a human. Four places to set it is three places to forget.

`Directory.Build.props` holds it, the build passes it to `vpk`, and a `git tag` is
what changes it. **The About text reads its own assembly** rather than a constant,
so it cannot claim a version the binary is not.

### 4.3 Update on start, apply on restart — never mid-session

Checking at launch and applying at the next launch is the only model that cannot
interrupt anyone. The alternative — swapping files under a running app — is how an
update takes a screenshot away from someone mid-annotation.

**The check is best-effort and silent on failure.** No network, a rate-limited
GitHub, a corporate proxy: all of these are ordinary, none of them is worth a
dialog. An app that cannot reach its update feed is an app that works exactly as
it did yesterday, and telling someone about it is telling them about a problem
they cannot fix and do not have.

**Nothing about the tray or the hotkey may wait on this.** The check runs after the
window is up. Spec 1 spent real effort on capture being instant; an update check on
the startup path would spend it back.

### 4.4 Signing: what it costs, what it buys, and what happens without it

**Unsigned, a person double-clicking Setup.exe sees "Windows protected your PC"**,
a blue full-window dialog with a "Don't run" button and a "More info" link that
has to be clicked before "Run anyway" appears. It is not a warning about anything
being wrong; it is what SmartScreen shows for a file it has not seen before. To
the audience this spec exists for, it reads as "this software is dangerous".

Three routes, and the choice is the user's because it is money and an identity
check rather than an engineering trade:

| | Cost | Identity | First-run experience |
|---|---|---|---|
| **Unsigned** | free | none | Warning on every release, forever — reputation is per-file, so each new version starts over |
| **Azure Artifact Signing** (formerly Trusted Signing) | ~$10/month | Verified; individuals limited to USA and Canada | Warning early, fading as reputation accrues **to the publisher** rather than the file |
| **EV certificate** | ~$400+/year plus a hardware token | Verified, stricter | No warning, immediately |

Two things worth being plain about. **Signing is not a magic trust switch** — with
Artifact Signing or a standard OV certificate the early downloads still get warned;
what changes is that the reputation accumulates against an identity instead of
resetting every release. And **the hardware token requirement on OV/EV certificates
is real**: signing must happen on the machine holding the token, which is why the
managed service exists.

**The pipeline is built with signing as a hook and no certificate configured**, so
the decision can be made later without redoing anything. `*.pfx` and `*.snk` are
already gitignored and must stay that way; a managed service is preferred partly
because there is no key file to leak.

### 4.5 Per-user install, and therefore no elevation

Velopack installs to `%LOCALAPPDATA%\Snipwhiz`. No UAC prompt, no admin rights, no
Program Files. This also means an update never needs elevation, which is what makes
§4.3's silent background download acceptable.

It costs one thing: the app is installed per-Windows-account rather than per-machine.
For this audience that is correct — these are personal laptops, not managed fleets.

### 4.6 The library survives an uninstall, and says so

Uninstalling removes the app from `%LOCALAPPDATA%\SnipwhizApp`. The **library** —
captures, projects, the database — lives in `%LOCALAPPDATA%\Snipwhiz` and is the
user's data, not the application's.

**Those two directory names are the whole mechanism, and this section originally
got it wrong.** It claimed the app installed to `%LOCALAPPDATA%\Snipwhiz\app-*`,
*beside* the library. It does not: Velopack installs into `%LOCALAPPDATA%\<packId>`
and its uninstaller removes that directory whole. With a packId of `Snipwhiz` the
directory it removes **is** the library, and a real uninstall in Windows Sandbox
deleted every screenshot the user had taken. The app therefore gets a packId of
`SnipwhizApp` and its own directory; the display name comes from `packTitle` and is
unchanged.

No amount of care in the uninstall hook could have prevented this, which is the
point — the hook is the app's code, and the deletion is Velopack's. Only a real
install and a real uninstall could show it.

**Deleting someone's screenshots because they uninstalled an app is not a decision
an uninstaller gets to make silently.** It stays, and the uninstall path says
where it is so somebody who does want it gone can remove it. This is the same
reasoning that made §4.10 of the editor spec keep annotations outside a crop.

### 4.7 First run, in order

1. Setup.exe, no prompts, no options, no install directory question.
2. The app starts. The tray icon appears.
3. **A single first-run window** — not a wizard — that says the hotkey, offers
   autostart with the consent checkbox that already exists, and gets out of the way.
4. Nothing else. No account, no tour, no "what's new".

The hotkey is the one thing a person cannot discover on their own, which is why it
is the one thing that window exists to say.

### 4.8 A place to send people

A one-page README with a download link, one screenshot of the editor, the hotkey,
and a plain-English paragraph about the SmartScreen warning if the build is
unsigned. Being told in advance that a scary dialog is coming and why is the
difference between a warning and an alarm.

---

## 5. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **SmartScreen stops people at the door** | §4.4; README warns in advance; signing decision surfaced early rather than discovered on first send |
| 2 | **The first install on a foreign machine fails in a way that never happens here** | The verification below is a real install on a machine that has never had the SDK, not a local `vpk pack` |
| 3 | **An update breaks the app and there is no way back** | Velopack keeps the previous version; verify rollback deliberately rather than assuming it |
| 4 | **The library is lost or orphaned by an update** | Install path and data path are separate by construction; verified across an actual version bump with real captures present |
| 5 | **A background update download on a metered connection** | Deltas are small; check is once per launch, not polled |
| 6 | **Signing key handling** | Managed service preferred; no key file on disk; `*.pfx` gitignored already |

---

## 6. Verification

The standing rule applies — **a guard must be observed failing before it is
trusted** — and this spec is where it is hardest to honour, because the subject is
a one-way action on someone else's computer. Three of these need a second machine
or a VM, and that is the point.

| Gate | What it proves | Negative control |
|---|---|---|
| **Clean install** | Setup.exe works on a machine with no .NET SDK, no Visual Studio and no prior Snipwhiz | Run it in a VM with the SDK removed; a build that secretly needs it fails here |
| **Update across versions** | 1.0.0 installed, 1.0.1 published, app updates itself and restarts into the new one | Publish a 1.0.1 whose feed the app cannot reach and confirm it keeps working silently, per §4.3 |
| **Library survives an update** | Captures taken in 1.0.0 are present and openable in 1.0.1 | Point the install at the library directory and confirm the check catches it |
| **Rollback** | The previous version is still on disk and can be run | Ship a deliberately broken 1.0.2 and recover from it |
| **Uninstall** | App gone; library still there and findable | Confirm an uninstaller that deletes the data directory fails this |
| **Version agreement** | Assembly, installer, feed and About text all say the same thing | Set the tag and the props file to different values and confirm the build refuses |

---

## 7. Known limitations

- **The build runs on one machine.** No CI, so releases are reproducible only in
  the sense that one person can reproduce them. The moment signing is real this
  should move, because a signing credential on a laptop is the thing worth moving
  it for.
- **No crash reporting.** When a friend says "it stopped working" there will be
  nothing to look at. Deliberate for now — it is a consent conversation, not a
  feature — but it is the obvious next gap.
- **GitHub Releases as a feed** means the repository has to be public, or every
  installer needs a token. Public is assumed.
- **No downgrade path for the library format.** A newer version can read older
  projects; the reverse has never been tested, and rolling back an app across a
  schema change is untested territory.
