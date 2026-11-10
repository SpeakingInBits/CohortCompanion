namespace CohortCompanion.Web.Onboarding;

/// <summary>Where an instructor is with the getting-started tour. Stored on their account, so it follows them between devices.</summary>
public enum TourStatus
{
    /// <summary>Never offered an answer: the tour is offered the first time they sign in.</summary>
    NotStarted = 0,

    InProgress = 1,

    /// <summary>Completed, skipped, or ended early. It can be taken again from the home page.</summary>
    Finished = 2,
}
