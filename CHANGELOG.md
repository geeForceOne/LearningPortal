# Release notes

## 1.15.1 - 2026-10-24

### Improved

- **Roomier exam page**: questions get the full page width. Settings and attempts now sit in one strip above them, with your latest scores at a glance, and Delete exam moved to the ⋯ menu.
- **Wider exam taking**: taking an exam and its results use the full page width.
- **Easier long questions**: long questions, answers and explanations are set in a smaller reading size, so a whole text to correct is easier to read.

## 1.15.0 - 2026-10-24

### Improved

- **Longer written answers**: written answers can be as long as the question needs. About 20 sentences is now a suggestion shown under the answer box, not a limit.
- **Uploaded exams keep the task**: each question from an uploaded exam keeps its full task and any text it asks you to work on, such as an answer you're asked to correct.
- **Better Word reading**: text in tables keeps its paragraphs and lists, line breaks and list numbers are kept, and empty fill-in fields are recognized. This helps both material and uploaded exams.

## 1.14.0 - 2026-10-22

### New

- **Upload your own exam**: practice with a real exam paper, like last year's exam. Upload it on the topic page (the same files as material, scanned PDFs too) and Recall copies out its questions in the paper's own words, multiple choice and written. Answers the paper gives are kept; Recall works out the rest from your material and writes an explanation for every question.
- **Check an answer**: ask your own question about a topic and give your answer. Recall answers the question from your material and tells you how right you are and what you missed. You'll find it under "Go deeper" on the topic page. Your checks are kept, and you can save a question to an exam.
- **Questions your material doesn't cover**: for an uploaded exam or a checked answer, you choose whether Recall searches the web (you see the cost first) or the AI answers from its own knowledge. These answers are marked as possibly wrong, and web answers list the pages they come from.

## 1.13.2 - 2026-10-15

### Fixed

- Updated a .NET library with security fixes.
- Minor fixes and updates.

## 1.13.1 - 2026-10-13

### Improved

- **Claude Haiku 5.5** replaces Claude Haiku 4.5 in the model list, at a tenth of the price. If you had Haiku 4.5 selected, you're now on Haiku 5.5. One thing to know: a call that sends more than 100k tokens of material costs five times the listed price, which is still less than Haiku 4.5 cost.

### Fixed

- Minor fixes and updates.

## 1.13.0 - 2026-10-12

### New

- **Flag a bad question**: when a question is ambiguous, wrong or off-topic, flag it with one click, next to a revealed answer or on the exam page. Recall then stops reusing it in new exams and in practice, and it no longer counts in the concept map. It stays in the exams that already have it, marked "Flagged", until you edit, replace or remove it. You can take the flag off again.
- **Exam date and readiness**: give a topic the date of the exam you're studying for, and Recall shows how many days are left. The date is optional. Every topic with material also shows a readiness percentage, on the topic page and on the concept map: each section counts by its size and your average score on its questions, and a section you haven't practiced counts as 0%.

### Fixed

- Minor fixes and updates.

## 1.12.1 - 2026-10-11

### Improved

- **New logo motions**: the logo has four new animations. Its dots gather in the center and spring back, light spreads through the network, the dots scatter and snap back, or the lines draw themselves again. You see them when you hover over the logo, on the sign-in page and while Recall is working.
- **Simpler Settings on a shared key**: the section is now called "API keys in use". While you use the key from your admin, the fields for your own keys are hidden; switch to "My own keys" to see them. The reminder about a low or expiring key updates as soon as you switch.

### Fixed

- A page could stop responding when the same message appeared twice in quick succession, for example when switching quickly between the key from your admin and your own keys.

## 1.12.0 - 2026-10-11

### New

- **Shared AI key**: admins can share an API key of their own with selected people, so they don't need one themselves. Save the key under Admin settings and choose there which models it may be used with, then give it to people on the Users page: without a limit, until a date, or with a balance in dollars that is one-time or per month. Recall estimates what each person has used and stops when the balance is spent or the date has passed. Someone with a shared key sees what's left in Settings and can switch between it and their own keys there.

## 1.11.0 - 2026-10-10

### New

- **Print an exam**: print it or save it as a PDF to practice away from the screen, with the "Print" button on the exam page. Add the answer key, with answers and explanations, if you want it; it starts on a new page.
- **Previous question**: go back to the question before while taking an exam. "Next question" is now always there too, so you can skip a question and come back to it, and both buttons stay in the same place. At the end, Recall lists the questions you haven't answered yet.

### Improved

- **Explain it back** asks about one concept at a time, with a line saying what to explain about it. Concepts are grouped by section, and "Surprise me" picks a single one. For material you add from now on, Recall lists the concepts itself; older material can be analyzed again from the Explain it back page to get the same.

### Fixed

- Minor fixes and updates.

## 1.10.0 - 2026-10-01

### New

- **I don't know**: instead of guessing, say you don't know. It counts as incorrect, so the question comes back when you practice your weak questions, and the results show which ones you didn't know.
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

- Each topic page has a new **Go deeper** section with four study tools side by side: practice your weak questions, Explain it back, the concept map and Find gaps.
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
- Concept map: each topic's material, section by section, colored by how well you do on its questions. Pick a section to practice it.
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
- When a topic has more than one material, you can choose which material an exam uses. The AI writes questions only from the selected material, and only its questions are reused from the question bank.

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
