// Run the agent from the console — for the AI engineer: edit the prompts and see the result right away,
// without starting the backend or the frontend.
//
//   dotnet run --project src/Roadmap.Cli -- --profile samples/profiles/data-engineer-from-backend.json
//
// Options:
//   --out result.json                              where to save the roadmap (default: roadmap.json)
//   --list-models                                  list the models your free key can use, then exit
//   --Agent:Provider Fake                          stub with no network and no cost
//   --Agent:OpenAICompatible:Model gemini-...      a different free model
//   --Agent:Provider Claude                        the paid Claude setup
//
// Keys are read from user secrets (shared with the API), see README → "Free setup".

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Roadmap.Agent;
using Roadmap.Agent.Models;
using Roadmap.Agent.OpenAICompatible;
using Roadmap.Agent.Pipeline;

// Same number format on every machine: "12,345".
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var listModels = args.Contains("--list-models");
args = args.Where(a => a != "--list-models").ToArray();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // so appsettings.json next to the exe is found
    EnvironmentName = Environments.Development,  // loads user secrets, where the API keys live
});
builder.Services.AddRoadmapAgent(builder.Configuration);
using var host = builder.Build();

var options = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

if (listModels)
{
    if (Providers.Normalize(options.Provider) != Providers.OpenAICompatible)
    {
        Console.Error.WriteLine("--list-models works with Agent:Provider OpenAICompatible.");
        return 2;
    }

    try
    {
        var models = await host.Services.GetRequiredService<OpenAICompatibleChatClient>().ListModelsAsync(default);
        Console.WriteLine($"Models available at {options.OpenAICompatible.BaseUrl}:");
        foreach (var m in models)
        {
            Console.WriteLine($"  {m}");
        }

        Console.WriteLine("\nWhich of them are free and how many requests per day: https://aistudio.google.com/rate-limit");
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Error: {e.Message}");
        return 1;
    }
}

var profilePath = builder.Configuration["profile"];
if (string.IsNullOrWhiteSpace(profilePath))
{
    Console.Error.WriteLine("Pass a profile: --profile samples/profiles/data-engineer-from-backend.json");
    return 2;
}

UserProfile profile;
try
{
    profile = JsonSerializer.Deserialize<UserProfile>(await File.ReadAllTextAsync(profilePath), RoadmapJson.Options)
              ?? throw new JsonException("empty file");
}
catch (Exception e) when (e is IOException or JsonException)
{
    Console.Error.WriteLine($"Could not read profile '{profilePath}': {e.Message}");
    return 2;
}

var generator = host.Services.GetRequiredService<RoadmapGenerator>();
var search = Providers.Normalize(options.Provider) switch
{
    Providers.Claude => "Claude web search",
    Providers.OpenAICompatible => string.IsNullOrWhiteSpace(options.Tavily.ApiKey) ? "none (no Tavily key)" : "Tavily",
    _ => "fake",
};
var fallbacks = Providers.Normalize(options.Provider) == Providers.OpenAICompatible && options.OpenAICompatible.FallbackModels.Count > 0
    ? $" (if busy: {string.Join(", ", options.OpenAICompatible.FallbackModels)})"
    : "";
Console.WriteLine($"Provider: {options.Provider}, model: {options.ActiveModel}{fallbacks}, web search: {search}");
Console.WriteLine($"Goal: {profile.Goal}\n");

var started = DateTime.UtcNow;
GenerationResult result;
try
{
    var progress = new Progress<string>(m => Console.WriteLine($"  … {m}"));
    result = await generator.GenerateAsync(profile, progress);
}
catch (RoadmapGenerationException e)
{
    Console.Error.WriteLine($"\nError: {e.Message}");
    return 1;
}
catch (Exception e)
{
    Console.Error.WriteLine($"\nError: {e.GetType().Name}: {e.Message}");
    Console.Error.WriteLine("Check the API keys (README → \"Free setup\") or run with --Agent:Provider Fake.");
    return 1;
}

PrintRoadmap(result.Roadmap, profile);

var outPath = builder.Configuration["out"] ?? "roadmap.json";
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(result.Roadmap, RoadmapJson.Options));

var u = result.Usage;
var cost = options.IsPaid
    ? u.EstimateCostUsd(options.ActiveModel) is { } c ? $"${c:0.000}" : "unknown for this model"
    : "$0 (free tier)";
Console.WriteLine($"\nSaved: {Path.GetFullPath(outPath)}");
Console.WriteLine($"Model: {result.Model}");
Console.WriteLine($"Time: {(DateTime.UtcNow - started).TotalSeconds:0} s");
Console.WriteLine($"Tokens: input {u.InputTokens:N0}, output {u.OutputTokens:N0}, searches {u.WebSearches}");
Console.WriteLine($"Cost: {cost}");
foreach (var w in result.Warnings)
{
    Console.WriteLine($"Warning: {w}");
}

return 0;

static void PrintRoadmap(LearningRoadmap roadmap, UserProfile profile)
{
    Console.WriteLine($"\n=== {roadmap.Goal} ===");
    Console.WriteLine(roadmap.Summary);
    var weeks = profile.HoursPerWeek > 0 ? Math.Ceiling(roadmap.TotalHours / (double)profile.HoursPerWeek) : 0;
    Console.WriteLine($"Left to learn: {roadmap.TotalHours} h (≈ {weeks} weeks at {profile.HoursPerWeek} h/week)\n");

    foreach (var n in roadmap.Nodes)
    {
        var mark = n.Status switch
        {
            NodeStatus.Known => "[✓]",
            NodeStatus.Refresh => "[~]",
            _ => "[ ]",
        };
        var after = n.Prerequisites.Count > 0 ? $"  ← after: {string.Join(", ", n.Prerequisites)}" : "";
        Console.WriteLine($"{mark} {n.Title} ({n.Id}), {n.EstimatedHours} h{after}");
        foreach (var r in n.Resources)
        {
            Console.WriteLine($"      {(r.Free ? "free" : "paid")} · {r.Type}: {r.Title} — {r.Url}");
        }
    }

    Console.WriteLine("\n[✓] already know   [~] refresh   [ ] to learn");
}
