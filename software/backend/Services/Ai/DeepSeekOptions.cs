namespace backend.Services.Ai;

// Bound from the "DeepSeek" config section. ApiKey is never in appsettings —
// it comes from user-secrets locally and the DeepSeek__ApiKey env var in Azure (NF-7).
public class DeepSeekOptions
{
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-flash";
    public int TimeoutSeconds { get; set; } = 60;
}
