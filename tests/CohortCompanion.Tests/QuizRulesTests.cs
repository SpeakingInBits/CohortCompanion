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

        Assert.Contains(QuizRules.Validate(QuizWith(question)), p => p.Contains("aren't numbered in order"));
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

    [Fact]
    public void Renumber_NumbersBlanksInReadingOrder_AndSaysWhatEachWas()
    {
        var (prompt, was) = BlankTokens.Renumber("<p>The {{7}} sat on the {{3}}, said the {{ 7 }}.</p>");

        Assert.Equal("<p>The {{1}} sat on the {{2}}, said the {{1}}.</p>", prompt);
        Assert.Equal([7, 3], was);
    }

    [Fact]
    public void Renumber_LeavesAPromptWithoutBlanksAlone()
    {
        var (prompt, was) = BlankTokens.Renumber("<p>Hello</p>");

        Assert.Equal("<p>Hello</p>", prompt);
        Assert.Empty(was);
        Assert.Equal("", BlankTokens.Renumber(null).Prompt);
    }

    [Fact]
    public void BlankChips_RoundTripBetweenStoredAndEditorForms()
    {
        var editor = BlankChips.ToEditor("<p>The {{1}} and the {{2}}</p>");

        Assert.DoesNotContain("{{", editor);
        Assert.Contains("data-blank=\"2\"", editor);
        Assert.Equal("<p>The {{1}} and the {{2}}</p>", BlankChips.FromEditor(editor));
    }

    [Fact]
    public void BlankChips_AreReadBackHoweverTheBrowserWritesThem()
    {
        // A browser is free to reorder a chip's attributes and add its own. It also puts hard spaces around a chip.
        var html = "<p>A&nbsp;<span contenteditable=\"false\" data-blank=\"4\" style=\"color: red;\" class=\"blank-chip\">Blank</span>&nbsp;here</p>";

        Assert.Equal("<p>A {{4}} here</p>", BlankChips.FromEditor(html));
        Assert.Equal("<p>Tom&nbsp;&amp; Jerry</p>", BlankChips.FromEditor("<p>Tom&nbsp;&amp; Jerry</p>"));
    }

    [Fact]
    public void BlankChips_RemoveTakesOutOnlyThatBlank()
    {
        var editor = BlankChips.ToEditor("<p>{{1}} then {{2}}</p>") + "{{1}}";

        Assert.Equal("<p> then {{2}}</p>", BlankChips.FromEditor(BlankChips.Remove(editor, 1)));
    }
}
