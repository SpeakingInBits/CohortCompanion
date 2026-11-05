using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Presentations;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Tests;

public sealed class LivePresentationTests : LiveSessionTestBase
{
    private static PresentationDocument Deck(int slides = 4) => new()
    {
        Id = 1,
        Title = "Deck",
        ThemeId = "midnight",
        Slides = Enumerable.Range(0, slides).Select(i => new EditableSlide
        {
            Layout = SlideLayout.TitleContent,
            Content = new SlideContent { Title = $"Slide {i}" },
            SpeakerNotes = $"SecretNote{i}",
        }).ToList(),
    };

    private async Task<(Guid Id, string Code)> RunningDeckAsync(int slides = 4)
    {
        var id = (await Manager.CreatePresentationSessionAsync(Deck(slides), HostId))!.Value;
        var code = (await Manager.GetHostViewAsync(id, HostId))!.JoinCode;
        await Manager.StartAsync(id, HostId);
        return (id, code);
    }

    private IEnumerable<SlideUpdate> SlideUpdates =>
        Broadcaster.ToStudents.Where(m => m.Method == HubEvents.SlideChanged).Select(m => (SlideUpdate)m.Argument!);

    [Fact]
    public async Task EmptyDeck_CannotStart() =>
        Assert.Null(await Manager.CreatePresentationSessionAsync(new PresentationDocument { Title = "Empty" }, HostId));

    [Fact]
    public async Task Start_ShowsTheFirstSlide()
    {
        await RunningDeckAsync();

        var update = Assert.Single(SlideUpdates);
        Assert.Equal(0, update.LiveIndex);
        Assert.Equal("Slide 0", update.Slide.Content.Title);
    }

    [Fact]
    public async Task Next_SendsOnlyTheNewSlide_NeverAhead()
    {
        var (id, _) = await RunningDeckAsync();

        await Manager.NextSlideAsync(id, HostId);

        var json = ToJson(Broadcaster.ToStudents.Select(m => m.Argument));
        Assert.Contains("Slide 1", json);
        Assert.DoesNotContain("Slide 2", json);
        Assert.DoesNotContain("Slide 3", json);
    }

    [Fact]
    public async Task SpeakerNotes_NeverReachStudentsOrTheProjector()
    {
        var (id, code) = await RunningDeckAsync();
        var joined = await Manager.JoinAsync(code, "Sam", "c1");
        await Manager.NextSlideAsync(id, HostId);
        var projector = await Manager.WatchProjectorAsync(id, HostId, "p1");

        var everything = ToJson(new object?[]
        {
            joined,
            projector,
            Broadcaster.ToStudents.Select(m => m.Argument),
            Broadcaster.ToProjector.Select(m => m.Argument),
        });

        Assert.DoesNotContain("SecretNote", everything);
    }

    [Fact]
    public async Task Host_SeesCurrentNextAndNotes()
    {
        var (id, _) = await RunningDeckAsync();

        var deck = (await Manager.GetHostViewAsync(id, HostId))!.Deck!;

        Assert.Equal("Slide 0", deck.Current!.Content.Title);
        Assert.Equal("Slide 1", deck.Next!.Content.Title);
        Assert.Equal("SecretNote0", deck.SpeakerNotes);
    }

    [Fact]
    public async Task LastSlide_HasNoNext_AndNextIsRejected()
    {
        var (id, _) = await RunningDeckAsync(slides: 2);
        await Manager.NextSlideAsync(id, HostId);

        Assert.Null((await Manager.GetHostViewAsync(id, HostId))!.Deck!.Next);
        Assert.False(await Manager.NextSlideAsync(id, HostId));
    }

    [Fact]
    public async Task Previous_FromTheFirstSlide_IsRejected()
    {
        var (id, _) = await RunningDeckAsync();

        Assert.False(await Manager.PreviousSlideAsync(id, HostId));
    }

    [Fact]
    public async Task LateJoiner_GetsOnlySlidesAlreadyShown()
    {
        var (id, code) = await RunningDeckAsync();
        await Manager.NextSlideAsync(id, HostId);

        var state = (await Manager.JoinAsync(code, "Sam", "c1")).State!;

        Assert.Equal([0, 1], state.Presentation!.ShownSlides.Select(s => s.Index));
        Assert.Equal(1, state.Presentation.LiveIndex);
        Assert.Equal("midnight", state.Presentation.ThemeId);
    }

    [Fact]
    public async Task JumpingAhead_DoesNotRevealTheSlidesItSkipped()
    {
        var (id, code) = await RunningDeckAsync();
        await Manager.GoToSlideAsync(id, HostId, 3);

        var state = (await Manager.JoinAsync(code, "Sam", "c1")).State!;

        Assert.Equal([0, 3], state.Presentation!.ShownSlides.Select(s => s.Index));
    }

    [Fact]
    public async Task GoingBack_KeepsLaterShownSlidesAvailable()
    {
        var (id, code) = await RunningDeckAsync();
        await Manager.NextSlideAsync(id, HostId);
        await Manager.PreviousSlideAsync(id, HostId);

        var state = (await Manager.JoinAsync(code, "Sam", "c1")).State!;

        Assert.Equal(0, state.Presentation!.LiveIndex);
        Assert.Equal([0, 1], state.Presentation.ShownSlides.Select(s => s.Index));
    }

    [Fact]
    public async Task Slides_FromAnotherInstructor_AreRejected()
    {
        var (id, _) = await RunningDeckAsync();

        Assert.False(await Manager.NextSlideAsync(id, "someone-else"));
    }

    [Fact]
    public async Task QuizCommands_DoNotApplyToPresentations()
    {
        var (id, _) = await RunningDeckAsync();

        Assert.False(await Manager.OpenQuestionAsync(id, HostId));
        Assert.False(await Manager.NextAsync(id, HostId));
    }

    [Fact]
    public async Task Projector_ShowsTheCurrentSlide()
    {
        var (id, _) = await RunningDeckAsync();
        await Manager.NextSlideAsync(id, HostId);

        var state = (await Manager.WatchProjectorAsync(id, HostId, "p1"))!;

        Assert.Equal("Slide 1", state.Slide!.Content.Title);
        Assert.Equal("midnight", state.ThemeId);
    }

    [Fact]
    public async Task Restart_RecoversThePositionAndShownSlides()
    {
        var (id, code) = await RunningDeckAsync();
        await Manager.NextSlideAsync(id, HostId);
        var token = (await Manager.JoinAsync(code, "Sam", "c1")).ParticipantToken!;

        var restarted = NewManager();
        await restarted.RecoverAsync();

        var state = (await restarted.RejoinAsync(token, "c2"))!;
        Assert.Equal(1, state.Presentation!.LiveIndex);
        Assert.Equal(2, state.Presentation.ShownSlides.Count);
    }

    [Fact]
    public async Task End_DeletesParticipants()
    {
        var (id, code) = await RunningDeckAsync();
        await Manager.JoinAsync(code, "Sam", "c1");

        Assert.True(await Manager.EndAsync(id, HostId));

        await using var db = Db.CreateDbContext();
        Assert.Equal(0, await db.Participants.CountAsync());
    }
}
