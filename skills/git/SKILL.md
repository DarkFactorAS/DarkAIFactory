---
name: git
description: >-
  Repository workflow for Git and GitHub operations, including branch naming,
  commits, pull requests, safety checks, and pre-commit validation. Use whenever
  changing branches, staging or committing files, pushing, or working with pull
  requests.
---

## When To Use

Use this skill for any Git or GitHub source-control operation in this repository.

## Branching

- In an existing repository, start each new work session on a new branch before
  making project changes.
- Use `session/<type>-<short-description>` for general work, such as
  `session/bugfix-audio-playback`.
- For Jira work, use `session/<JIRA-KEY>-<short-description>`, such as
  `session/BOTS-864-git-skill`.
- When initializing a brand-new repository, its selected default branch (for
  example, `main`) is the initial setup branch; use session branches for later
  work in that repository.
- Check the current branch and working tree before switching branches. Preserve
  existing user changes; do not discard, overwrite, or fold them into task work.

## Commit Protocol

- Keep commits focused on one resolved action and commit incrementally rather
  than accumulating unrelated completed work.
- Use a clear imperative commit subject. Include the Jira key when applicable,
  for example, `BOTS-864 Add Git workflow skill`.
- Do not finish a task that changed files with its task-owned changes left
  uncommitted. After completing the pre-commit checks, commit all in-scope
  changes to the current session branch before the final response, unless the
  user explicitly asks not to commit or a genuine blocker prevents it.
- Preserve and exclude pre-existing changes unless they are explicitly in scope.
- Stage only the files intended for the commit. Do not use `git add .` or stage
  the entire repository without first reviewing every included change.
- Do not commit files under `UserSettings/`.

## GitHub Workflow

- Before opening a pull request, ensure all unit tests pass and Docker containers
  build and run successfully.
- Do not force-push, rewrite shared history, or create a pull request unless the
  user or task explicitly requests it.
- Never expose credentials, tokens, or other secrets in commits, command output,
  or pull-request content.

## Safety Guardrails

- Inspect `git status --short` before and after source-control operations, and
  inspect the exact staged diff before committing.
- Preserve all changes that predate the task. If they affect what should be
  committed, ask the user how to proceed before committing.
- Do not run destructive commands such as `git reset --hard`, `git clean`, or
  checkout/restore operations that discard changes unless the user explicitly
  requests that action.
- Do not hand-author or recreate Unity `.meta` files. Let the Unity Editor
  generate them, and preserve tracked `.meta` files so asset GUID references
  remain stable. See `.github/copilot-instructions.md` for the Unity asset rule.

## Pre-Commit Checks

Before creating a commit:

1. Run `git status --short` and verify no unrelated or pre-existing user files
   are included; in particular, exclude `UserSettings/`.
2. Run `git diff --check` and `git diff --cached --check` to catch whitespace
   errors in both unstaged and staged changes.
3. Review `git diff --cached` and confirm every staged file belongs in this
   commit, contains no secrets, and does not include generated or user-specific
   files unintentionally.
4. Run the focused tests and checks for the change. For a full client build, use
   `dotnet build Bots.slnx`; run Unity-specific validation in the Editor
   when the change depends on Unity asset generation or runtime behavior.
5. Recheck the status after validation, since build and Editor workflows may
  generate files that should not be committed.
