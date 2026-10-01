# Release notes

## 1.10.0 - 2026-10-01

### New

- **I don't know**: instead of guessing, say you don't know. It counts as incorrect, so the question comes back when you practise your weak questions, and the results show which ones you didn't know.
- **Homepage widget**: admins can show the number of accounts and active users on a [Homepage](https://gethomepage.dev) dashboard, with a token created under Admin settings. "Email settings" is now called "Admin settings".

### Improved

- Ask up to 10 follow-up questions per answer, instead of 3.

### Fixed

- The concept map no longer repeats the file name on every tile for material without headings, such as most PDFs. Sections are named after their subject from the material's outline instead, also on the material page and in where a question came from.

## 1.9.1 - 2026-10-01

### Improved

- Settings, Account and Email settings are laid out section by section across the full page width, each with a short line on what it's for.
- "Let people request an account" has moved to Admin → Email settings, next to the email about new requests.

### Fixed

- Minor fixes and updates.

## 1.9.0 - 2026-10-01

### New

- Each topic page has a new **Go deeper** section with four study tools side by side: practise your weak questions, Explain it back, the concept map and Find gaps.
- Settings links to step-by-step guides for getting a Claude or an OpenAI API key.

### Improved

- Admins see each account's activity on the Users page: when they were last active and how many topics, exams, bank questions and attempts they have, with a choice to sort by name, last activity or attempts. The separate Admin statistics page is gone.

### Fixed

- Minor fixes and updates.

## 1.8.0 - 2026-09-30

### New

- Put Recall on the home screen of your phone or tablet and use it like an app: "Install app" in Chrome on Android, or Share → "Add to Home Screen" in Safari on iPhone and iPad.
- Add an optional expiry date to your API keys in Settings. From 5 days before, a banner reminds you to replace the key; close it and it stays hidden until the next day.
- Admins can let people request an account from the sign-in page (Admin → Users). Each request comes with the person's name and an optional message, and the admin approves it (the usual invite is sent) or declines it (with a short email).

### Fixed

- Minor fixes and updates.

## 1.7.0 - 2026-09-29

### Fixed

- Minor fixes and updates.

## 1.6.0 - 2026-09-29

### New

- Questions are now written in the background. You can leave the page or close the browser while the AI writes them; they're there when you come back, and the topic page shows which exam is still being written.
- Concept map: each topic's material, section by section, coloured by how well you do on its questions. Pick a section to practise it.
- Explain it back: pick a concept from your material (or "Surprise me") and explain it in your own words. The AI asks one or two questions like a curious student, then tells you what you explained well and what was missing.
- Gap finder: the AI lists what a course on your topic usually covers that your material doesn't, and can write a short study note for each gap. These come from the AI's general knowledge, so they're clearly marked and can be wrong.

### Improved

- Claude Sonnet 5.5 is now in the model list, at the same price as Sonnet 5.

### Fixed

- Minor fixes and updates.

## 1.5.0 - 2026-09-29

### New

- Practice weak questions: a topic's questions you've answered wrong or only partly right most often, in one practice run. It's 10 questions by default (you can change that), reuses your existing questions, and costs nothing. Start it from the topic or from Statistics, and keep a run as a normal exam with "Save as exam".
- Ask the AI a follow-up about a revealed answer, such as "why isn't B right?". You can ask up to three per question, and the replies are saved with the attempt, so they're still there on the results.

### Improved

- The logo's yellow dot now moves while the AI works and when you point at the logo, with three different motions. Nothing moves if your device is set to reduce motion.

## 1.4.0 - 2026-09-28

### New

- In Advanced mode, the "Outline with" and "Write with" pickers take a custom model ID. It's used for that action only and doesn't change your default in Settings.

### Improved

- The "Outline with" and "Write with" pickers only list models from providers you have an API key for. Settings still shows them all.
- API keys and models in Settings each have their own Save button. A newly saved key makes its models available right away.

### Fixed

- The "Score over time" chart on Statistics no longer flickers when you hover over a point.

## 1.3.0 - 2026-09-27

### New

- Choose a Dark, Light or System theme in Settings. Dark is the default; System follows your device's light or dark setting.

## 1.2.0 - 2026-09-27

### New

- Settings has two model modes. Simple uses one model for everything. Advanced has one model for outlining uploads and one for questions and grading, and lets you pick another model each time you upload material or generate questions.
- When a topic has more than one material, you can choose which material an exam uses. The AI writes questions only from the ticked material, and only its questions are reused from the question bank.

### Improved

- Your Anthropic and OpenAI keys are now side by side in Settings, and an eye button shows the key you're typing so you can check it before saving.
- Questions and outlines show which model wrote them.

## 1.1.0 - 2026-09-26

### Improved

- Claude Opus 5.5 and GPT-6 Sol are now the default models. If you never picked a model in Settings, you use them from now on; a model you chose yourself stays as it is.

### Fixed

- The header no longer shifts sideways when you open Statistics.
- Minor fixes and updates.

## 1.0.0 - 2026-09-25

Initial release.
