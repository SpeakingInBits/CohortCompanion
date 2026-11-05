# CohortCompanion – Planning Document

_Status: milestones 1 to 7 built, demo not yet deployed. Last updated 2026-11-05_

CohortCompanion is an ASP.NET Core Blazor app for collaborative classroom activities, similar to Mentimeter.
Instructors build **presentations** (slideshows students follow on their own devices) and **quizzes**
(set up ahead of time and run live in class). Students join anonymously with a nickname using a QR code
or a short join link.

---

## 1. Decisions so far

| Topic | Decision |
|---|---|
| Audience | A single higher-ed school, many instructors |
| Hosting | Azure. Demo on SpeakingInBits's subscription for testers. If the school later needs its own infrastructure, we refactor then |
| Instructor sign-in | ASP.NET Core Identity with email/password. No email confirmation for now. Microsoft work accounts added once the school registers the app for production |
| Instructor access | Invite code only, for everyone |
| Student identity | Anonymous, nickname only. No student accounts for now |
| Presentations vs quizzes | Separate features for now |
| Quiz pacing | Instructor-paced, or timed with a time limit per question and automatic advance |
| Scoring | Optional per quiz |
| Slide authoring | Visual editor aimed at non-technical instructors |
| Code support | Syntax highlighting in slides and quiz questions |
| Local database | SQL Server LocalDB |
| Production database | Azure SQL |
| LMS | Canvas (LTI 1.3) is a future option; design now so it can be added |
| Student data retention | None. Per-nickname data is deleted when the session ends; only per-question totals are kept |

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
- **Hosting: Azure.** If the school later needs to run it on its own infrastructure, we refactor then.
  - Azure App Service (Linux) with WebSockets turned on and ARR affinity
  - Azure SQL
  - Azure Blob Storage for uploaded images (Azurite emulator in dev)
  - SignalR built in, single instance. Azure SignalR Service only if scale ever demands it
  - Secrets in Azure Key Vault, referenced from App Service settings. User secrets in dev
  - Infrastructure defined in Bicep
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
- **Now:** ASP.NET Core Identity with email/password accounts. Emails are **not** confirmed
  (`RequireConfirmedAccount = false`), so no email service is needed yet.
  - Password reset works without email: an admin generates a one-time reset link from the instructor
    list and passes it on.
- **Production:** the school registers a single-tenant app in its Entra tenant and Microsoft sign-in is
  added as an external login. Identity keeps a local `ApplicationUser` linked to it, so roles and
  profile data stay ours. Turning Microsoft sign-in on is configuration only (tenant ID, client ID,
  secret); no code changes.
  - The template's external-login page creates accounts without asking for an invite code. Those
    accounts get no role, so they can't reach instructor pages. Before turning Microsoft sign-in on,
    add the invite code (or the domain rule) to that page.

### Becoming an instructor: invite codes
**Registration requires an invite code.** There is no open sign-up page.
- An admin creates an `InviteCode`: a short code (for example `K7M-4QX`) and a link
  (`/register?invite=K7M-4QX`).
- Each code has an optional note ("Spring testers"), an optional email it is locked to, a maximum
  number of uses (default 1), and an expiry date (default 14 days).
- Registering with a valid code creates the account with the `Instructor` role and records which code
  was used. Admins can revoke a code and see who redeemed it.
- Because emails aren't confirmed, the invite code is the only gate. Locking a code to an email or
  keeping uses at 1 stops a shared link from letting strangers in.
- _Later:_ automatic instructor access for the school's email domain, once Microsoft sign-in or email
  confirmation makes email addresses trustworthy.

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
  - Students can swipe or tap back to any slide the instructor has already shown, but never ahead of
    the instructor. While a student is browsing an earlier slide, their device stops following, and a
    **"Jump to live"** button (with a badge saying how many slides behind they are) takes them back to
    the instructor's current slide and resumes following.

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

Projector to server (requires the `Instructor` role and session ownership):
- `WatchProjector(sessionId) → projectorState`

Instructor controls don't go through the hub. The host page runs on the server (Interactive Server), so it
calls `LiveSessionManager` directly, which checks that the caller owns the session: start, go to a slide, open,
close, show results, reveal, next, pause, resume, extend, skip, remove, rename, hide a word, accept an answer, end.

Who receives what, so that a whole class answering at once stays cheap:

| Event | Goes to | When |
|---|---|---|
| Join and rejoin results | The one student | Full state, on join, on rejoin, and after any reconnect |
| `PhaseChanged`, `SlideChanged` | Students | Once per change |
| `ResultsUpdated` | Students | Only once the instructor has shown results |
| `LeaderboardUpdated` | Each student, separately | That student's own score and rank, from the reveal onward |
| `ProjectorUpdated` | Projector | Throttled to twice a second, with counts, nicknames, and the top five |
| `NicknameChanged`, `ParticipantRemoved` | The one student | When the instructor renames or removes them |
| `SessionEnded` | Students and projector | Once |

An answer never causes a broadcast to students. Answers are buffered in memory and saved in batches, at each phase
change and every few seconds. The host page sees live totals, also throttled.

Groups: `session:{id}:students`, `session:{id}:projector`.

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
- **When a session ends, its `Participant` and `Response` rows are deleted** in the same operation
  that writes the `SessionSummary` aggregates and marks the session `Ended`. Per-nickname results
  exist only while the session is live.
- The final podium is shown before the session ends, so nothing is lost by deleting right after.
- A background job sweeps sessions abandoned without being ended (no activity for 12 hours), so
  per-nickname data can't linger when an instructor just closes the laptop.
- `SessionSummary` aggregates are kept with no participant data, so instructors can see how a class
  did on each question over time.

**Exports**
- Exports are aggregate only (per-question results). There is no per-nickname export.
- Scores from anonymous quizzes are not meant for grades. Graded use waits for Canvas integration,
  where identity is handled properly.

**Logging and telemetry**
- Never log answer content or nicknames.
- Application Insights stays on its default IP masking. Do not log IP addresses alongside participant
  IDs.
- Join and answer calls are rate-limited in memory only, never persisted. Limits are tight per connection and
  loose per IP, because a whole class usually shares one address behind campus Wi-Fi.

**Infrastructure**
- Azure region in the US. Azure SQL Transparent Data Encryption (on by default). TLS everywhere with HSTS.
- Managed identity for database access. Secrets in Key Vault.
- Instructors can only reach their own sessions. Admin actions are audit-logged to the application log, by id only.
- A written incident response contact and process, in case of a breach. **Still to be decided:** see
  [DEPLOYMENT.md](DEPLOYMENT.md).

**Who operates the app**
- **Demo (now):** SpeakingInBits hosts it on Azure for testers. No agreement with the school for now,
  since the data is anonymous and short-lived. The demo should not be used to run real graded classes.
- **Long term:** the school hosts it, most likely in its own Azure subscription. The data never
  leaves the school, so no vendor agreement is needed.
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

Milestones 1 to 6 are built. Milestone 7 is built except the demo deployment itself, which needs an Azure sign-in:
see [DEPLOYMENT.md](DEPLOYMENT.md).

1. **Foundation:** solution scaffold, EF Core with LocalDB, Identity with email/password, roles,
   bootstrap admin, invite codes, admin password-reset links, layout and theme, CI build
2. **Quiz authoring:** quiz CRUD, editors for all three question types, code snippets with highlighting
3. **Live quiz (instructor-paced):** join code, QR code, lobby, SignalR hub, phases, results charts, reveal
4. **Scoring and timed mode:** timers, speed bonus, leaderboard, auto-advance
5. **Presentation authoring:** visual slide editor, layouts, themes, image upload
6. **Live presentations:** presenter view, projector view, students following along
7. **Privacy hardening and demo deployment:** delete-on-end and abandoned-session sweep, privacy
   notice, rate limiting, Azure infrastructure (Bicep), demo deployment for testers
8. **School handoff:** Microsoft sign-in (configuration only), deployment guide for the school's own
   Azure subscription
9. **Later:** Canvas LTI 1.3, self-paced homework quizzes, Q&A with upvoting, the "I'm lost" signal,
   co-teaching and sharing, PowerPoint import

---

## 10. Open questions

- **Incident contact:** who is the named contact for a breach? See section 8.
- **Load test:** capacity figures are estimates until the app has been tested with a few hundred simulated clients
  on the real App Service plan.
