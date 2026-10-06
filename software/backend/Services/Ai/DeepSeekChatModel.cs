using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace backend.Services.Ai;

// Thin client over DeepSeek's OpenAI-format /chat/completions (spec D-12).
// Hand-rolled rather than using an SDK because we need DeepSeek-specific
// request fields (thinking) and full control over SSE parsing.
public class DeepSeekChatModel : IChatModel
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly DeepSeekOptions _options;

    public DeepSeekChatModel(HttpClient http, IOptions<DeepSeekOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress ??= new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        using var httpRequest = BuildHttpRequest(request, stream: false);
        using var response = await SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct);

        var body = await response.Content.ReadFromJsonAsync<WireResponse>(Json, ct);
        var choice = body?.Choices?.FirstOrDefault()
            ?? throw new ChatModelException("DeepSeek returned no choices.");

        return new ChatResponse(
            choice.Message?.Content,
            choice.Message?.ToolCalls?.Select(tc => new ChatToolCall(tc.Id, tc.Function.Name, tc.Function.Arguments)).ToList() ?? [],
            NormalizeFinishReason(choice.FinishReason),
            ToUsage(body!.Usage));
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var httpRequest = BuildHttpRequest(request, stream: true);
        using var response = await SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        // Tool calls arrive in fragments keyed by index: the first fragment
        // carries id + name, later ones append pieces of the arguments string.
        var toolCalls = new SortedDictionary<int, ToolCallBuilder>();
        var finishReason = "stop";
        var usage = new ChatUsage(0, 0);

        while (await ReadLineAsync(reader, ct) is { } line)
        {
            // SSE: payload lines start with "data:"; blank lines separate events
            // and ":"-prefixed lines are keep-alive comments.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            var chunk = ParseChunk(data);
            if (chunk.Usage is not null) usage = ToUsage(chunk.Usage);

            var choice = chunk.Choices?.FirstOrDefault();
            if (choice is null) continue;
            if (choice.FinishReason is not null) finishReason = NormalizeFinishReason(choice.FinishReason);

            if (!string.IsNullOrEmpty(choice.Delta?.Content))
            {
                content.Append(choice.Delta.Content);
                yield return new TextDelta(choice.Delta.Content);
            }

            foreach (var fragment in choice.Delta?.ToolCalls ?? [])
            {
                if (!toolCalls.TryGetValue(fragment.Index, out var builder))
                    toolCalls[fragment.Index] = builder = new ToolCallBuilder();
                builder.Id ??= fragment.Id;
                builder.Name ??= fragment.Function?.Name;
                builder.Arguments.Append(fragment.Function?.Arguments);
            }
        }

        yield return new StreamCompleted(new ChatResponse(
            content.Length > 0 ? content.ToString() : null,
            toolCalls.Values.Select(b => b.Build()).ToList(),
            finishReason,
            usage));
    }

    private HttpRequestMessage BuildHttpRequest(ChatRequest request, bool stream)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ChatModelException("DeepSeek API key is not configured.");

        var body = new WireRequest(
            Model: _options.Model,
            Messages: request.Messages.Select(ToWire).ToList(),
            Tools: request.Tools?.Select(t => new WireTool("function", new WireFunctionDef(t.Name, t.Description, t.ParametersSchema))).ToList(),
            MaxTokens: request.MaxTokens,
            Stream: stream,
            StreamOptions: stream ? new WireStreamOptions(IncludeUsage: true) : null,
            // Thinking mode would require replaying reasoning_content on every
            // tool-carrying request, which our text-only history can't do (D-11).
            Thinking: new WireThinking("disabled"));

        return new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey) },
            Content = JsonContent.Create(body, options: Json),
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatModelException("Could not reach DeepSeek.", inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ChatModelException("DeepSeek request timed out.", inner: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new ChatModelException(
                $"DeepSeek returned {status}: {Truncate(errorBody, 500)}", status);
        }
        return response;
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct);
        }
        catch (IOException ex)
        {
            throw new ChatModelException("DeepSeek stream was interrupted.", inner: ex);
        }
    }

    private static WireResponse ParseChunk(string data)
    {
        try
        {
            return JsonSerializer.Deserialize<WireResponse>(data, Json)
                ?? throw new ChatModelException("DeepSeek sent an empty stream chunk.");
        }
        catch (JsonException ex)
        {
            throw new ChatModelException("DeepSeek sent a malformed stream chunk.", inner: ex);
        }
    }

    private static WireMessage ToWire(ChatMessage m) => new(
        Role: m.Role.ToString().ToLowerInvariant(),
        Content: m.Content,
        ToolCalls: m.ToolCalls is { Count: > 0 }
            ? m.ToolCalls.Select(tc => new WireToolCall(tc.Id, "function", new WireFunctionCall(tc.Name, tc.ArgumentsJson))).ToList()
            : null,
        ToolCallId: m.ToolCallId);

    private static ChatUsage ToUsage(WireUsage? u) => new(u?.PromptTokens ?? 0, u?.CompletionTokens ?? 0);

    private static string NormalizeFinishReason(string? reason) => reason?.ToLowerInvariant() ?? "stop";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();

        public ChatToolCall Build() => new(
            Id ?? throw new ChatModelException("DeepSeek streamed a tool call without an id."),
            Name ?? throw new ChatModelException("DeepSeek streamed a tool call without a name."),
            Arguments.Length > 0 ? Arguments.ToString() : "{}");
    }

    // ---- DeepSeek wire format (snake_case via the naming policy) ----

    private sealed record WireRequest(
        string Model,
        List<WireMessage> Messages,
        List<WireTool>? Tools,
        int MaxTokens,
        bool Stream,
        WireStreamOptions? StreamOptions,
        WireThinking Thinking);

    private sealed record WireThinking(string Type);
    private sealed record WireStreamOptions(bool IncludeUsage);
    private sealed record WireMessage(string Role, string? Content, List<WireToolCall>? ToolCalls, string? ToolCallId);
    private sealed record WireToolCall(string Id, string Type, WireFunctionCall Function);
    private sealed record WireFunctionCall(string Name, string Arguments);
    private sealed record WireTool(string Type, WireFunctionDef Function);
    private sealed record WireFunctionDef(string Name, string Description, JsonElement Parameters);

    // Used for both full responses (Message) and stream chunks (Delta).
    private sealed record WireResponse(List<WireChoice>? Choices, WireUsage? Usage);
    private sealed record WireChoice(WireResponseMessage? Message, WireDelta? Delta, string? FinishReason);
    private sealed record WireResponseMessage(string? Content, List<WireToolCall>? ToolCalls);
    private sealed record WireDelta(string? Content, List<WireToolCallDelta>? ToolCalls);
    private sealed record WireToolCallDelta(int Index, string? Id, WireFunctionDelta? Function);
    private sealed record WireFunctionDelta(string? Name, string? Arguments);
    private sealed record WireUsage(int PromptTokens, int CompletionTokens);
}
