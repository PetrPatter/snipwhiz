# Domain Docs

How the engineering skills should consume this repo's domain documentation when
exploring the codebase.

This is a **single-context** repo: one `CONTEXT.md` and one `docs/adr/` at the
root. There are no monorepo signals here — no workspace file, no `packages/` —
so the multi-context layout (`CONTEXT-MAP.md` plus per-context `CONTEXT.md`)
does not apply and is not described here.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root
- **`docs/adr/`** — the ADRs that touch the area you're about to work in

If either doesn't exist, **proceed silently.** Don't flag their absence and don't
suggest creating them up front. The `/domain-modeling` skill (reached via
`/grill-with-docs` and `/improve-codebase-architecture`) creates them lazily,
when terms or decisions actually get resolved.

Both exist. `CONTEXT.md` holds two terms so far and `docs/adr/` holds one decision
— they are grown when something is actually argued about, not filled in up front.

## File structure

```
/
├── CONTEXT.md
├── docs/adr/
│   ├── 0001-....md
│   └── 0002-....md
└── src/
```

## Related, and not the same thing

`docs/superpowers/specs/` and `docs/superpowers/plans/` already hold this
project's design record — what each phase is building, and what was decided
along the way. Those are **per-phase**: a spec is scoped to the work in front of
it and stops being edited once that work ships.

`CONTEXT.md` and `docs/adr/` are the opposite: standing vocabulary and decisions
that outlive any one phase. An ADR is where a choice goes when it will still
constrain the code three phases from now.

If a decision in a spec turns out to be that kind, it belongs in an ADR too — the
spec records why this phase did it, the ADR records that the project does it.

## Use the glossary's vocabulary

When your output names a domain concept — an issue title, a refactor proposal, a
hypothesis, a test name — use the term as defined in `CONTEXT.md`. Don't drift to
synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're
inventing language the project doesn't use (reconsider), or there's a real gap
(note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it rather than silently
overriding it:

> _Contradicts ADR-0007 (...) — but worth reopening because…_
