using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Live;
using CohortCompanion.Web.Quizzes;

namespace CohortCompanion.Web.Onboarding;

/// <summary>The control a tour step points at, which the page rings so it's easy to find.</summary>
public enum TourTarget
{
    None,
    NewQuizTitle,
    EditQuiz,
    AddQuestion,
    RunLive,
    JoinInfo,
    Start,

    /// <summary>The blue button for the quiz's current phase on the host page.</summary>
    NextStep,
    EndSession,
}

/// <summary>One piece of guidance: what this part of the site is for, and the one thing to do next.</summary>
public sealed record TourStep(int Number, string Title, string Body, string? Action = null, TourTarget Target = TourTarget.None)
{
    /// <summary>The quiz whose row holds the target, on the quiz list.</summary>
    public int? TargetQuizId { get; init; }

    /// <summary>What's still missing before the next step, shown as a short list.</summary>
    public IReadOnlyList<string> Todo { get; init; } = [];

    /// <summary>A caveat about this step, such as why a phone can't join here.</summary>
    public string? Note { get; init; }

    public string? LinkText { get; init; }

    public string? LinkHref { get; init; }

    /// <summary>The closing message, shown once the tour is over.</summary>
    public bool IsFarewell { get; init; }
}

/// <summary>
/// Works out the tour's guidance from what's on the page, not from a remembered step number. An instructor who
/// wanders off, reloads, or does things in their own order always gets the advice that fits where they are.
/// </summary>
public static class TourGuide
{
    public const int StepCount = 6;

    public const string OfferTitle = "Take the 3-minute tour";

    public const string OfferBody =
        "Make a practice quiz, run it live, and join it from your phone the way your students will.";

    /// <summary>How many todo items to list at once, so the panel stays small.</summary>
    private const int MaxTodo = 3;

    public static TourStep Farewell { get; } = new(
        StepCount,
        "That's the tour",
        "You made a quiz, ran it live, and joined it as a student. Your practice quiz is still under Quizzes, to run again "
        + "or delete, and the totals from this session are under Sessions. Presentations work the same way, with Present.")
    {
        IsFarewell = true,
        LinkText = "Back to your quizzes",
        LinkHref = "quizzes",
    };

    /// <summary>The CSS class that rings a control, when the step points at it.</summary>
    public static string? Highlight(this TourStep? step, TourTarget target, int? quizId = null) =>
        step is not null && step.Target == target && (quizId is null || step.TargetQuizId == quizId) ? "tour-target" : null;

    /// <summary>The instructor's own quiz list, newest first.</summary>
    public static TourStep ForQuizList(IReadOnlyList<QuizListItem> quizzes)
    {
        if (quizzes.Count == 0)
        {
            return new TourStep(
                1,
                "Create a quiz",
                "Everything you run in class starts here as a quiz. Any title will do, such as “Practice quiz”.",
                "Type a title, then press Create quiz.",
                TourTarget.NewQuizTitle);
        }

        if (quizzes.FirstOrDefault(q => q.IsReady == true) is { } ready)
        {
            return new TourStep(
                3,
                "Run it live",
                $"“{ready.Title}” is ready. Running it live opens a session that students join from their own devices.",
                "Press Run live next to it.",
                TourTarget.RunLive)
            {
                TargetQuizId = ready.Id,
            };
        }

        var draft = quizzes[0];
        return new TourStep(
            2,
            "Add a question",
            $"“{draft.Title}” can't run yet, because it still needs a complete question.",
            "Press Edit next to it.",
            TourTarget.EditQuiz)
        {
            TargetQuizId = draft.Id,
        };
    }

    /// <summary>The quiz editor. <paramref name="problems"/> is what <see cref="QuizRules.Validate"/> says about the quiz as it stands.</summary>
    public static TourStep ForQuizEdit(Quiz quiz, IReadOnlyList<string> problems)
    {
        if (quiz.Questions.Count == 0)
        {
            return new TourStep(
                2,
                "Add a question",
                "A quiz is a list of questions, and there are three kinds. Multiple choice is the quickest to try.",
                "Press Add multiple choice.",
                TourTarget.AddQuestion);
        }

        if (problems.Count > 0)
        {
            var body = quiz.Questions.Any(q => q.Type == QuestionType.MultipleChoice)
                ? "Type a question under Prompt, such as “What is 2 + 2?”. Type an answer into each option, "
                    + "then tick the box beside the correct one."
                : "Fill in the question until nothing is left on this list.";
            return new TourStep(2, "Fill in the question", body)
            {
                Todo = problems.Take(MaxTodo).ToList(),
            };
        }

        return new TourStep(
            3,
            "Run it live",
            "That's a complete quiz, and you can add more questions any time. Run live saves it and opens a session "
            + "for students to join.",
            "Press Run live.",
            TourTarget.RunLive);
    }

    /// <summary>
    /// The host page of a live session. <paramref name="joinPage"/> is the address students type, which says
    /// whether a phone could reach this copy of the site at all.
    /// </summary>
    public static TourStep? ForHost(HostView view, Uri joinPage)
    {
        if (view.Kind != SessionKind.Quiz)
        {
            return null;
        }

        if (view.Status == SessionStatus.Lobby)
        {
            return view.Participants.Count == 0 ? Join(view, joinPage) : Start(view);
        }

        if (view.Phase == QuestionPhase.Finished)
        {
            return new TourStep(
                6,
                "End the session",
                "Ending deletes every nickname and answer. Only the totals for each question are kept, for you to look "
                + "back on under Sessions.",
                "Press End session and keep the totals.",
                TourTarget.EndSession);
        }

        if (view.IsTimed)
        {
            return new TourStep(
                5,
                "Watch it run",
                "A timed quiz runs itself: each question opens, closes when its time is up, shows the results, and "
                + "moves on. Answer on your phone as the questions come.");
        }

        var nextLabel = view.QuestionIndex + 1 >= view.QuestionCount ? "Finish quiz" : "Next question";
        var isPoll = view.Question?.Type == QuestionType.WordCloud;
        return view.Phase switch
        {
            QuestionPhase.Waiting => new TourStep(
                5,
                "Open the question",
                "You set the pace. Your phone says “Get ready” until you open the question, so you can talk first.",
                "Press Open question.",
                TourTarget.NextStep),

            QuestionPhase.Open when view.Participants.Count == 0 => new TourStep(
                5,
                "Show the results",
                "Nobody has joined, so no answers will arrive. You can still step through the rest.",
                "Press Close and show results.",
                TourTarget.NextStep),

            QuestionPhase.Open when view.ResponseCount == 0 => new TourStep(
                5,
                "Answer on your phone",
                "The question is on your phone now. Answers are counted here as they arrive, and the class can't see "
                + "them yet.",
                "Answer the question on your phone."),

            QuestionPhase.Open => new TourStep(
                5,
                "Show the results",
                $"{view.ResponseCount} of {view.Participants.Count} answered. So far only you can see how they answered.",
                "Press Close and show results.",
                TourTarget.NextStep),

            QuestionPhase.Closed => new TourStep(
                5,
                "Show the results",
                "The question is closed, so no more answers are taken.",
                "Press Show results.",
                TourTarget.NextStep),

            QuestionPhase.ShowingResults when !isPoll => new TourStep(
                5,
                "Reveal the answer",
                "Your phone and the projector now show how the class answered, without names.",
                "Press Reveal answer.",
                TourTarget.NextStep),

            _ => new TourStep(
                5,
                "Move on",
                isPoll
                    ? "Your phone and the projector now show what the class wrote, without names."
                    : "Your phone now shows whether you got it right.",
                $"Press {nextLabel}.",
                TourTarget.NextStep),
        };
    }

    private static TourStep Join(HostView view, Uri joinPage)
    {
        const string body =
            "This is the lobby. Students scan the QR code or type the join code, and pick a nickname. They never "
            + "need an account.";
        var studentPage = $"j/{view.JoinCode}";

        // A phone can't open an address that only means "this computer", so a second window stands in for it.
        if (joinPage.IsLoopback)
        {
            return new TourStep(4, "Join as a student", body, "Open the student page and pick a nickname.", TourTarget.JoinInfo)
            {
                Note = "This copy of the site runs on your own computer, where a phone can't reach it, so a second "
                    + "window plays the student.",
                LinkText = "Open the student page in a new window",
                LinkHref = studentPage,
            };
        }

        return new TourStep(
            4,
            "Join as a student",
            body,
            "Scan the QR code with your phone's camera, then pick a nickname.",
            TourTarget.JoinInfo)
        {
            LinkText = "No phone handy? Open the student page in a new window",
            LinkHref = studentPage,
        };
    }

    private static TourStep Start(HostView view) => new(
        4,
        "Start the quiz",
        $"“{view.Participants[0].Nickname}” has joined and is listed under Participants, where you can rename or remove "
        + "anyone. In class, Open projector view puts the join code on the big screen.",
        "Press Start.",
        TourTarget.Start);
}
