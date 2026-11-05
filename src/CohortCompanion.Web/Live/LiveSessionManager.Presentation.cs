using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Presentations;

namespace CohortCompanion.Web.Live;

/// <summary>A frozen copy of a deck, stored with its session. Speaker notes stay on the server.</summary>
public sealed record PresentationSnapshot(string Title, string ThemeId, IReadOnlyList<SlideSnapshot> Slides)
{
    public static PresentationSnapshot From(PresentationDocument document) => new(
        document.Title,
        document.ThemeId,
        document.Slides.Select(s => new SlideSnapshot(s.Layout, s.Content, s.SpeakerNotes)).ToList());

    public SlideView? ToView(int index) =>
        index >= 0 && index < Slides.Count ? new SlideView(index, Slides[index].Layout, Slides[index].Content) : null;
}

public sealed record SlideSnapshot(SlideLayout Layout, SlideContent Content, string SpeakerNotes);

/// <summary>Moving through a live presentation. Students only ever receive slides the instructor has shown.</summary>
public sealed partial class LiveSessionManager
{
    public Task<bool> GoToSlideAsync(Guid sessionId, string hostId, int index) =>
        CommandAsync(sessionId, hostId, async runtime =>
        {
            if (runtime.Status != SessionStatus.Running
                || runtime.Deck is not { } deck
                || index < 0
                || index >= deck.Slides.Count
                || index == runtime.CurrentIndex)
            {
                return false;
            }

            runtime.CurrentIndex = index;
            runtime.Extras.ShownSlides.Add(index);
            await PersistAsync(runtime);
            await AnnounceSlideAsync(runtime);
            return true;
        });

    public async Task<bool> NextSlideAsync(Guid sessionId, string hostId) =>
        sessions.TryGetValue(sessionId, out var runtime) && await GoToSlideAsync(sessionId, hostId, runtime.CurrentIndex + 1);

    public async Task<bool> PreviousSlideAsync(Guid sessionId, string hostId) =>
        sessions.TryGetValue(sessionId, out var runtime) && await GoToSlideAsync(sessionId, hostId, runtime.CurrentIndex - 1);

    private async Task AnnounceSlideAsync(LiveRuntime runtime)
    {
        if (runtime.Deck?.ToView(runtime.CurrentIndex) is { } slide)
        {
            await broadcaster.ToStudentsAsync(runtime.Id, HubEvents.SlideChanged, new SlideUpdate(runtime.CurrentIndex, slide));
        }

        await PushViewsAsync(runtime);
    }

    private static PresentationState? BuildPresentationState(LiveRuntime runtime) =>
        runtime.Deck is not { } deck
            ? null
            : new PresentationState(
                deck.ThemeId,
                runtime.CurrentIndex,
                deck.Slides.Count,
                runtime.Extras.ShownSlides.Order().Select(deck.ToView).OfType<SlideView>().ToList());

    private static DeckView? BuildDeckView(LiveRuntime runtime)
    {
        if (runtime.Deck is not { } deck || runtime.Status != SessionStatus.Running)
        {
            return null;
        }

        var index = runtime.CurrentIndex;
        return new DeckView(deck.ThemeId, deck.ToView(index), deck.ToView(index + 1), deck.Slides[index].SpeakerNotes);
    }
}
