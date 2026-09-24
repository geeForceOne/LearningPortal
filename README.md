# Recall

A self-hosted web app that turns your own study material into practice exams with AI, and
explains every answer so you learn from it.

Upload your notes, slides or scripts into a topic, and Recall writes multiple-choice and written
questions about them, grades your written answers, and tracks how you improve over time.

![Sign in](docs/images/recall-signin.png)

## Topics and material

A topic holds any amount of study material:

- Upload **PDF, Word (.docx), PowerPoint (.pptx), LaTeX or Markdown** files, or paste text in
  directly. PowerPoint slides come in one section per slide, speaker notes included
- Each material's text is extracted once and outlined by the AI, so later requests only send
  what's needed
- The token count is shown on each material and topic, so you can see what the AI will read
- A topic can be in any language. Questions, answers, explanations and grading use that
  language, while the app itself stays in English

## Exams

Create any number of exams per topic: pick a name, a question count, the type (multiple choice,
written, or a mix) and the difficulty (easy, medium or hard). Optionally, give the AI
instructions such as "focus on dates and names".

- **Multiple choice** questions say clearly whether there's one right answer or several
- **Written** answers are short (a few sentences, not an essay). The AI scores them from 0 to
  100% and explains what was right and what was missing
- Reveal each answer right away or wait until the end. Every answer comes with a thorough
  explanation and a link to the part of the material it came from
- There's no timer, but every attempt's duration is recorded. Answers are saved as you go, so you
  can leave and resume an attempt later
- Repeat an exam as often as you like, with the question order and the options shuffled
- Amend an exam later: edit its settings, remove or replace a question, edit a question by hand,
  or add your own

## Question bank

Every generated question is kept in the topic's bank and can be reused by later exams, which
costs nothing. Each exam chooses how much to reuse (20% by default), so practice stays mostly
fresh. New questions are checked against the bank, and repeats are dropped. When a topic's bank
gets close to what its material can support, Recall tells you.

## Statistics

- Score over time for each exam
- An overview per topic: average score, attempt count, last practised
- Weak questions: the ones you get wrong most often
- A breakdown by question type and difficulty

## AI and costs

Each user brings their own **Claude or ChatGPT API key**, set in Settings. Keys are stored
encrypted and never sent to the browser. There's no shared key, and usage is billed to that
user's own account.

Because it's your money, Recall shows the expected tokens and cost for your chosen model next to
anything that costs noticeably, and asks before an unusually large generation. When a topic fits
the model's budget it is sent whole and cached, so follow-up requests are cheap. Larger topics are
worked through section by section.

## Accounts and email

There's no public sign-up. The first person to open a new installation creates the
administrator account, and after that only the admin creates accounts. Each person's topics and
results are private.

The admin sets up outgoing email under **Admin → Email settings** (any SMTP server, with
shortcuts for Gmail and Brevo). With email set up:

- New users get an **invite link** to choose their own password
- **Forgot password?** on the sign-in page sends a reset link

Without email, the admin gets the same links on the Users page to pass on by hand.

## With Docker

Using docker compose:

```yaml
services:
  recall:
    image: ghcr.io/geeforceone/recall:latest
    container_name: recall
    ports:
      - 8080:8080
    volumes:
      - recall-data:/data
    restart: unless-stopped

volumes:
  recall-data:
```

or docker run:

```bash
docker run -d --name recall \
  -p 8080:8080 \
  -v recall-data:/data \
  --restart unless-stopped \
  ghcr.io/geeforceone/recall:latest
```

Then open http://localhost:8080 and create the admin account.

Everything that needs to persist lives in one volume mounted at `/data`: the SQLite database,
uploaded files, and the encryption keys that protect stored API keys and the SMTP password.
Back up that volume, and don't lose the keys: without them, saved keys have to be entered
again.

Optional settings, as environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `LearningPortal__MaxUploadBytes` | `52428800` (50 MB) | Upload limit per file |
| `LearningPortal__TopicTokenBudget` | `120000` | Topics up to this size are sent whole; larger ones section by section |
| `LearningPortal__LargeGenerationWarnTokens` | `60000` | Ask before generations that send more than this |
| `Storage__DataDirectory` | `/data` | Where the database, files and keys are kept |

If you run it behind a reverse proxy under its own address, enter that address in
**Admin → Email settings** so links in emails point to it.

## Build from source instead

```
git clone https://github.com/geeForceOne/LearningPortal.git
cd LearningPortal
docker build -t recall .
docker run -d --name recall -p 8080:8080 -v recall-data:/data recall
```

The repository's own `docker-compose.yml` does the same with `docker compose up -d --build`.

## Publishing a new image

Pushing a version tag builds the image on GitHub and publishes it as
`ghcr.io/geeforceone/recall` (`latest`, plus the version):

```
git tag v1.0.0
git push origin v1.0.0
```

It can also be started by hand under **Actions → Publish Docker image**. Ordinary pushes to `main`
don't publish.

## Development

```
dotnet build LearningPortal.slnx              # build everything
dotnet run --project src/LearningPortal.Web   # run locally
```

Built with .NET 10 and Blazor Server. See `CLAUDE.md` for the architecture.

## About this project

This project was developed entirely by [Claude Code](https://claude.com/claude-code), Anthropic's
AI coding agent. It is provided as-is, without warranty of any kind, express or implied. The
author accepts no responsibility or liability for any loss, damage, or costs arising from its
use, including charges from AI providers. Use at your own risk.

## License

Released under the [MIT License](LICENSE).
