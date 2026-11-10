using CohortCompanion.Web.Data;
using CohortCompanion.Web.Onboarding;
using Microsoft.Data.Sqlite;

namespace CohortCompanion.Tests;

public sealed class TourServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private TestDbContextFactory factory = default!;
    private string userId = default!;
    private string otherId = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new TestDbContextFactory(connection);

        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var user = new ApplicationUser { UserName = "a@example.com", Email = "a@example.com", DisplayName = "A" };
        var other = new ApplicationUser { UserName = "b@example.com", Email = "b@example.com", DisplayName = "B" };
        db.Users.AddRange(user, other);
        await db.SaveChangesAsync();
        userId = user.Id;
        otherId = other.Id;
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private async Task<TourService> LoadedAsync(string forUserId)
    {
        var service = new TourService(factory);
        await service.LoadAsync(forUserId);
        return service;
    }

    [Fact]
    public void BeforeLoading_NothingIsOffered()
    {
        var service = new TourService(factory);

        Assert.Equal(TourStatus.Finished, service.Status);
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task NewAccount_HasNotStartedTheTour() =>
        Assert.Equal(TourStatus.NotStarted, (await LoadedAsync(userId)).Status);

    [Fact]
    public async Task Starting_IsRememberedOnTheNextPage()
    {
        await (await LoadedAsync(userId)).StartAsync();

        var nextPage = await LoadedAsync(userId);

        Assert.Equal(TourStatus.InProgress, nextPage.Status);
        Assert.True(nextPage.IsActive);
    }

    [Fact]
    public async Task Finishing_StopsTheOffer_AndTheTourCanBeTakenAgain()
    {
        var service = await LoadedAsync(userId);
        await service.FinishAsync();
        Assert.Equal(TourStatus.Finished, (await LoadedAsync(userId)).Status);

        await service.StartAsync();

        Assert.Equal(TourStatus.InProgress, (await LoadedAsync(userId)).Status);
    }

    [Fact]
    public async Task OneInstructorsTour_DoesNotTouchAnothers()
    {
        await (await LoadedAsync(userId)).StartAsync();

        Assert.Equal(TourStatus.NotStarted, (await LoadedAsync(otherId)).Status);
    }

    [Fact]
    public async Task UnknownAccount_IsNeverOfferedTheTour()
    {
        var service = await LoadedAsync("no-such-user");

        Assert.Equal(TourStatus.Finished, service.Status);
    }

    [Fact]
    public async Task ChangingBeforeLoading_DoesNothing()
    {
        await new TourService(factory).StartAsync();

        Assert.Equal(TourStatus.NotStarted, (await LoadedAsync(userId)).Status);
    }
}
