using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Roadmap.Agent;
using Roadmap.Agent.Models;
using Roadmap.Api;
using Roadmap.Api.Jobs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRoadmapAgent(builder.Configuration);
builder.Services.AddSingleton<RoadmapJobStore>();
builder.Services.AddHostedService<RoadmapWorker>();
builder.Services.ConfigureHttpJsonOptions(o => RoadmapJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();

// Every generation costs money — limit how often it can be started.
// Once there is authentication, limit per user id instead of per IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("generate", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Demo page (wwwroot/index.html) to try the agent in a browser.
    // Not served in production: the real frontend lives elsewhere.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Which model the demo is running on (shown in the page header).
    app.MapGet("/api/info", (IOptions<AgentOptions> o) => Results.Ok(new
    {
        provider = o.Value.Provider,
        model = o.Value.ActiveModel,
        free = !o.Value.IsPaid,
        web_search = Providers.Normalize(o.Value.Provider) switch
        {
            Providers.Claude => "Claude web search",
            Providers.OpenAICompatible => string.IsNullOrWhiteSpace(o.Value.Tavily.ApiKey) ? "none" : "Tavily",
            _ => "fake",
        },
    }));
}

app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var roadmaps = app.MapGroup("/api/roadmaps");

// Start a generation. Responds immediately (202) — fetch the roadmap with GET.
roadmaps.MapPost("/", (UserProfile profile, RoadmapJobStore store) =>
{
    var errors = ProfileValidation.Validate(profile);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var job = store.Enqueue(profile);
    return job is null
        ? Results.Problem("The service is busy, please try again in a minute.", statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Accepted($"/api/roadmaps/{job.Id}", job);
}).RequireRateLimiting("generate");

// Status and result. The client polls until status is done or failed.
roadmaps.MapGet("/{id:guid}", (Guid id, RoadmapJobStore store) =>
    store.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

app.Run();
