# Specs

A spec in this directory is a proposal for a change that is not delivered yet: `specs/<YYYYMMDD>-<name>/` with an `overview.md`, one file per phase and an `epic.md` (written by `/dev-workflow:spec`). The code and `docs/` describe how the system behaves now; never cite a spec for current behaviour.

When a phase is delivered, its file is deleted in the change that delivers it, together with the guide update in `docs/`. When no undelivered phase is left, the whole directory is deleted. Git keeps every spec: `git log --all -- specs/<name>` finds it.

## Retired specs

Specs and phases delivered before 2026-11-26 were retired from this directory. Every one is readable at `4883ace1c3e9d1e26d91226051f1fcf013054431`:
`git show 4883ace1c3e9d1e26d91226051f1fcf013054431