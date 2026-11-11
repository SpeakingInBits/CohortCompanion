using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Live;
using CohortCompanion.Web.Onboarding;
using CohortCompanion.Web.Quizzes;

namespace CohortCompanion.Tests;

public class TourGuideTests
{
    private static readonly Uri SchoolSite = new("https://cohort.example.edu/join");
    private static readonly Uri OwnComputer = new("http://localhost:5083/join");

    private static QuizListItem Listed(int id, string title, bool ready) =>
        new(id, title, QuizMode.InstructorPaced, ready ? 1 : 0, DateTime.UnixEpoch, "A", ready);

    private static Question MultipleChoice(bool complete) => new()
    {
        Type = QuestionType.MultipleChoice,
        Prompt = complete ? "<p>What is 2 + 2?</p>" : "",
        Options = [new ChoiceOption { Order = 0, Text = "4", IsCorrect = complete }, new ChoiceOption { Order = 1, Text = "5" }],
    };

    private static TourStep ForEditor(params Question[] questions)
    {
        var quiz = new Quiz { Title = "Practice quiz", Questions = [.. questions] };
        return TourGuide.ForQuizEdit(quiz, QuizRules.Validate(quiz));
    }

    private static HostView Hosting(
        SessionStatus status = SessionStatus.Running,
        QuestionPhase phase = QuestionPhase.Waiting,
        int participants = 1,
        int responses = 0,
        bool timed = false,
        QuestionType type = QuestionType.MultipleChoice,
        int questionCount = 1,
        SessionKind kind = SessionKind.Quiz) => new(
            Guid.NewGuid(),
            "Practice quiz",
            kind,
            "123456",
            status,
            phase,
            QuestionIndex: 0,
            questionCount,
            new QuestionSnapshot(1, type, "<p>What is 2 + 2?</p>", null, null, 1000, false, false, 1, 0, [], []),
            RemainingMs: null,
            IsPaused: false,
            responses,
            Enumerable.Range(1, participants).Select(i => new ParticipantView(Guid.NewGuid(), $"Phone{i}")).ToList(),
            LiveResults: null,
            timed,
            Leaderboard: null,
            Deck: null,
            ProjectorKey: "key",
            EndsByItselfUtc: null);

    [Fact]
    public void QuizList_WithNoQuizzes_AsksForATitle()
    {
        var step = TourGuide.ForQuizList([]);

        Assert.Equal(1, step.Number);
        Assert.Equal(TourTarget.NewQuizTitle, step.Target);
    }

    [Fact]
    public void QuizList_WithOnlyADraft_PointsAtItsEditButton()
    {
        var step = TourGuide.ForQuizList([Listed(7, "Draft", ready: false)]);

        Assert.Equal(2, step.Number);
        Assert.Equal(TourTarget.EditQuiz, step.Target);
        Assert.Equal(7, step.TargetQuizId);
    }

    [Fact]
    public void QuizList_WithAReadyQuiz_PointsAtItsRunLiveButton()
    {
        var step = TourGuide.ForQuizList([Listed(7, "Draft", ready: false), Listed(8, "Ready", ready: true)]);

        Assert.Equal(3, step.Number);
        Assert.Equal(TourTarget.RunLive, step.Target);
        Assert.Equal(8, step.TargetQuizId);
        Assert.Contains("Ready", step.Body);
    }

    [Fact]
    public void Highlight_RingsOnlyTheTargetOnTheTargetQuizsRow()
    {
        var step = TourGuide.ForQuizList([Listed(7, "Draft", ready: false), Listed(8, "Ready", ready: true)]);

        Assert.Equal("tour-target", step.Highlight(TourTarget.RunLive, 8));
        Assert.Null(step.Highlight(TourTarget.RunLive, 7));
        Assert.Null(step.Highlight(TourTarget.EditQuiz, 8));
        Assert.Null(((TourStep?)null).Highlight(TourTarget.RunLive, 8));
    }

    [Fact]
    public void Editor_WithNoQuestions_PointsAtAddMultipleChoice()
    {
        var step = ForEditor();

        Assert.Equal(2, step.Number);
        Assert.Equal(TourTarget.AddQuestion, step.Target);
    }

    [Fact]
    public void Editor_WithAnUnfinishedQuestion_ListsWhatIsMissing()
    {
        var step = ForEditor(MultipleChoice(complete: false));

        Assert.Equal(2, step.Number);
        Assert.Equal(TourTarget.None, step.Target);
        Assert.Contains("Question 1 needs a prompt.", step.Todo);
        Assert.Contains("Question 1 needs a correct option.", step.Todo);
    }

    [Fact]
    public void Editor_KeepsTheTodoListShort()
    {
        var step = ForEditor(MultipleChoice(complete: false), MultipleChoice(complete: false), MultipleChoice(complete: false));

        Assert.Equal(3, step.Todo.Count);
    }

    [Fact]
    public void Editor_WithACompleteQuiz_PointsAtRunLive()
    {
        var step = ForEditor(MultipleChoice(complete: true));

        Assert.Equal(3, step.Number);
        Assert.Equal(TourTarget.RunLive, step.Target);
        Assert.Empty(step.Todo);
    }

    [Fact]
    public void EmptyLobby_AsksForAPhoneToScanTheCode()
    {
        var step = TourGuide.ForHost(Hosting(SessionStatus.Lobby, participants: 0), SchoolSite)!;

        Assert.Equal(4, step.Number);
        Assert.Equal(TourTarget.JoinInfo, step.Target);
        Assert.Contains("QR code", step.Action);
        Assert.Null(step.Note);
        Assert.Equal("j/123456", step.LinkHref);
    }

    [Fact]
    public void EmptyLobby_OnTheInstructorsOwnComputer_UsesASecondWindowInstead()
    {
        var step = TourGuide.ForHost(Hosting(SessionStatus.Lobby, participants: 0), OwnComputer)!;

        Assert.DoesNotContain("QR code", step.Action);
        Assert.NotNull(step.Note);
        Assert.Equal("j/123456", step.LinkHref);
    }

    [Fact]
    public void Lobby_OnceSomeoneJoins_ExplainsTheProjectorThenPointsAtStart()
    {
        var step = TourGuide.ForHost(Hosting(SessionStatus.Lobby), SchoolSite)!;

        Assert.Equal(5, step.Number);
        Assert.Equal(TourTarget.Start, step.Target);
        Assert.Equal(TourTarget.Projector, step.AlsoTarget);
        Assert.Contains("Phone1", step.Body);
        Assert.Contains("projector view", step.Body);
        Assert.Contains("Open projector view", step.Action);
    }

    [Fact]
    public void Highlight_RingsBothTargetsOfATwoPartStep()
    {
        var step = TourGuide.ForHost(Hosting(SessionStatus.Lobby), SchoolSite);

        Assert.Equal("tour-target", step.Highlight(TourTarget.Start));
        Assert.Equal("tour-target", step.Highlight(TourTarget.Projector));
        Assert.Null(step.Highlight(TourTarget.JoinInfo));
        Assert.Null(step.Highlight(TourTarget.None));
    }

    [Theory]
    [InlineData(QuestionPhase.Waiting, 0, "Press Open question.")]
    [InlineData(QuestionPhase.Open, 1, "Press Close and show results.")]
    [InlineData(QuestionPhase.Closed, 1, "Press Show results.")]
    [InlineData(QuestionPhase.ShowingResults, 1, "Press Reveal answer.")]
    [InlineData(QuestionPhase.AnswerRevealed, 1, "Press Finish quiz.")]
    public void RunningQuiz_NamesTheBlueButtonForEachPhase(QuestionPhase phase, int responses, string action)
    {
        var step = TourGuide.ForHost(Hosting(phase: phase, responses: responses), SchoolSite)!;

        Assert.Equal(6, step.Number);
        Assert.Equal(action, step.Action);
        Assert.Equal(TourTarget.NextStep, step.Target);
    }

    [Fact]
    public void OpenQuestion_WaitsForThePhoneToAnswer()
    {
        var step = TourGuide.ForHost(Hosting(phase: QuestionPhase.Open), SchoolSite)!;

        Assert.Equal("Answer the question on your phone.", step.Action);
        Assert.Equal(TourTarget.None, step.Target);
    }

    [Fact]
    public void OpenQuestion_WithNobodyJoined_DoesNotWaitForAnAnswer()
    {
        var step = TourGuide.ForHost(Hosting(phase: QuestionPhase.Open, participants: 0), SchoolSite)!;

        Assert.Equal("Press Close and show results.", step.Action);
        Assert.Equal(TourTarget.NextStep, step.Target);
    }

    [Fact]
    public void WordCloud_SkipsTheReveal()
    {
        var step = TourGuide.ForHost(Hosting(phase: QuestionPhase.ShowingResults, type: QuestionType.WordCloud), SchoolSite)!;

        Assert.Equal("Press Finish quiz.", step.Action);
    }

    [Fact]
    public void EarlierQuestions_MoveOnToTheNextOne()
    {
        var step = TourGuide.ForHost(Hosting(phase: QuestionPhase.AnswerRevealed, questionCount: 2), SchoolSite)!;

        Assert.Equal("Press Next question.", step.Action);
    }

    [Fact]
    public void TimedQuiz_HasNoButtonToPress()
    {
        var step = TourGuide.ForHost(Hosting(phase: QuestionPhase.Open, timed: true), SchoolSite)!;

        Assert.Equal(6, step.Number);
        Assert.Null(step.Action);
        Assert.Equal(TourTarget.None, step.Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinishedQuiz_PointsAtEndingTheSession(bool timed)
    {
        var step = TourGuide.ForHost(Hosting(phase: QuestionPhase.Finished, timed: timed), SchoolSite)!;

        Assert.Equal(TourGuide.StepCount, step.Number);
        Assert.Equal(TourTarget.EndSession, step.Target);
    }

    [Fact]
    public void Presentations_AreNotPartOfTheTour() =>
        Assert.Null(TourGuide.ForHost(Hosting(kind: SessionKind.Presentation), SchoolSite));
}
