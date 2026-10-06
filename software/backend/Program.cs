using System.Text;
using backend.Data;
using backend.DTOs;
using backend.Services;
using backend.Services.Ai;
using backend.Validators;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// CORS — allow Vite dev server and deployed frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// Application services
builder.Services.AddScoped<AuthService>();

// AI (specs/06-ai-features-spec.md). The API key is optional at startup so the
// rest of the app — and CI — runs without one; AI calls fail cleanly instead.
builder.Services.Configure<DeepSeekOptions>(builder.Configuration.GetSection("DeepSeek"));
builder.Services.AddHttpClient<DeepSeekChatModel>();
builder.Services.AddScoped<AiUsageTracker>();
builder.Services.AddScoped<ChatQuotaService>();
builder.Services.AddScoped<ChatToolExecutor>();
builder.Services.AddScoped<ChatAssistantService>();
builder.Services.AddSingleton(TimeProvider.System);
// Business code asks for IChatModel and gets the provider wrapped in the
// usage-tracking decorator.
builder.Services.AddScoped<IChatModel>(sp => new UsageTrackingChatModel(
    sp.GetRequiredService<DeepSeekChatModel>(),
    sp.GetRequiredService<AiUsageTracker>(),
    sp.GetRequiredService<ILogger<UsageTrackingChatModel>>()));

// FluentValidation
builder.Services.AddScoped<IValidator<RegisterRequest>, RegisterRequestValidator>();
builder.Services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
builder.Services.AddScoped<IValidator<WorkoutRecordRequest>, WorkoutRecordRequestValidator>();
builder.Services.AddScoped<IValidator<UpdateUsernameRequest>, UpdateUsernameRequestValidator>();

var app = builder.Build();

// Apply migrations and seed data on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapControllers();

app.Run();
