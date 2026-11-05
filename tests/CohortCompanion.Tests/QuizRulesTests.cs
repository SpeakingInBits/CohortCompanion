using CohortCompanion.Shared;
using CohortCompanion.Web.Data;
using CohortCompanion.Web.Quizzes;

namespace CohortCompanion.Tests;

public class QuizRulesTests
{
    private static Question MultipleChoice(params bool[] correct) => new()
    {
        Type = QuestionType.MultipleChoice,
        Prompt = "<p>Pick one</p>",
        Options = correct.Select((c, i) => new ChoiceOption { Order = i, Text = $"Option {i}", IsCorrect = c }).ToList(),
    };

    private static Quiz QuizWith(params Question[] questions) => new() { Title = "Quiz", Questions = [.. questions] };

    [Fact]
    public void ValidMultipleChoiceQuiz_HasNoProblems() =>
        Assert.Empty(QuizRules.Validate(QuizWith(MultipleChoice(true, false))));

    [Fact]
    public void EmptyQuiz_NeedsTitleAndQuestion()
    {
        var problems = QuizRules.Validate(new Quiz());

        Assert.Contains("The quiz needs a title.", problems);
        Assert.Contains("Add at least one question.", problems);
    }

    [Fact]
    public void PromptOfOnlyEmptyTags_CountsAsEmpty()
    {
        var question = MultipleChoice(true, false);
        question.Prompt = "<p>&nbsp;</p><p><br></p>";

        Assert.Contains("Question 1 needs a prompt.", QuizRules.Validate(QuizWith(question)));
    }

    [Fact]
    public void MultipleChoice_NeedsTwoToSixOptions()
    {
        Assert.Contains("Question 1 needs between 2 and 6 options.", QuizRules.Validate(QuizWith(MultipleChoice(true))));
        Assert.Contains("Question 1 needs between 2 and 6 options.", QuizRules.Validate(QuizWith(MultipleChoice(true, false, false, false, false, false, false))));
    }

    [Fact]
    public void MultipleChoice_NeedsACorrectOption() =>
        Assert.Contains("Question 1 needs a correct option.", QuizRules.Validate(QuizWith(MultipleChoice(false, false))));

    [Fact]
    public void SeveralCorrectOptions_RequireMultipleSelect()
    {
        var question = MultipleChoice(true, true);
        Assert.Contains("Question 1 has several correct options, so it must allow multiple select.", QuizRules.Validate(QuizWith(question)));

        question.AllowMultipleSelect = true;
        Assert.Empty(QuizRules.Validate(QuizWith(question)));
    }

    [Fact]
    public void FillInBlank_EveryBlankNeedsAnAnswer()
    {
        var question = new Question
        {
            Type = QuestionType.FillInBlank,
            Prompt = "The {{1}} and the {{2}}",
            BlankAnswers = [new BlankAnswer { BlankIndex = 1, AcceptedText = "cat" }],
        };

        Assert.Contains("Question 1: blank 2 needs an accepted answer.", QuizRules.Validate(QuizWith(question)));
    }

    [Fact]
    public void FillInBlank_BlanksMustBeNumberedInOrder()
    {
        var question = new Question
        {
            Type = QuestionType.FillInBlank,
            Prompt = "The {{2}}",
            BlankAnswers = [new BlankAnswer { BlankIndex = 2, AcceptedText = "cat" }],
        };

        Assert.Contains(QuizRules.Validate(QuizWith(question)), p => p.Contains("number the blanks in order"));
    }

    [Fact]
    public void TimedQuiz_NeedsATimeLimitForEveryQuestion()
    {
        var quiz = QuizWith(MultipleChoice(true, false));
        quiz.Mode = QuizMode.Timed;
        quiz.DefaultTimeLimitSeconds = null;

        Assert.Contains("Question 1 needs a time limit, because this quiz is timed.", QuizRules.Validate(quiz));

        quiz.Questions[0].TimeLimitSeconds = 20;
        Assert.Empty(QuizRules.Validate(quiz));
    }

    [Fact]
    public void BlankTokens_FindsDistinctNumbersInOrder() =>
        Assert.Equal([1, 2], BlankTokens.Find("{{2}} then {{1}} then {{ 2 }}"));
}
