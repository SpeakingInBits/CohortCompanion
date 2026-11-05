namespace CohortCompanion.Shared;

public enum QuizMode
{
    InstructorPaced = 0,
    Timed = 1,
}

public enum QuestionType
{
    MultipleChoice = 0,
    WordCloud = 1,
    FillInBlank = 2,
}

public enum MatchMode
{
    Exact = 0,
    CaseInsensitive = 1,
    Fuzzy = 2,
}
