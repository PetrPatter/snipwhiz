# Snipwhiz

A screenshot tool for Windows 11: capture to the clipboard, keep every capture in a
library, annotate it later with objects that stay editable. WPF on .NET 10, packaged
with Velopack.

## Build and test

```
dotnet build                      # Debug
dotnet test                       # 346 tests
scripts\release.ps1               # publish + installer into Releases\
scripts\publish-release.ps1       # upload to GitHub Releases, draft by default
scripts\verify-clean-install.ps1  # install in Windows Sandbox and prove it runs
```

`TreatWarningsAsErrors` is on. A warning is a build failure, so an orphaned `using`
will stop you rather than accumulate.

## Layout

- `src/Snipwhiz.Core` — capture, storage, the scene graph, annotation rendering. No
  UI. Testable without a window.
- `src/Snipwhiz.App` — WPF: the overlay, the library, the editor, the tray.
- `tests/` — xUnit against Core, plus App tests that need a WPF thread.

`Annotation.Render(DrawingContext)` lives in Core and is called by *both* the
on-screen canvas and the flattener. That is what makes WYSIWYG a property of the
design rather than something to keep checking.

## Where decisions are written down

- **`CONTEXT.md`** — the glossary. Terms arrive when they have been argued about, not
  when first used. If a term you need is missing, that is a signal: either you are
  inventing vocabulary the project does not use, or there is a real gap.
- **`docs/adr/`** — decisions that outlive a phase. Written when a choice is hard to
  reverse, surprising without context, and the result of a real trade-off.
- **`docs/design/`** — what each phase was building and why.
- **`docs/plans/`** — the tasks, with checkboxes, and the deviations recorded against
  them. `*Deviated:*` notes are the point: they say what was built instead and why.

Use the glossary's vocabulary in anything you write. If your work contradicts an ADR,
say so rather than quietly overriding it.

## The verification standard

Most of what matters here is not gateable, and pretending otherwise has been the
mistake every time. A person looking has found nearly every defect that mattered in
this project.

So gates come in three kinds and the plans say which is which: automated tests, a gate
run by eye, and a gate run by hand on a clean machine in Windows Sandbox. **Every gate
gets a negative control** — deliberately break the thing and confirm the gate fails.
A gate that has never been seen to fail has not been shown to work.

`src/Snipwhiz.App/Diagnostics/` holds those harnesses, each behind a `SNIPWHIZ_*`
environment variable, several able to sabotage real behaviour so a control has
something to fail against. **They are excluded from Release builds** by a `Condition`
in the csproj, and every call site is inside `#if DEBUG`. Both are load-bearing: the
exclusion alone would not compile, and the guards alone would leave the harness in the
shipped binary. If you add one, guard it the same way.

## Style

Comments explain *why*, and especially why the obvious thing was not done. Several
here exist to stop a future reader "fixing" something deliberate. Match that: if you
work out something non-obvious, write it down where the next person will hit it.
