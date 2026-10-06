namespace backend.Services.Ai;

// The only AI abstraction business code depends on (spec D-3, NF-5).
// Swapping providers means adding another implementation; tests use a fake.
public interface IChatModel
{
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);

    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);
}
