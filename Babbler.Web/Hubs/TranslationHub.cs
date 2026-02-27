using Babbler.Web.Models;
using Babbler.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace Babbler.Web.Hubs;

public sealed class TranslationHub : Hub
{
    private const string RoomIdItemKey = "roomId";
    private readonly TranslationSessionService _session;

    public TranslationHub(TranslationSessionService session)
    {
        _session = session;
    }

    public override async Task OnConnectedAsync()
    {
        var roomId = Context.GetHttpContext()?.Request.Query["roomId"].ToString().Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(roomId))
        {
            Context.Items[RoomIdItemKey] = roomId;
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RoomIdItemKey, out var roomIdObject) &&
            roomIdObject is string roomId &&
            !string.IsNullOrWhiteSpace(roomId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task PublishClientTranslation(ClientTranslationUpdate payload)
    {
        if (!Context.Items.TryGetValue(RoomIdItemKey, out var roomIdObject) ||
            roomIdObject is not string roomId ||
            string.IsNullOrWhiteSpace(roomId))
        {
            throw new HubException("Room connection is required.");
        }

        return _session.PublishClientTranslationAsync(roomId, payload, Context.ConnectionAborted);
    }
}
