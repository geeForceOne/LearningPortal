---
name: release
description: Release a new Recall version. Use when the user asks for a release, a version bump, or to publish.
---

# Releasing Recall

## 1. Questions first, one at a time (AskUserQuestion, numbered, recommendation first)
Ask before touching any code, then work without further questions:
1. Which changes go into the release notes (list everything since the last tag; the user may add lines
   such as "Minor fixes and updates.").
2. The version number.
3. README: concrete options (features to add, screenshots to retake, leave as is).
4. Any major dependency bump or new AI model found in steps 2-3 below, as a short follow-up.

Also read the Ideas Inbox: tick the Recall ideas this release ships and move them to Done with a one-line
note (pre-approved). Mention other open Recall ideas in one line; never queue them.

## 2. Dependency check (every release)
- `dotnet list LearningPortal.slnx package --outdated`; keep the `dotnet-ef` tool in step with EF Core.
- Docker base images in `Dockerfile`, Actions versions in `.github/workflows/docker-publish.yml`,
  bundled libs in `src/LearningPortal.Web/wwwroot/lib`.
- Patch/minor: bump, build, test. Major or breaking: ask (step 1.4). Check release notes of the Anthropic
  SDK, PdfPig, OpenXml and EF Core against the official docs.
- Deferred by the user: MailKit 5, .NET 11 packages. Mention MailKit again, don't bump it.

## 3. Monthly model and price check (only when the month differs from the last release month)
Last release month: **November 2026**. Update this line after every release.
1. Read the official Anthropic and OpenAI model and pricing pages (never from memory).
2. Update `AiModels` in `src/LearningPortal.Core/Ai/AiClient.cs`: models, defaults, prices,
   `CachedInputPrice`, `CacheWriteFactor`, long-prompt factors; set `AiModels.PricesAsOf`.
3. Check the wiki how-tos (Get-a-Claude-API-key, Get-an-OpenAI-API-key) against current docs. The wiki is
   the `LearningPortal.wiki` repo (clone at D:/Programming/LearningPortal.wiki); commit as 1780268+geeForceOne@users.noreply.github.com. Wiki fixes may be
   pushed without asking. Matching app wording: Settings key help, `AppInfo.KeySafety`.

## 4. Build the release
- Work on a branch; never on main.
- Add `## X.Y.Z - YYYY-MM-DD` to `CHANGELOG.md` (newest first, `### New` / `### Improved` / `### Fixed`,
  written for users, American English). The app shows this file on /release-notes.
- Commit only after the user's OK in that turn.

## 5. Stop for testing
End with a running test instance and a hand-over: what was and wasn't tested. Nothing public yet. Skip the
stop only if the user says to publish without testing.

## 6. Publish (only after the user's OK)
- Push main and the tag `vX.Y.Z` (the Docker workflow triggers on `v*.*.*` tags).
- `gh release create vX.Y.Z --title "X.Y.Z" --notes-file <that CHANGELOG section>`. Title is the bare
  version. Never write the GitHub notes separately.
- Afterwards: `git pull --ff-only origin main`, `git branch -d` merged branches, `git fetch --prune`; report
  what was pruned.
- If a publish command was rejected or interrupted, check what actually ran (`git ls-remote --tags origin`,
  `gh release view`, `gh run list`) before saying anything about it.
