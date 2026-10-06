using System.Security.Claims;
using System.Text.Json;
using backend.DTOs;
using backend.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

// AI endpoints — see specs/06-ai-features-spec.md §7.
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AiController : ControllerBase
{
    private const string UnavailableMessage = "The AI assistant is unavailable right now. Please try again.";

    private readonly ChatQuotaService _quota;
    private readonly ChatAssistantService _assistant;
    private readonly AiUsageTracker _tracker;
    private readonly ILogger<AiController> _logger;

    public AiController(ChatQuotaService quota, ChatAssistantService assistant, AiUsageTracker tracker, ILogger<AiController> logger)
    {
        _quota = quota;
        _assistant = assistant;
        _tracker = tracker;
        _logger = logger;
    }

    // Always the signed-in user from the JWT — never from the request (spec D-5)
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("chat/quota")]
    public async Task<IActionResult> GetChatQuota(CancellationToken ct)
    {
        var quota = await _quota.GetAsync(UserId, ct);
        return Ok(new { quota.Used, quota.Limit, quota.Remaining });
    }

    // Server-Sent Events: one JSON object per "data:" line —
    // {type:"tool",name} | {type:"text",text} | {type:"done",remaining} | {type:"error",message}.
    // Validation and quota failures are plain JSON errors returned before the stream starts.
    [HttpPost("chat")]
    public async Task<IActionResult> Chat(AiChatRequest req, [FromQuery] string? localDate, CancellationToken ct)
    {
        var message = req.Message?.Trim() ?? "";
        if (message.Length == 0)
            return BadRequest(new { message = "Message cannot be empty." });
        if (message.Length > ChatAssistantService.MaxMessageLength)
            return BadRequest(new { message = $"Message must be {ChatAssistantService.MaxMessageLength} characters or fewer." });

        var quota = await _quota.GetAsync(UserId, ct);
        if (quota.Exhausted)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"You've used all {quota.Limit} AI messages for today. Try again tomorrow." });

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        // Stops reverse proxies (e.g. nginx) from buffering the stream
        Response.Headers["X-Accel-Buffering"] = "no";

        var today = CheckInController.ResolveToday(localDate);
        try
        {
            await foreach (var e in _assistant.RunAsync(UserId, today, req.History, message, ct))
            {
                object payload = e switch
                {
                    AssistantText t => new { type = "text", text = t.Text },
                    AssistantToolStarted t => new { type = "tool", name = t.Name },
                    _ => throw new InvalidOperationException($"Unhandled assistant event {e.GetType().Name}"),
                };
                await WriteEventAsync(payload, ct);
            }

            await _tracker.SaveAsync(UserId, AiFeatures.Chat, success: true);
            var after = await _quota.GetAsync(UserId, ct);
            await WriteEventAsync(new { type = "done", remaining = after.Remaining }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client went away mid-stream: nothing left to write to. Logged as a
            // failure so it doesn't count against the quota.
            await _tracker.SaveAsync(UserId, AiFeatures.Chat, success: false, error: "Client disconnected");
        }
        catch (Exception ex)
        {
            // Headers are already sent, so errors must travel inside the stream (CA-9)
            _logger.LogError(ex, "AI chat failed for user {UserId}", UserId);
            await _tracker.SaveAsync(UserId, AiFeatures.Chat, success: false, error: ex.Message);
            await WriteEventAsync(new { type = "error", message = UnavailableMessage }, CancellationToken.None);
        }

        return new EmptyResult();
    }

    private async Task WriteEventAsync(object payload, CancellationToken ct)
    {
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
