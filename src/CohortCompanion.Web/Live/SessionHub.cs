using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CohortCompanion.Web.Live;

public interface ISessionBroadcaster
{
    Task AddToStudentsAsync(Guid sessionId, string connectionId);

    Task RemoveFromStudentsAsync(Guid sessionId, string connectionId);

    Task AddToProjectorAsync(Guid sessionId, string connectionId);

    Task ToStudentsAsync(Guid sessionId, string method, object? argument);

    Task ToProjectorAsync(Guid sessionId, string method, object? argument);

    Task ToConnectionAsync(string connectionId, string method, object? argument);
}

public sealed class HubSessionBroadcaster(IHubContext<SessionHub> hub) : ISessionBroadcaster
{
    private static string Students(Guid id) => $"session:{id}:students";

    private static string Projector(Guid id) => $"session:{id}:projector";

    public Task AddToStudentsAsync(Guid sessionId, string connectionId) =>
        hub.Groups.AddToGroupAsync(connectionId, Students(sessionId));

    public Task RemoveFromStudentsAsync(Guid sessionId, string connectionId) =>
        hub.Groups.RemoveFromGroupAsync(connectionId, Students(sessionId));

    public Task AddToProjectorAsync(Guid sessionId, string connectionId) =>
        hub.Groups.AddToGroupAsync(connectionId, Projector(sessionId));

    public Task ToStudentsAsync(Guid sessionId, string method, object? argument) =>
        hub.Clients.Group(Students(sessionId)).SendAsync(method, argument);

    public Task ToProjectorAsync(Guid sessionId, string method, object? argument) =>
        hub.Clients.Group(Projector(sessionId)).SendAsync(method, argument);

    public Task ToConnectionAsync(string connectionId, string method, object? argument) =>
        hub.Clients.Client(connectionId).SendAsync(method, argument);
}

/// <summary>
/// The hub students and the projector connect to. Instructor controls don't come through here: the host
/// page runs on the server, so it calls <see cref="LiveSessionManager"/> directly.
/// </summary>
public sealed class SessionHub(LiveSessionManager manager) : Hub
{
    public Task<JoinResult> Join(string joinCode, string nickname) =>
        manager.JoinAsync(joinCode, nickname, Context.ConnectionId);

    public Task<StudentSessionState?> Rejoin(string participantToken) =>
        manager.RejoinAsync(participantToken, Context.ConnectionId);

    public Task<SubmitResult> SubmitAnswer(int questionId, AnswerPayload answer) =>
        manager.SubmitAnswerAsync(Context.ConnectionId, questionId, answer);

    [Authorize(Roles = Roles.Instructor)]
    public Task<ProjectorState?> WatchProjector(Guid sessionId) =>
        manager.WatchProjectorAsync(sessionId, Context.UserIdentifier!, Context.ConnectionId);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await manager.ConnectionClosedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
