---
name: commit
description: Commit staged/unstaged changes with a Conventional Commits message (type(scope): description) generated from the diff. Use when the user says "commit", "/commit", or asks to commit current changes.
---

# commit

Generate a Conventional Commits message from the current changes and commit them.

## Steps

1. Run in parallel: `git status`, `git diff HEAD`, `git log --oneline -10`.
2. If there are no staged or unstaged changes, report that and stop.
3. Stage relevant files by name (never `git add -A`/`.`). Skip anything that looks like a secret (`.env`, credentials) and warn the user.
4. Derive the message from the diff:
   - **type**: `feat` (new capability), `fix` (bug fix), `refactor` (no behavior change), `docs`, `test`, `chore` (build/deps/tooling), `perf`, `style`.
   - **scope**: the project/module most of the diff touches, e.g. project folder name minus `RayTree.` prefix, lowercased (`core`, `postgresql`, `hosting`, `opentelemetry`). Omit scope if changes span many unrelated areas.
   - **description**: imperative, lowercase, no trailing period, summarizing the *why*/effect, under ~72 chars for the subject line.
   - Add a short body only if the change needs more than the subject line to explain (e.g. breaking change, non-obvious rationale).
5. Commit with the message via heredoc, e.g.:
   ```bash
   git commit -m "$(cat <<'EOF'
   fix(postgresql): revert outbox claim on publish failure
   EOF
   )"
   ```
   Do not add a `Co-Authored-By` trailer unless the user's global git commit conventions elsewhere in this session already require it.
6. Run `git status` after to confirm the commit succeeded. If a pre-commit hook fails, fix the issue, re-stage, and create a NEW commit — never `--amend` unless asked, never `--no-verify`.

## Rules

- Never commit unless this skill or the user explicitly triggers it.
- Never use destructive flags (`--force`, `--hard`, `--no-verify`, `--no-gpg-sign`).
- One logical change per commit; if the diff clearly bundles unrelated changes, ask the user whether to split before committing.
