# Issue tracker: Local Markdown

Issues for this repo live as markdown files in `.scratch/`.

**`.scratch/` is gitignored.** This repo is public; issue drafts are not meant to
be. The same treatment `.superpowers/` already gets. If you decide a particular
document belongs in the public history, move it into `docs/` deliberately rather
than un-ignoring the directory.

## Specs and plans do not live here

**Departure from the skill's default**, and the one thing worth reading before
creating a file.

This repo already had a spec convention before these skills arrived:

- `docs/superpowers/specs/YYYY-MM-DD-<name>-design.md` — what is being built and why
- `docs/superpowers/plans/YYYY-MM-DD-<name>.md` — the tasks, with checkboxes and
  recorded deviations
- `docs/superpowers/mockups/` — anything visual a spec refers to

Six of these exist and they are the project's actual design record, including the
verification standard the whole repo works to. The skills' default would put new
specs at `.scratch/<feature>/spec.md`, which would quietly fork the documentation
into two systems that drift apart.

So: **a spec or a plan goes in `docs/superpowers/`, tracked in git. `.scratch/`
carries issues only.** When a skill says "write the spec", write it there and in
that format.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`
- Issues are one file per ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`,
  numbered from `01` — never a single combined tickets file
- Triage state is a `Status:` line near the top of each issue file; see
  `triage-labels.md` for the role strings
- Comments and conversation history append to the bottom under a `## Comments` heading

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/issues/`, creating the directory
if needed.

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the
issue number directly.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a file with one **child** file per ticket.

- **Map**: `.scratch/<effort>/map.md` — the Notes / Decisions-so-far / Fog body.
- **Child ticket**: `.scratch/<effort>/issues/NN-<slug>.md`, numbered from `01`,
  with the question in the body. A `Type:` line records the ticket type
  (`research`/`prototype`/`grilling`/`task`); a `Status:` line records
  `claimed`/`resolved`.
- **Blocking**: a `Blocked by: NN, NN` line near the top. A ticket is unblocked
  when every file it lists is `resolved`.
- **Frontier**: scan `.scratch/<effort>/issues/` for files that are open,
  unblocked, and unclaimed; first by number wins.
- **Claim**: set `Status: claimed` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set
  `Status: resolved`, then append a context pointer (gist + link) to the map's
  Decisions-so-far in `map.md`.
