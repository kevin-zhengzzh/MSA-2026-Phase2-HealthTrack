namespace backend.DTOs;

// Role is "user" or "assistant"; anything else is treated as "user" so a client
// can never inject a system message (spec D-7).
public record AiChatTurn(string Role, string Text);

// Mode is the optional topic pill (spec §5.3): "review", "advice", "rank", "points" or "goal"
public record AiChatRequest(List<AiChatTurn>? History, string Message, string? Mode = null);
