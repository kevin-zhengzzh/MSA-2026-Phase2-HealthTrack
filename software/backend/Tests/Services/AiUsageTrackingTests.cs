using backend.Data;
using backend.Services.Ai;
using backend.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace backend.Tests.Services;

public class AiUsageTrackingTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static (UsageTrackingChatModel model, AiUsageTracker tracker, AppDbContext db) NewTrackedModel(FakeChatModel inner)
    {
        var db = NewDb();
        var tracker = new AiUsageTracker(db, NullLogger<AiUsageTracker>.Instance);
        var model = new UsageTrackingChatModel(inner, tracker, NullLogger<UsageTrackingChatModel>.Instance);
        return (model, tracker, db);
    }

    private static readonly ChatRequest AnyRequest = new([ChatMessage.User("hi")]);

    [Fact]
    public async Task CompleteAsync_AddsUsageToTracker()
    {
        var inner = new FakeChatModel().Returns(FakeChatModel.Text("hello", inputTokens: 12, outputTokens: 3));
        var (model, tracker, _) = NewTrackedModel(inner);

        var response = await model.CompleteAsync(AnyRequest);

        Assert.Equal("hello", response.Content);
        Assert.Equal(12, tracker.InputTokens);
        Assert.Equal(3, tracker.OutputTokens);
        Assert.Equal(1, tracker.ModelCalls);
    }

    [Fact]
    public async Task StreamAsync_PassesEventsThroughAndAddsUsageOnCompletion()
    {
        var inner = new FakeChatModel().Returns(FakeChatModel.Text("this week", inputTokens: 20, outputTokens: 2));
        var (model, tracker, _) = NewTrackedModel(inner);

        var events = new List<ChatStreamEvent>();
        await foreach (var e in model.StreamAsync(AnyRequest)) events.Add(e);

        Assert.Equal(2, events.OfType<TextDelta>().Count());
        Assert.IsType<StreamCompleted>(events[^1]);
        Assert.Equal(20, tracker.InputTokens);
        Assert.Equal(1, tracker.ModelCalls);
    }

    [Fact]
    public async Task MultipleModelCalls_AreSummedIntoOneRow()
    {
        // An agent loop: tool call round, then the final answer
        var inner = new FakeChatModel()
            .Returns(FakeChatModel.ToolCall("call_1", "get_workout_summary", inputTokens: 40, outputTokens: 10))
            .Returns(FakeChatModel.Text("You worked out 4 times.", inputTokens: 60, outputTokens: 8));
        var (model, tracker, db) = NewTrackedModel(inner);

        await model.CompleteAsync(AnyRequest);
        tracker.AddToolCall("get_workout_summary");
        await model.CompleteAsync(AnyRequest);
        await tracker.SaveAsync(userId: 7, AiFeatures.Chat, success: true);

        var row = await db.AiUsageLogs.SingleAsync();
        Assert.Equal(7, row.UserId);
        Assert.Equal("chat", row.Feature);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(18, row.OutputTokens);
        Assert.Equal("get_workout_summary", row.ToolsCalled);
        Assert.True(row.Success);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task FailedCall_RethrowsAndCanBeSavedAsFailure()
    {
        var inner = new FakeChatModel().Throws(new ChatModelException("DeepSeek returned 500: oops", 500));
        var (model, tracker, db) = NewTrackedModel(inner);

        var ex = await Assert.ThrowsAsync<ChatModelException>(() => model.CompleteAsync(AnyRequest));
        await tracker.SaveAsync(userId: 7, AiFeatures.Chat, success: false, error: ex.Message);

        Assert.Equal(0, tracker.ModelCalls);
        var row = await db.AiUsageLogs.SingleAsync();
        Assert.False(row.Success);
        Assert.Equal("DeepSeek returned 500: oops", row.Error);
    }

    [Fact]
    public async Task SaveAsync_TruncatesLongErrors()
    {
        var (_, tracker, db) = NewTrackedModel(new FakeChatModel());

        await tracker.SaveAsync(userId: 7, AiFeatures.Chat, success: false, error: new string('x', 2000));

        var row = await db.AiUsageLogs.SingleAsync();
        Assert.Equal(500, row.Error!.Length);
    }
}
