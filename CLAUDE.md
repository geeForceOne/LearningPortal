# CLAUDE.md

LearningPortal ("Recall") is a multi-user, Docker-hosted Blazor Server app that turns a user's own study
material into AI-written training exams. Users create topics, add material (PDF, Word, PowerPoint, LaTeX,
Markdown, pasted text), and generate exams that the AI writes and grades. Each user brings their own
Claude or OpenAI key (an admin can share one).

**Before changing or adding a feature, read its entry in `docs/product-decisions.md`.** It holds the agreed
behavior for every feature (accounts, shared key, exams, grading, bank reuse, chats, uploads, printing...)
that the code alone doesn't explain. Update the entry when a decision changes.

## Commands

```
dotnet build LearningPortal.slnx              # build everything (.slnx, there is no .sln)
dotnet run --project src/LearningPortal.Web   # run locally
docker compose up --build                     # build and run the container
```

## Architecture

- .NET 10 (`net10.0`), Blazor Server, interactive server render mode, prerendering off.
- `src/LearningPortal.Core`: all business logic (models, EF Core + SQLite, AI access, text extraction,
  generation, grading, statistics), no dependency on the web UI so a future mobile app can reuse it.
  Long-running methods take `IProgress<string>?` and a `CancellationToken`; no `Console.*` or other I/O.
- `src/LearningPortal.Web`: UI, ASP.NET Core Identity auth, hosting, Docker packaging.
- No mobile clients or public APIs until asked (the Homepage stats endpoint is the one exception).
- Every Core query is scoped by the user's ID; users never see each other's data.
- Persistent data (SQLite, uploads, Data Protection keys) lives in a Docker volume, paths from env vars.
- API keys are encrypted with Data Protection, never sent to the browser. A key that can't be decrypted
  counts as "not set" and Settings asks for it again; it must never silently disappear.
- Auth: email/password now; Google/Apple/Facebook SSO later as external logins, so keep auth code open
  for that. New accounts use their email as the Identity UserName.

## Conventions

- **Verify external APIs, don't work from memory.** Check current official docs before coding against the
  Claude or OpenAI API, an SSO provider, or a parsing library (PdfPig, DocumentFormat.OpenXml).
- **Wording.** UI is English-only, American spelling ("practice", "analyze", "color"), also in comments and
  docs. Say "AI", never a vendor name outside Settings. Say "Recall" for what the app does ("Recall is
  writing questions"); keep "AI" where a machine may be wrong ("Written by the AI") and for provider, cost
  and settings. Say "material", not "documents".
- **Topic language.** Questions, answers, explanations and grading use the topic's language, not English.
- **AI-written text** goes through the `RichText` component, never raw text or unescaped HTML.
- **Dialogs:** use the awaited `ConfirmDialog`, `PromptDialog` and `Toast` in `Components/Shared/`. Clicking
  outside a dialog never closes it.
- **CSS:** hand-written design system in `wwwroot/css/app.css`, dark-first with a light theme
  (`<html data-theme>`), no component library. Works at desktop, tablet and phone widths. Pages span the
  full page width (`--page-max`); never cap content in a narrow column.
- **Cost:** AI actions that can cost noticeably show tokens and cost for the selected model, using the
  shared helpers (`Format.NotableCost`, `Format.LargeGeneration`, `IsLargeGeneration`); cent-level actions
  show nothing. Shared-key spend is recorded through `IAiMeter`.
- **Sending material to the AI:** extract text once and store it with a token estimate; outline each
  material once. Under the topic budget send all material with prompt caching; above it send outlines plus
  the sections being covered, preferring sections the bank covers least. Grading sends only the question,
  reference answer and source section. Guardrails: 50 MB per-file upload limit, token count shown on each
  topic, a confirmation before an unusually large generation.
- **Text extraction:** PdfPig for PDF, DocumentFormat.OpenXml for .docx/.pptx (one section per slide, with
  speaker notes). Old .doc/.ppt aren't supported. LaTeX, Markdown and pasted text are stored as plain text.
- **No secrets in git:** keys, SQLite databases, Data Protection keys, uploads, .env files and local
  settings stay in the ignored `./data` folder or the volume. Check staged files before every commit.
- **Gotcha:** don't name a component parameter `Assets` (collides with `ComponentBase.Assets` in .NET 10).
- Commit messages say *why*, not just what.
