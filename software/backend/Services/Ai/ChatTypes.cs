using System.Text.Json;

namespace backend.Services.Ai;

// Provider-neutral chat types. Business code (controllers, tool executor)
// only ever sees these, never DeepSeek's wire format — see spec D-3.

public enum ChatRole { System, User, Assistant, Tool }

// ArgumentsJson is the raw JSON string the model produced; the tool executor
// parses and validates it, since models can emit malformed arguments.
public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatMessage(
    ChatRole Role,
    string? Content,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    public static ChatMessage System(string content) => new(ChatRole.System, content);
    public static ChatMessage User(string content) => new(ChatRole.User, content);
    public static ChatMessage Assistant(string? content, IReadOnlyList<ChatToolCall>? toolCalls = null) =>
        new(ChatRole.Assistant, content, toolCalls);
    public static ChatMessage ToolResult(string toolCallId, string content) =>
        new(ChatRole.Tool, content, ToolCallId: toolCallId);
}

// ParametersSchema is a JSON Schema object describing the tool's input.
public sealed record ChatToolDefinition(string Name, string Description, JsonElement ParametersSchema);

public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatToolDefinition>? Tools = null,
    int MaxTokens = 1024);

public sealed record ChatUsage(int InputTokens, int OutputTokens);

// FinishReason is normalized to lowercase: "stop", "tool_calls", "length", ...
public sealed record ChatResponse(
    string? Content,
    IReadOnlyList<ChatToolCall> ToolCalls,
    string FinishReason,
    ChatUsage Usage);

// Streaming emits zero or more TextDelta events, then exactly one
// StreamCompleted carrying the fully accumulated response (including any
// tool calls and usage), so callers don't have to reassemble it themselves.
public abstract record ChatStreamEvent;
public sealed record TextDelta(string Text) : ChatStreamEvent;
public sealed record StreamCompleted(ChatResponse Response) : ChatStreamEvent;

public class ChatModelException : Exception
{
    public int? StatusCode { get; }

    public ChatModelException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
