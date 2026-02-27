using Babbler.Web.Hubs;
using Babbler.Web.Options;
using Babbler.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SpeechOptions>(
    builder.Configuration.GetSection(SpeechOptions.SectionName));
builder.Services.Configure<SessionLimitsOptions>(
    builder.Configuration.GetSection(SessionLimitsOptions.SectionName));
builder.Services.Configure<BitStoreOptions>(
    builder.Configuration.GetSection(BitStoreOptions.SectionName));
builder.Services.AddHttpClient("bitstore");
builder.Services.AddSignalR();
builder.Services.AddSingleton<IMonthlyUsageStore, BitStoreUsageStore>();
builder.Services.AddSingleton<TranslationSessionService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/display.html", StringComparison.OrdinalIgnoreCase))
    {
        var roomId = context.Request.Query["roomId"].ToString();
        if (string.IsNullOrWhiteSpace(roomId))
        {
            context.Response.Redirect("/join.html");
            return;
        }

        var session = context.RequestServices.GetRequiredService<TranslationSessionService>();
        if (!session.HasDisplayAccess(roomId, context))
        {
            context.Response.Redirect($"/join.html?roomId={Uri.EscapeDataString(roomId)}");
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path.Value?.EndsWith(".html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
    }
});

app.MapHub<TranslationHub>("/hubs/translation");

app.MapGet("/api/diag", async (
    IHostEnvironment environment,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    var rooms = await session.GetRoomsAsync(cancellationToken);
    var running = rooms.Count(room => room.IsRunning);
    return Results.Ok(new
    {
        utcNow = DateTimeOffset.UtcNow,
        environment = environment.EnvironmentName,
        version = typeof(Program).Assembly.GetName().Version?.ToString(),
        roomsCount = rooms.Count,
        runningRoomsCount = running,
        roomIds = rooms.Select(room => room.RoomId).Take(12).ToArray()
    });
});

app.MapGet("/api/speech/token", async (
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await session.GetBrowserSpeechTokenAsync(cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/rooms", async (
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
    Results.Ok(await session.GetRoomsAsync(cancellationToken)));

app.MapPost("/api/rooms", async (
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
    Results.Ok(await session.CreateRoomAsync(cancellationToken)));

app.MapGet("/api/rooms/{roomId}/access-info", async (
    string roomId,
    HttpContext httpContext,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    try
    {
        var room = await session.GetRoomAccessInfoAsync(roomId, cancellationToken);
        var encodedRoomId = Uri.EscapeDataString(room.RoomId);
        var joinUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/join.html?roomId={encodedRoomId}";
        return Results.Ok(new
        {
            roomId = room.RoomId,
            pin = room.Pin,
            joinUrl
        });
    }
    catch (InvalidOperationException ex)
    {
        return ToRoomError(ex);
    }
});

app.MapPost("/api/rooms/{roomId}/access/verify", async (
    string roomId,
    VerifyPinRequest request,
    HttpContext httpContext,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Pin))
    {
        return Results.BadRequest(new { error = "PIN is required." });
    }

    try
    {
        if (!await session.VerifyRoomPinAsync(roomId, request.Pin, httpContext, cancellationToken))
        {
            return Results.Json(new { error = "Invalid PIN." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new { success = true });
    }
    catch (InvalidOperationException ex)
    {
        return ToRoomError(ex);
    }
});

app.MapGet("/api/rooms/{roomId}/session/status", async (
    string roomId,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await session.GetStatusAsync(roomId, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return ToRoomError(ex);
    }
});

app.MapPost("/api/rooms/{roomId}/session/start", async (
    string roomId,
    StartSessionRequest request,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SourceLanguage))
    {
        return Results.BadRequest(new { error = "SourceLanguage is required." });
    }

    try
    {
        await session.StartAsync(
            roomId,
            request.SourceLanguage.Trim(),
            request.TargetLanguage,
            cancellationToken);
        return Results.Ok(await session.GetStatusAsync(roomId, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return ToRoomError(ex);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/rooms/{roomId}/session/target", async (
    string roomId,
    SetTargetRequest request,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.TargetLanguage))
    {
        return Results.BadRequest(new { error = "TargetLanguage is required." });
    }

    try
    {
        await session.SetTargetLanguageAsync(roomId, request.TargetLanguage.Trim(), cancellationToken);
        return Results.Ok(await session.GetStatusAsync(roomId, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return ToRoomError(ex);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/rooms/{roomId}/session/stop", async (
    string roomId,
    string? reason,
    TranslationSessionService session,
    CancellationToken cancellationToken) =>
{
    try
    {
        await session.StopAsync(roomId, reason, cancellationToken);
        return Results.Ok(await session.GetStatusAsync(roomId, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return ToRoomError(ex);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static IResult ToRoomError(InvalidOperationException exception)
{
    if (exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { error = exception.Message });
    }

    return Results.BadRequest(new { error = exception.Message });
}

internal sealed record VerifyPinRequest(string Pin);
internal sealed record StartSessionRequest(string SourceLanguage, string? TargetLanguage);
internal sealed record SetTargetRequest(string TargetLanguage);
