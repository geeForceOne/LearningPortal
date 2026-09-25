# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

LearningPortal is a multi-user, Docker-hosted web app that turns a user's own study material into training exams using AI.

- **Topics.** A user creates topics. Each topic holds any number of content materials: uploaded PDF, Word, PowerPoint, LaTeX, or Markdown files, or text pasted in directly.
- **AI.** Content material is sent to an AI provider (Claude or ChatGPT) for analysis. Each user configures their provider and API key in Settings. In the UI it's just called **"AI"**, with no vendor-specific wording outside Settings.
- **Exams.** A user creates any number of exams per topic and can amend them later. An exam definition has a user-chosen name, a question count, a type (multiple choice, written, or a mix), and a difficulty (easy, medium, or hard). The AI generates the questions and answers.
- **Written answers.** The user types an answer and the AI judges whether it qualifies as correct. Answers are short, at most 10–20 sentences, not essays. The UI should make this limit clear.
- **Revealing answers.** After answering a question, the user can reveal the answer right away or wait until the end of the exam. Every answer comes with a thorough explanation, because the point is to learn from it.
- **Repeat and statistics.** Every exam attempt's result is stored. Exams can be repeated any number of times, and a statistics page shows progress over time.
- **Question bank.** Generated questions and answers, with their explanations, are stored locally and reused across exams, so they aren't regenerated every time.
- **Language.** The UI is English-only, but a topic can be in any language. The questions, answers, explanations, and AI grading for a topic must use that topic's language.
- **Responsive.** The app is designed mainly for PC use but must work well on tablets and phones.

### Product decisions

- **Accounts.** There is no public sign-up. The first user to register becomes the admin, and after that only the admin creates accounts. Each user's data is private, with no sharing between users.
- **AI key.** Each user configures their own AI provider and API key. There is no shared or admin key.
- **Multiple choice.** The AI decides per question whether it has one correct answer or several. The UI clearly shows which kind it is ("choose one" vs. "choose all that apply").
- **Written grading.** The AI returns a score from 0 to 100% plus feedback on what was right and what was missing. The UI labels a score of 80% or more as *correct*, 1–79% as *partially correct*, and 0% as *incorrect*.
- **Programming topics.** A topic can be marked as a programming topic (at creation or later). Its exams have a code share (default 40%): that many questions work with code, the rest are theory. Other topics get theory questions only. Each question is tagged code or theory. Changing the flag only affects newly generated questions and never regenerates on its own; changing an exam's share is a settings change that swaps just enough questions to match.
- **Question bank reuse.** Each exam has a reuse percentage (default 20%): up to that share of its questions comes from the topic's bank (matching type, difficulty and code/theory, least-used first), and the AI writes the rest. New questions must not repeat what the bank already asks. When a topic's bank is large compared to its material, the UI says new questions will increasingly overlap.
- **Repeating an exam.** A repeat uses the same questions, with the question order and the multiple-choice options shuffled.
- **Amending an exam.** The user can:
  - edit its settings (name, count, type, difficulty) and regenerate
  - remove a question, or have the AI replace one
  - manually edit a question, its answer, or its explanation
  - add their own questions
- **Attempts.** There is no timer, but each attempt's duration is recorded. Every answer is saved as soon as it's given, so the user can leave and resume an unfinished attempt later.
- **What's new.** After an update, each user sees a banner ("Recall was updated to X.Y.Z", linking to the release notes) until they close it. Users are never told about the version they first saw; local "dev" builds show nothing.
- **Statistics.** The page shows:
  - score over time for each exam
  - a per-topic overview (average score, attempt count, last practiced)
  - weak questions (the ones most often answered wrong)
  - a breakdown by question type and difficulty

## Commands

```
dotnet build LearningPortal.slnx              # build everything
dotnet run --project src/LearningPortal.Web   # run the app locally
docker compose up --build                     # build and run the container
```

This repo uses the newer `.slnx` XML solution format; there is no `.sln` file.

## Workflow

When the user gives feedback or requests changes in a back-and-forth (bug reports, tweaks, "also do X"), don't implement each one immediately as it comes in. Acknowledge it, queue it, and keep gathering — only start making changes once the user explicitly says "go" (or an equally unambiguous confirmation). This lets them batch up a full list of feedback before any code gets touched. Purely informational questions (no code change requested) can still be answered right away.

## Git workflow

Never run `git commit` or `git push` unless the user explicitly asks for it in that turn. Finish the change and leave it uncommitted in the working tree; tell the user it's ready and wait for an explicit go-ahead before committing (and again before pushing — a commit approval does not imply a push approval, and approval from an earlier turn does not carry forward to later changes).

When the user does ask you to commit:
- Write clean, specific commit messages describing *why*, not just what changed.

## Architecture

Uses the latest .NET (currently .NET 10, `net10.0`) with Blazor Server. It is split so that all business logic can be reused by a future Android or iOS app. This version is web-only, so don't build mobile clients or public APIs until asked.

- **`src/LearningPortal.Core`** (class library, no dependency on the web UI) holds the domain models, persistence, AI access, document text extraction, exam generation and grading, and statistics.
  - Every long-running method takes an `IProgress<string>?` (for status/log messages) and a `CancellationToken`. There is no `Console.*` or other I/O baked into Core, so the same methods can drive a UI progress display and cancellation.
- **`src/LearningPortal.Web`** (Blazor Server) holds the UI, authentication, and hosting. It is packaged for Docker via `Dockerfile` and `docker-compose.yml`.
  - Uses interactive server render mode with prerendering off, so JS interop and browser storage are available immediately.

### Hosting and data
- The database is **SQLite**, accessed through EF Core.
- Everything persistent (the SQLite database, uploaded content files, and Data Protection keys) lives in a Docker **volume**, never in the image or container filesystem. Paths are configured through environment variables.

### Authentication
- **Phase 1:** email and password login with ASP.NET Core Identity, stored in the same SQLite database. New accounts use their email as the Identity UserName (kept in sync when the email changes); accounts created before that keep their own username, which still works as a login. Each account has an optional display name shown in the UI.
- **Later phase:** add SSO with Google, Apple, and Facebook on top of Identity as external logins. Keep the auth code structured so these can be added without reworking it.
- Each user's data (topics, content, exams, attempts, settings) is isolated to that user. Every query in Core is scoped by the user's ID.

### Document text extraction
- PDF files use **PdfPig**. Word (`.docx`) and PowerPoint (`.pptx`) files use **DocumentFormat.OpenXml**; a presentation becomes one section per slide, with its speaker notes. Old binary `.doc`/`.ppt` files aren't supported: the upload asks for "Save as" .docx/.pptx or PDF.
- LaTeX and Markdown files, and pasted text, are stored and sent as plain text.

### Secrets
- A user's AI API key is stored encrypted with ASP.NET Core Data Protection. A stored value that can no longer be decrypted is treated as "not set", and Settings tells the user it can't be read and needs to be entered again. The key must never silently disappear or be sent to the browser.

## Conventions

- **Verify external APIs, don't work from memory.** Before coding against the Claude or OpenAI API, an SSO provider, or a document-parsing library, check its current official docs or spec. Model IDs and SDK shapes change.
- **AI-written text.** Show questions, options, answers, explanations and grading feedback through the `RichText` component (safe Markdown with code blocks and syntax colouring), never as raw text or unescaped HTML.
- **Shared dialogs.** Use reusable `ConfirmDialog`, `PromptDialog`, and `Toast` components in `Components/Shared/`, which are awaited from code, instead of writing one-off modals per page.
- **Hand-written CSS design system** in `wwwroot/css/app.css`. It is dark-first, with a light theme via `prefers-color-scheme`, and uses no component library. It must be responsive at desktop, tablet, and phone widths.
- **Gotcha:** don't name a component parameter `Assets`, because it collides with `ComponentBase.Assets` in .NET 10.

### Sending content to the AI
- **On ingest.** Extract each material's text once, store it, and record an estimated token count. Have the AI produce a short outline (sections and key concepts) for each material. This outline is the "analysis" step and is stored for reuse.
- **Topic fits the budget.** If the topic's total material is under a configurable threshold (default roughly 100–150k tokens; check current model context limits in the docs), send all of it for exam generation. Use prompt caching so follow-up calls on the same topic (replacing a question, generating more) reuse it cheaply.
- **Topic exceeds the budget.** Split the material into sections, by headings where possible or by size otherwise, and spread questions across them. Each call sends the outlines plus only the sections being covered. Prefer the sections the question bank covers least, so questions spread across the whole topic over time.
- **Source tracking.** Every question stores its source material and section. Grading a written answer sends only the question, the reference answer, and that source section, never the whole topic. The UI can show where a question came from.
- **Guardrails** (users pay with their own keys):
  - a per-file upload size limit (default 50 MB)
  - the token count shown on each topic
  - a confirmation before an unusually large generation
