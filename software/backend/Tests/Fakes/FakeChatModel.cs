using backend.Services.Ai;

namespace backend.Tests.Fakes;

// Scripted IChatModel for tests: returns queued responses in order and records
// every request it received. StreamAsync splits the queued response's text
// into word-sized TextDelta events before the final StreamCompleted.
public class FakeChatModel : IChatModel
{
    private readonly Queue<Func<ChatResponse>> _script = new();

    public List<ChatRequest> Requests { get; } = [];

    public FakeChatModel Returns(ChatResponse response)
    {
        _script.Enqueue(() => response);
        return this;
    }

    public FakeChatModel Throws(Exception exception)
    {
        _script.Enqueue(() => throw exception);
        return this;
    }

    public static ChatResponse Text(string content, int inputTokens = 10, int outputTokens = 5) =>
        new(content, [], "stop", new ChatUsage(inputTokens, outputTokens));

    public static ChatResponse ToolCall(string id, string name, string argumentsJson = "{}", int inputTokens = 10, int outputTokens = 5) =>
        new(null, [new ChatToolCall(id, name, argumentsJson)], "tool_calls", new ChatUsage(inputTokens, outputTokens));

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(Next());
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(request);
        var response = Next();
        if (response.Content is not null)
        {
            foreach (var word in response.Content.Split(' '))
            {
                await Task.Yield();
                yield return new TextDelta(word + " ");
            }
        }
        yield return new StreamCompleted(response);
    }

    private ChatResponse Next() =>
        _script.Count > 0
            ? _script.Dequeue()()
            : throw new InvalidOperationException("FakeChatModel has no more scripted responses.");
}
