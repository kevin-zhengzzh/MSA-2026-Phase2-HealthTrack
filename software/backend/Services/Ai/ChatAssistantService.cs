using System.Globalization;
using System.Runtime.CompilerServices;
using backend.DTOs;

namespace backend.Services.Ai;

public abstract record AssistantEvent;
public sealed record AssistantText(string Text) : AssistantEvent;
public sealed record AssistantToolStarted(string Name) : AssistantEvent;

// The agent loop behind POST /api/ai/chat (spec §5.1). Streams the model's
// text as it arrives; when the model asks for tools, runs them and calls the
// model again with the results, until it answers or MaxModelCalls is hit.
// Knows nothing about HTTP — AiController turns these events into SSE.
public class ChatAssistantService
{
    public const int MaxModelCalls = 5;          // NF-3
    public const int MaxHistoryTurns = 20;       // CA-7
    public const int MaxMessageLength = 1000;    // §7
    private const int MaxReplyTokens = 1024;

    public const string LoopLimitReply =
        "Sorry, I couldn't finish looking that up. Could you ask in a more specific way?";

    // §9 — kept verbatim in the spec; update both together
    private const string SystemPrompt =
        "You are the HealthTrack assistant. You help the signed-in user understand their own workouts, " +
        "check-ins, streaks, goals, leaderboard ranks and points in this app. Whenever an answer depends on the user's data, call the " +
        "relevant tool first — even for follow-up questions and even if earlier messages mention numbers, " +
        "because data can change and earlier replies may be incomplete. Never guess or invent numbers. " +
        "Call tools directly without announcing that you are about to look something up. " +
        "Reply in the same language as the user's latest message. Keep answers short and " +
        "encouraging. Only discuss fitness and this app; politely decline anything else. Do not diagnose " +
        "medical conditions; for health concerns, suggest consulting a professional.";

    private readonly IChatModel _model;
    private readonly ChatToolExecutor _tools;

    public ChatAssistantService(IChatModel model, ChatToolExecutor tools)
    {
        _model = model;
        _tools = tools;
    }

    public async IAsyncEnumerable<AssistantEvent> RunAsync(
        int userId,
        DateOnly today,
        IReadOnlyList<AiChatTurn>? history,
        string message,
        ChatMode? mode = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(today, history, message, mode);
        // A picked topic narrows the tools to what it needs (spec §5.3)
        var tools = ChatToolExecutor.DefinitionsFor(mode?.Tools);
        // Models sometimes say a sentence before calling a tool; without a break
        // it would run straight into the next round's answer ("...progress.This week").
        var needsBreak = false;

        for (var call = 0; call < MaxModelCalls; call++)
        {
            ChatResponse? response = null;
            await foreach (var e in _model.StreamAsync(new ChatRequest(messages, tools, MaxReplyTokens), ct))
            {
                if (e is TextDelta delta)
                {
                    if (needsBreak)
                    {
                        yield return new AssistantText("\n\n");
                        needsBreak = false;
                    }
                    yield return new AssistantText(delta.Text);
                }
                else if (e is StreamCompleted completed) response = completed.Response;
            }

            if (response is null || response.ToolCalls.Count == 0) yield break;

            // Echo the model's tool request back, then one result per call —
            // the API rejects a follow-up where any tool_call id lacks a result.
            needsBreak = !string.IsNullOrEmpty(response.Content);
            messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls));
            foreach (var toolCall in response.ToolCalls)
            {
                yield return new AssistantToolStarted(toolCall.Name);
                var result = await _tools.ExecuteAsync(userId, today, toolCall, ct);
                messages.Add(ChatMessage.ToolResult(toolCall.Id, result));
            }
        }

        yield return new AssistantText(LoopLimitReply);
    }

    internal static List<ChatMessage> BuildMessages(DateOnly today, IReadOnlyList<AiChatTurn>? history, string message, ChatMode? mode = null)
    {
        // Invariant culture so the date reads the same on any server locale
        var system = $"{SystemPrompt}\n\nToday is {today.ToString("dddd, yyyy-MM-dd", CultureInfo.InvariantCulture)} in the user's time zone.";
        if (mode is not null)
            system += $"\n\nThe user picked a topic for this message. {mode.Instructions}";

        List<ChatMessage> messages = [ChatMessage.System(system)];

        foreach (var turn in (history ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Text)).TakeLast(MaxHistoryTurns))
        {
            // History is client-supplied: cap each turn's length to bound cost, and
            // never let it claim the system role.
            var text = turn.Text.Length > MaxMessageLength * 2 ? turn.Text[..(MaxMessageLength * 2)] : turn.Text;
            messages.Add(turn.Role == "assistant" ? ChatMessage.Assistant(text) : ChatMessage.User(text));
        }

        messages.Add(ChatMessage.User(message));
        return messages;
    }
}
