# Release notes

## 1.2.0 (2026-09-24)

### New

- The version is shown next to the logo, and at the bottom of the sign-in page.
- These release notes, under your name in the top right, or from the version on the sign-in page.

### Fixed

- In the answer review, a correct answer you had picked was dark text on a dark background and hard to read.
- In the answer review, a wrong pick now shows its red edge again instead of looking like a normal selection.
- Browsers no longer fill your saved login password into the API key and SMTP password fields.

## 1.1.1 (2026-09-24)

### Fixed

- The container no longer crashes at startup when `/data` is a host folder or an existing volume. It now runs as root, so any `/data` mount works as-is.

## 1.1.0 (2026-09-24)

### Improved

- The exam page now warns when a topic's question bank is close to what its material can support. Filling missing questions, "New questions" and replacing a question would otherwise keep producing near-repeats.

## 1.0.0 (2026-09-24)

The first release.

- Topics with PDF, Word, PowerPoint, LaTeX, Markdown or pasted material, in any language.
- AI-written multiple-choice and written questions, with thorough explanations and AI grading of written answers.
- A reusable question bank (20% reuse by default) that avoids repeating itself.
- Statistics: score over time, an overview per topic, weak questions, and a breakdown by type and difficulty.
- Bring your own Claude or ChatGPT key, with cost estimates before anything expensive.
- Accounts created by the admin, with email invites and password reset.
