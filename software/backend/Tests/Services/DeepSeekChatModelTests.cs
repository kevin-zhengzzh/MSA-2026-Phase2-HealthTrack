using System.Net;
using System.Text;
using System.Text.Json;
using backend.Services.Ai;
using Microsoft.Extensions.Options;

namespace backend.Tests.Services;

// Exercises DeepSeekChatModel against a stubbed HTTP handler — no network,
// no API key, safe for CI (spec NF-6).
public class DeepSeekChatModelTests
{
    private sealed class StubHandler(HttpStatusCode status, string responseBody, string contentType = "application/json")
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? RequestBody { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, contentType),
            };
        }
    }

    private static DeepSeekChatModel NewModel(StubHandler handler, string? apiKey = "test-key") =>
        new(new HttpClient(handler), Options.Create(new DeepSeekOptions { ApiKey = apiKey }));

    private static ChatRequest UserSays(string text, IReadOnlyList<ChatToolDefinition>? tools = null) =>
        new([ChatMessage.User(text)], tools);

    private static readonly ChatToolDefinition WorkoutTool = new(
        "get_workout_summary",
        "Get workout totals",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { range = new { type = "string" } } }));

    private const string TextResponse = """
        {"choices":[{"message":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":3}}
        """;

    [Fact]
    public async Task CompleteAsync_SendsModelBearerAndThinkingDisabled()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TextResponse);

        await NewModel(handler).CompleteAsync(UserSays("hi", [WorkoutTool]));

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("test-key", handler.Request.Headers.Authorization.Parameter);
        Assert.EndsWith("/chat/completions", handler.Request.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var root = body.RootElement;
        Assert.Equal("deepseek-flash", root.GetProperty("model").GetString());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("get_workout_summary",
            root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task CompleteAsync_ParsesTextAndUsage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TextResponse);

        var response = await NewModel(handler).CompleteAsync(UserSays("hi"));

        Assert.Equal("Hello!", response.Content);
        Assert.Empty(response.ToolCalls);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal(new ChatUsage(12, 3), response.Usage);
    }

    [Fact]
    public async Task CompleteAsync_ParsesToolCalls()
    {
        const string json = """
            {"choices":[{"message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_workout_summary","arguments":"{\"range\":\"this_week\"}"}}]},
              "finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":40,"completion_tokens":15}}
            """;
        var handler = new StubHandler(HttpStatusCode.OK, json);

        var response = await NewModel(handler).CompleteAsync(UserSays("how was my week?", [WorkoutTool]));

        Assert.Equal("tool_calls", response.FinishReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_workout_summary", call.Name);
        Assert.Equal("""{"range":"this_week"}""", call.ArgumentsJson);
    }

    [Fact]
    public async Task CompleteAsync_SerializesToolCallRoundTrip()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TextResponse);
        var request = new ChatRequest(
        [
            ChatMessage.User("how was my week?"),
            ChatMessage.Assistant(null, [new ChatToolCall("call_1", "get_workout_summary", """{"range":"this_week"}""")]),
            ChatMessage.ToolResult("call_1", """{"workoutCount":4}"""),
        ]);

        await NewModel(handler).CompleteAsync(request);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("call_1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        var tool = messages[2];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("""{"workoutCount":4}""", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task StreamAsync_YieldsTextDeltasThenCompletedResponse()
    {
        const string sse = """
            data: {"choices":[{"delta":{"role":"assistant","content":"This "},"finish_reason":null}]}

            : keep-alive

            data: {"choices":[{"delta":{"content":"week"},"finish_reason":null}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: {"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":2}}

            data: [DONE]

            """;
        var handler = new StubHandler(HttpStatusCode.OK, sse, "text/event-stream");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in NewModel(handler).StreamAsync(UserSays("hi"))) events.Add(e);

        Assert.Equal(["This ", "week"], events.OfType<TextDelta>().Select(d => d.Text));
        var completed = Assert.IsType<StreamCompleted>(events[^1]);
        Assert.Equal("This week", completed.Response.Content);
        Assert.Equal("stop", completed.Response.FinishReason);
        Assert.Equal(new ChatUsage(20, 2), completed.Response.Usage);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task StreamAsync_AccumulatesToolCallFragments()
    {
        // The arguments string is split across chunks, as real streams do
        const string sse = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_workout_summary","arguments":""}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"range\":"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"this_week\"}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var handler = new StubHandler(HttpStatusCode.OK, sse, "text/event-stream");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in NewModel(handler).StreamAsync(UserSays("how was my week?", [WorkoutTool]))) events.Add(e);

        Assert.Empty(events.OfType<TextDelta>());
        var completed = Assert.IsType<StreamCompleted>(Assert.Single(events));
        Assert.Null(completed.Response.Content);
        Assert.Equal("tool_calls", completed.Response.FinishReason);
        var call = Assert.Single(completed.Response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_workout_summary", call.Name);
        Assert.Equal("""{"range":"this_week"}""", call.ArgumentsJson);
    }

    [Fact]
    public async Task ErrorStatus_ThrowsChatModelExceptionWithStatusCode()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid key"}}""");

        var ex = await Assert.ThrowsAsync<ChatModelException>(() => NewModel(handler).CompleteAsync(UserSays("hi")));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("invalid key", ex.Message);
    }

    [Fact]
    public async Task MissingApiKey_ThrowsWithoutCallingDeepSeek()
    {
        var handler = new StubHandler(HttpStatusCode.OK, TextResponse);

        await Assert.ThrowsAsync<ChatModelException>(() => NewModel(handler, apiKey: null).CompleteAsync(UserSays("hi")));

        Assert.Equal(0, handler.CallCount);
    }
}
