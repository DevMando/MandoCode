---
name: commit-message
description: Draft a concise, well-formatted git commit message from the currently staged changes.
---

When the user invokes this skill, follow this workflow exactly:

1. Run `git status --porcelain` via `execute_command` to confirm something is staged.
   If nothing is staged, stop and tell the user to stage their changes first.

2. Run `git diff --cached` to see the staged changes.

3. Run `git log -n 5 --pretty=format:"%s"` so you can match the repo's existing
   commit-message style (sentence case vs. lowercase, prefix tags, scopes, etc.).

4. Draft a commit message:
   - Subject line: imperative mood, under 72 characters, matching repo style
   - Blank line
   - Body (optional): 2-4 lines explaining *why* the change is happening,
     not *what* the diff already shows. Skip the body for trivial changes.

5. Show the message to the user inside a fenced code block. Do NOT commit —
   the user will copy the message and run `git commit` themselves.

Keep it tight. Do not list every file changed; the diff already does that.
