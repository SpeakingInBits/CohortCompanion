# CohortCompanion – Planning Document

_Status: draft, last updated 2026-11-02_

CohortCompanion is an ASP.NET Core Blazor app for collaborative classroom activities, similar to Mentimeter.
Instructors build **presentations** (slideshows students follow on their own devices) and **quizzes**
(set up ahead of time and run live in class). Students join anonymously with a nickname using a QR code
or a short join link.

---

## 1. Decisions so far

| Topic | Decision |
|---|---|
| Audience | A single higher-ed school, many instructors |
| Hosting | Demo: SpeakingInBits's Azure, for testers. Long term: the school hosts it on its own servers or in its own Azure subscription |
| Instructor sign-in | ASP.NET Core Identity. Email/password for the demo; Microsoft work accounts added once the school registers the app for production |
| Instructor access | Automatic for a verified email at the school's domain, or by invite code |
| Student identity | Anonymous, nickname only. No student accounts for now |
| Presentations vs quizzes | Separate features for now |
| Quiz pacing | Instructor-paced, or timed with a time limit per question and automatic advance |
| Scoring | Optional per quiz |
| Slide authoring | Visual editor aimed at non-technical instructors |
| Code support | Syntax highlighting in slides and quiz questions |
| Local database | SQL Server LocalDB |
| Production database | Azure SQL |
| LMS | Canvas (LTI 1.3) is a future option; design now so it can be added |
| Student data retention | Not needed. Participant-level data is short-lived (see FERPA section) |

---

## 2. Tech stack

- **.NET 10 (LTS)** Blazor Web App
  - Instructor pages (authoring, presenter controls) use **Interactive Server** rendering
  - Student pages use a lightweight **Interactive WebAssembly** client that talks to a **SignalR hub**.
    A hub client with automatic reconnect copes better with phones that sleep and with flaky classroom
    Wi-Fi than one Blazor Server circuit per student
- **EF Core 10** with SQL Server
  - Dev: `Server=(localdb)\MSSQLLocalDB;Database=CohortCompanion;Trusted_Connection=True;MultipleActiveResultSets=true`
  - Prod: Azure SQL using a managed identity (`Authentication=Active Directory Default`), so the
    connection string holds no password
- **Hosting must stay portable.** The demo runs on Azure, but the school will host the app on its own
  servers or in its own Azure subscription. Nothing may require an Azure-only service:
  - Database: any SQL Server (LocalDB, on-premises SQL Server, or Azure SQL)
  - File storage: an `IFileStore` interface with a local-disk implementation and an Azure Blob Storage
    implementation, picked in configuration
  - Email: an `IEmailSender` interface with SMTP (the school's mail server) and Azure Communication
    Services implementations, plus a dev implementation that writes emails to the log
  - SignalR: built in, single instance. Azure SignalR Service only if scale ever demands it
  - Secrets: standard ASP.NET Core configuration (environment variables, user secrets in dev). Key Vault
    is optional, wired in only when its URI is configured
- **Demo hosting:** Azure App Service (Linux) with WebSockets turned on and ARR affinity, plus Azure SQL
- **School hosting:** IIS on Windows Server, or Azure App Service in the school's subscription. A
  self-hosting guide ships with the repo
- **UI components:** Radzen.Blazor (MIT). It provides an HTML editor, drag and drop, charts, and dialogs
  in one library. The rich-text editor sits behind our own component so we can swap it (for example
  for TipTap via JS interop) if it proves too limited
- **QR codes:** QRCoder (MIT), rendered as SVG on the server
- **Syntax highlighting:** highlight.js, bundled locally rather than loaded from a CDN (see FERPA section)
- **HTML sanitization:** HtmlSanitizer (Ganss.Xss) on every piece of rich text before it is saved
  or shown to students

### Solution layout (proposed)

```
CohortCompanion.sln
src/
  CohortCompanion.Web/          Server: Identity, EF Core, SignalR hubs, instructor UI, background jobs
  CohortCompanion.Web.Client/   WASM: student join and participate UI, projector view
  CohortCompanion.Shared/       DTOs, hub contracts, enums shared by server and client
tests/
  CohortCompanion.Tests/        Unit tests (scoring, answer matching, session state machine)
docs/
  PLAN.md
```

---

## 3. Authentication and authorization

### Instructors
- **Demo:** ASP.NET Core Identity with email/password accounts and email confirmation.
- **Production:** the school registers a single-tenant app in its Entra tenant and Microsoft sign-in is
  added as an external login. Identity keeps a local `ApplicationUser` linked to it, so roles and
  profile data stay ours. Email/password accounts remain for invited guests. Turning Microsoft sign-in
  on is configuration only (tenant ID, client ID, secret); no code changes.

### Becoming an instructor
A new account gets the `Instructor` role in one of two ways:
1. **School email domain.** `Auth:InstructorEmailDomains` (for example `["school.edu"]`) lists the
   allowed domains. An account whose email is at one of them becomes an instructor automatically, but
   only once the email is **verified**: confirmed through the emailed link, or vouched for by Microsoft
   sign-in. Without verification, anyone could register a made-up `@school.edu` address.
2. **Invite code.** For testers and guests outside the domain.
   - An admin creates an `InviteCode`: a short code (for example `K7M-4QX`) and a link
     (`/register?invite=K7M-4QX`).
   - Each code has an optional note ("Spring testers"), an optional email it is locked to, a maximum
     number of uses (default 1), and an expiry date (default 14 days).
   - Registering with a valid code grants `Instructor` and records which code was used. Admins can
     revoke a code and see who redeemed it.

An account that matches neither (for example, a student who registers with their school email while
the domain rule is off) sees a "no instructor access yet" page and can enter an invite code there.

### Roles
- `Admin`: manages instructors and invite codes, sees school-wide usage, can delete any content.
  The first admin is created at startup from configuration (`Auth:BootstrapAdminEmail`).
- `Instructor`: creates and runs their own presentations and quizzes.
- Instructors can only see their own content and sessions. Sharing or co-teaching is a later feature.

### Students
- No account. On join, the server issues a random **participant token**, stored in `localStorage` and
  sent on reconnect, so a student who refreshes or whose phone sleeps rejoins as the same participant.
- The token only works for that session and expires when the session ends.

---

## 4. Domain model

### Accounts

| Entity | Key fields |
|---|---|
| `ApplicationUser` | Identity user, plus DisplayName, CreatedAt, InviteCodeId (if one was used) |
| `InviteCode` | Id, Code, Note, LockedToEmail, MaxUses, UseCount, ExpiresAt, CreatedById, RevokedAt |

### Authoring

| Entity | Key fields |
|---|---|
| `Presentation` | Id, OwnerId, Title, ThemeId, CreatedAt, UpdatedAt |
| `Slide` | Id, PresentationId, Order, Layout, Content (JSON of blocks), SpeakerNotes |
| `Quiz` | Id, OwnerId, Title, Mode (`InstructorPaced`, `Timed`), ScoringEnabled, SpeedBonusEnabled, DefaultTimeLimitSeconds, ShowLeaderboard |
| `Question` | Id, QuizId, Order, Type (`MultipleChoice`, `WordCloud`, `FillInBlank`), Prompt (sanitized HTML), CodeSnippet, CodeLanguage, ImageUrl, TimeLimitSeconds, Points |
| `ChoiceOption` | Id, QuestionId, Order, Text, IsCorrect, CodeSnippet |
| `BlankAnswer` | Id, QuestionId, BlankIndex, AcceptedText, MatchMode (`Exact`, `CaseInsensitive`, `Fuzzy`) |

### Running

| Entity | Key fields |
|---|---|
| `LiveSession` | Id, Kind (`Presentation`, `Quiz`), PresentationId or QuizId, HostId, JoinCode, Status (`Lobby`, `Running`, `Ended`), CurrentIndex, Phase, PhaseDeadlineUtc, StartedAt, EndedAt |
| `Participant` | Id, LiveSessionId, Nickname, TokenHash, JoinedAt, IsRemoved |
| `Response` | Id, ParticipantId, QuestionId, Payload (JSON), SubmittedAt, IsCorrect, Score |
| `SessionSummary` | Id, LiveSessionId, QuestionId, aggregate JSON (option counts, top words, answer distribution, average score). **No participant data.** Kept after participant data is purged |

Notes:
- Starting a session snapshots the quiz or presentation content as JSON. Edits made while a session is
  running then cannot change what students see.
- Live state (current slide, phase, response counts) lives in memory in a `LiveSessionManager`
  singleton and is written to the database at each phase change, so a server restart can recover it.

---

## 5. Presentations

- A slideshow made of slides built from **layout templates**: Title, Title + Content, Two Columns,
  Image, Image + Caption, Code, Section Header, Blank.
- **Visual editor**
  - Thumbnail strip on the left with drag-and-drop reordering, duplicate, and delete
  - WYSIWYG canvas in the middle. Click any text to edit it in place. Toolbar for bold, italic, lists,
    links, and text color from the theme palette
  - Properties panel on the right: layout, background, speaker notes
  - Image upload by drag-and-drop or paste. Images are stored in Azure Blob Storage, or on local disk
    in dev
  - Code block: paste code, pick a language, and get a live highlighted preview
  - A handful of built-in themes so instructors never fiddle with fonts or colors
  - Autosave with an "All changes saved" indicator. Undo and redo
- **Presenting**
  - The instructor laptop shows the control view: current slide, next slide, speaker notes, and the
    join code
  - A projector view opens in a second window
  - Student devices show the current slide and follow the instructor automatically.
    _Open question: may students scroll back to earlier slides?_

---

## 6. Quizzes

### Question types
- **Multiple choice**
  - Single or multiple select, 2–6 options, each option can contain code
  - Results: bar chart; the reveal highlights the correct option(s)
  - Scoring: full points for correct. With multiple select, optional partial credit
- **Word cloud**
  - Each student submits 1–N short entries (N set per question)
  - Entries are normalized (trimmed, lowercased, punctuation removed) and run through a profanity filter
  - The instructor can hide a word live
  - Never scored. Used as a poll inside a quiz
- **Fill in the blank**
  - The prompt contains one or more blanks, for example `The keyword {{1}} declares a constant in C#.`
  - Each blank has a list of accepted answers and a match mode. Fuzzy mode uses Levenshtein distance ≤ 1
    for answers of 5 or more characters
  - Results: the most common answers per blank
  - The instructor can mark a common answer as accepted live, which rescores everyone
  - Scoring: points split evenly across blanks

### Modes
- **Instructor-paced:** the instructor opens each question, closes it, shows results, and reveals the
  answer. A per-question timer is optional and only closes the question; the instructor still advances.
- **Timed:** every question has its own time limit (the quiz default, overridable per question).
  When the timer runs out the question closes, results and the answer show for a few seconds
  (configurable, default 5), and the quiz moves to the next question automatically. The instructor
  can pause, extend the timer, or skip.

### Question phases
`Waiting → Open → Closed → ShowingResults → AnswerRevealed → (next question)`

- The server enforces timers using `PhaseDeadlineUtc`, with a 500 ms grace period for network latency.
- Results stay hidden until the instructor shows them, so early answers don't sway everyone else.

### Scoring
- Points per question (default 1000).
- Optional speed bonus: `score = points × (1 − 0.5 × elapsed / timeLimit)` for correct answers.
- Optional leaderboard: top 5 between questions, plus a final podium (top 3). Each student sees
  their own rank and score on their device only.

---

## 7. Joining and live sessions

- **Join code:** 6 digits, unique among sessions that are currently live, released when a session ends.
  The input uses `inputmode="numeric"` so phones show a number pad.
- **Join link:** `https://<host>/j/123456`. The QR code encodes this link and appears on the lobby
  screen and in a corner of the projector view.
- **Lobby:** students enter a nickname and see "Waiting for the instructor to start". The projector
  shows the nicknames as they join. The instructor can remove or rename a participant.
- **Nicknames:** 2–20 characters, profanity filtered, unique within the session.

### SignalR hub contract (sketch)

Student to server:
- `Join(joinCode, nickname) → { participantToken, sessionState }`
- `Rejoin(participantToken) → sessionState`
- `SubmitAnswer(questionId, payload) → { accepted, reason }`

Instructor to server (requires the `Instructor` role and session ownership):
- `StartSession`, `GoTo(index)`, `Next`, `Previous`
- `OpenQuestion`, `CloseQuestion`, `ShowResults`, `RevealAnswer`, `PauseTimer`, `ResumeTimer`
- `RemoveParticipant(id)`, `RenameParticipant(id, name)`, `HideWord(questionId, word)`,
  `AcceptAnswer(questionId, blankIndex, text)`
- `EndSession`

Server to clients:
- `SessionState(snapshot)`: full state, sent on join, on rejoin, and after any reconnect
- `SlideChanged`, `PhaseChanged`, `ResponseCountChanged`, `ResultsUpdated`, `LeaderboardUpdated`,
  `ParticipantJoined`, `ParticipantRemoved`, `SessionEnded`

Groups: `session:{id}:students`, `session:{id}:host`, `session:{id}:projector`.

---

## 8. FERPA and student privacy

> This is engineering guidance, not legal advice. Confirm the approach with the school's FERPA
> officer or registrar before launch.

### Why the anonymous model keeps the FERPA burden small
FERPA protects **education records**: records that are directly related to an *identifiable* student
and are maintained by the school or by a party acting for it. Anonymous nickname responses that are
never linked to a student's identity, and that are deleted soon after class, mostly fall outside
that definition. The real risk is that **students type their real names as nicknames**, which makes
responses and scores identifiable. The design therefore assumes some nicknames will be real names
and limits what is kept and shown.

### Effects on the website

**Join page and student UI**
- Short notice above the nickname box: "Use a nickname. Don't enter your full name or student ID."
- A link to a plain-language privacy notice: what is collected (nickname, answers), how long it is
  kept, and that nothing is used for grading or advertising.
- No analytics, advertising, or third-party scripts on student pages. Fonts, highlight.js, and all
  other assets are served from our own domain, so third parties never see student traffic.
- Only one strictly necessary item is stored on the device: the participant token. No cookie banner
  is needed for students.

**Projector and leaderboard**
- Showing scores next to identifiable students in front of the class is the main exposure.
  Mitigations:
  - The leaderboard is optional per quiz and shows only the top 5. Nobody's low score is shown publicly.
  - Each student sees their own rank only on their own device.
  - The instructor can remove or rename a participant before the leaderboard appears.
  - Word cloud and fill-in-the-blank results are shown as aggregates, without nicknames.

**Data retention**
- `Participant` and `Response` rows are deleted by a background job a short time after the session
  ends (proposed default: 24 hours, so the instructor can review results the same day).
- `SessionSummary` aggregates are kept with no participant data, so instructors can see how a class
  did on each question over time.
- Instructors can delete a session's data immediately.

**Exports**
- Default export is aggregate only (per-question results).
- If a per-nickname export is added, the UI warns that nicknames may identify students and that the
  exported file becomes the instructor's responsibility under school policy.
- Scores from anonymous quizzes should not be used for grades. Graded use waits for Canvas
  integration, where identity is handled properly.

**Logging and telemetry**
- Never log answer content or nicknames.
- Application Insights stays on its default IP masking. Do not log IP addresses alongside participant
  IDs.
- Join and answer endpoints are rate-limited per IP in memory only, never persisted.

**Infrastructure**
- Azure region in the US. Azure SQL Transparent Data Encryption (on by default). TLS everywhere with HSTS.
- Managed identity for database access. Secrets in Key Vault.
- Instructors can only reach their own sessions. Admin actions are audit-logged.
- A written incident response contact and process, in case of a breach.

**Who operates the app**
- **Demo (now):** SpeakingInBits hosts it on Azure for testers. No agreement with the school for now,
  since the data is anonymous and short-lived. The demo should not be used to run real graded classes.
- **Long term:** the school hosts it on its own servers or in its own Azure subscription. The data
  never leaves the school, so no vendor agreement is needed.
- If SpeakingInBits ever hosts it for real classes with identified students (for example after Canvas
  integration), SpeakingInBits becomes a vendor. The school would then need to designate it a "school
  official" under a written agreement covering permitted use, no redisclosure, security, and deletion.

**Age**
- The school is higher ed, so COPPA (children under 13) does not apply, and neither do K-12 state
  laws such as SOPIPA. Dual-enrollment high school students in college classes are still covered by
  the same anonymous-by-default design.

### What changes when Canvas (LTI 1.3) arrives
LTI launches pass real names, Canvas user IDs, and roster data, and grade passback writes scores into
the gradebook. Those are education records, so at that point the app needs:
- The vendor or school-official agreement above
- A documented retention schedule and deletion on request
- Access audit logging for every view of student data
- Participant identity in its own table (`ParticipantIdentity`), linked to `Participant`, so the
  anonymous flow stays unchanged and identified data can be secured and purged separately

The domain model keeps `Participant` free of identity fields now, so this can be added later without
reworking it.

---

## 9. Milestones

1. **Foundation:** solution scaffold, EF Core with LocalDB, Identity with email/password and email
   confirmation, roles, domain rule and invite codes, layout and theme, CI build
2. **Quiz authoring:** quiz CRUD, editors for all three question types, code snippets with highlighting
3. **Live quiz (instructor-paced):** join code, QR code, lobby, SignalR hub, phases, results charts, reveal
4. **Scoring and timed mode:** timers, speed bonus, leaderboard, auto-advance
5. **Presentation authoring:** visual slide editor, layouts, themes, image upload
6. **Live presentations:** presenter view, projector view, students following along
7. **Privacy hardening and demo deployment:** purge job, privacy notice, rate limiting, Azure
   infrastructure (Bicep), demo deployment for testers
8. **School handoff:** Microsoft sign-in (configuration only), self-hosting guide for IIS and for the
   school's own Azure subscription
9. **Later:** Canvas LTI 1.3, self-paced homework quizzes, Q&A with upvoting, the "I'm lost" signal,
   co-teaching and sharing, PowerPoint import

---

## 10. Open questions

1. **Retention window:** is 24 hours of per-nickname results right, or should they be deleted the
   moment the session ends?
2. **Presentations:** may students navigate back to earlier slides on their device, or stay locked to
   the instructor's current slide?
3. **Demo email:** which service sends confirmation and password-reset emails from the demo? Azure
   Communication Services is the default assumption.
