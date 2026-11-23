# Roadmap Agent — MVP

An AI agent that builds a personal learning roadmap (a skill graph) for the user's goal:
"I want to become a Data Engineer" → a graph of topics with dependencies, hour estimates and links to materials.
It takes into account what the person already knows and searches the internet for up-to-date information.

Backend — ASP.NET (.NET 10). The agent runs **for free** by default (a free-tier model + free web search),
and can be switched to Claude (paid, best quality) with one setting.

## How it works

```
Questionnaire (goal, skills 0–3, background, hours/week, deadline)
   │
   ├─ 1. Research       web search → notes + sources
   │                    free: 4 Tavily searches   |   Claude: built-in web_search
   │                    (cached per goal: "Data Engineer" research is the same for everyone)
   ├─ 2. Build roadmap  profile + notes → JSON graph that follows a schema
   │                    free: Gemini (or any OpenAI-compatible model)   |   Claude
   ├─ 3. Validate       no cycles, all dependencies exist, ids are unique
   │                    → on errors the model gets the list of problems and fixes them (1 attempt)
   └─ 4. Links          HTTP check, dead links (404/410) are removed
```

Steps 1 and 2 are separate on purpose: research can be cached per goal, while personalization happens
in step 2. In the free setup step 1 makes no model call at all, which saves the free model's daily quota.

## Providers

| `Agent:Provider` | Model | Web search | Cost |
|---|---|---|---|
| `OpenAICompatible` (default) | Gemini free tier, or Groq / OpenRouter / local Ollama | Tavily free plan (optional) | $0 |
| `Claude` | Claude (`claude-opus-5-5` by default) | Claude built-in web search | ~$0.05–0.5 per roadmap |
| `Fake` | stub, always the same graph | none | $0, no network |

## Free setup (5 minutes, no credit card)

Requires the .NET 10 SDK.

**1. Free model key — Google Gemini.** Open [Google AI Studio → API keys](https://aistudio.google.com/apikey)
and create a key. Check which models have free quota on the
[rate limits page](https://aistudio.google.com/rate-limit) — pick a **Flash** model.

**2. Free search key — Tavily (optional, recommended).** Sign up at [app.tavily.com](https://app.tavily.com)
and copy the API key (free plan: 1,000 searches per month). Without it the agent still works,
but builds the roadmap from the model's own knowledge, without fresh sources.

**3. Save the keys** with .NET user secrets — they are stored outside the repository, so they never end up in git.
The API and the CLI share them. The easiest way on macOS/Linux is `./scripts/setup-free-keys.sh`:
it asks for both keys with hidden input. Or set them one by one:

```bash
dotnet user-secrets set "Agent:OpenAICompatible:ApiKey" "YOUR_GEMINI_KEY" --project src/Roadmap.Api
```

```bash
dotnet user-secrets set "Agent:Tavily:ApiKey" "tvly-YOUR_TAVILY_KEY" --project src/Roadmap.Api
```

(Environment variables `GEMINI_API_KEY` and `TAVILY_API_KEY` work too.)

**4. Check that the models exist for your key.** The default is `gemini-3.8-flash` with fallbacks
`gemini-3.6-flash`, `gemini-3.5-flash`, `gemini-3.1-flash-lite` (in `appsettings.json`). If an id is missing
from the list, change it:

```bash
dotnet run --project src/Roadmap.Cli -- --list-models
```

```bash
dotnet user-secrets set "Agent:OpenAICompatible:Model" "MODEL_ID_FROM_THE_LIST" --project src/Roadmap.Api
```

**5. Run.** From the terminal:

```bash
dotnet run --project src/Roadmap.Cli -- --profile samples/profiles/data-engineer-from-backend.json
```

Or the API with the demo page at http://localhost:5191:

```bash
dotnet run --project src/Roadmap.Api --launch-profile http
```

Sample requests are in `src/Roadmap.Api/Roadmap.Api.http` (opens in Rider / VS / VS Code with REST Client).

### What "free" means here

- **Limits.** Free tiers have per-minute and per-day limits (shown in AI Studio for your project).
  One roadmap = 1–2 model requests + 4 Tavily searches; research is cached per goal for a week.
- **Busy models.** Free models are often overloaded (HTTP 503), out of quota (429) or slow. The agent retries once,
  then switches to the next model in `FallbackModels` (also when the model gives no answer within
  `FallbackAfterSeconds`) and stays on the model that worked for 10 minutes. Quotas are per model,
  so a fallback usually still has quota. If every model fails, the user gets a clear error.
- **Speed.** `ReasoningEffort: low` makes a roadmap take ~35–50 s instead of ~4 minutes, with similar quality
  in our tests. Remove it from `appsettings.json` for more careful (and slower) roadmaps.
- **Data.** On free tiers providers may use requests to improve their products. Fine for a demo;
  don't send personal data.
- **Quality.** A free Flash model is good enough for a demo; Claude gives more accurate roadmaps.

### Other free models

Any chat-completions API in the OpenAI format works — change three settings
(`Agent:OpenAICompatible:BaseUrl`, `:Model`, `:ApiKey`):

| Provider | BaseUrl | Model | Key |
|---|---|---|---|
| Google Gemini (default) | `https://generativelanguage.googleapis.com/v1beta/openai/` | a Flash model with free quota | [AI Studio](https://aistudio.google.com/apikey) |
| Groq | `https://api.groq.com/openai/v1/` | see [Groq models](https://console.groq.com/docs/models) | [console.groq.com](https://console.groq.com) |
| OpenRouter | `https://openrouter.ai/api/v1/` | any model id ending in `:free` | [openrouter.ai](https://openrouter.ai) |
| Ollama (on your computer, no limits) | `http://localhost:11434/v1/` | e.g. `qwen3:8b` | not needed |

Free tiers change often; if one runs out, switch to another. Models differ in how well they follow
a JSON Schema — the agent falls back automatically (JSON Schema → JSON mode → schema in the prompt)
and validates the result anyway. With Groq, lower `MaxOutputTokens` if the model rejects 32,000.

### Stub mode (Fake)

No keys and no internet — for backend and frontend work:

```bash
dotnet run --project src/Roadmap.Cli -- --profile samples/profiles/data-engineer-from-backend.json --Agent:Provider Fake
```

```bash
Agent__Provider=Fake dotnet run --project src/Roadmap.Api --launch-profile http
```

## Structure

```
src/
  Roadmap.Agent/            ← the AI part (library)
    Models/                 UserProfile (input), LearningRoadmap (output — the contract with the frontend)
    Pipeline/               RoadmapGenerator (pipeline), RoadmapValidator, HttpLinkChecker, cache
    Claude/                 Prompts.cs ← prompts (shared by all providers), RoadmapSchema.cs ← response JSON schema,
                            ClaudeResearcher, ClaudeRoadmapBuilder
    OpenAICompatible/       free model client (Gemini, Groq, OpenRouter, Ollama) and its roadmap builder
    Search/                 TavilyResearcher (free search), NoSearchResearcher (fallback)
    Fake/                   stubs with no network and no cost
    AgentOptions.cs         settings (the "Agent" section)
  Roadmap.Api/              ← ASP.NET API: POST/GET /api/roadmaps, background queue, rate limits,
                              demo page wwwroot/index.html (Development only)
  Roadmap.Cli/              ← run the agent from the terminal (for working on prompts)
tests/Roadmap.Agent.Tests/  ← tests (no network: HTTP answers come from stubs)
samples/profiles/           ← sample profiles
```

The backend only needs `services.AddRoadmapAgent(configuration)` and `RoadmapGenerator` from DI.
The model sits behind the `IResearcher` / `IRoadmapBuilder` interfaces, so it can be replaced.

## Claude (paid)

Get a key in the [Claude Console](https://platform.claude.com/) and put it in an environment variable:

```bash
export ANTHROPIC_API_KEY="sk-ant-..."
```

```bash
dotnet run --project src/Roadmap.Cli -- --profile samples/profiles/data-engineer-from-backend.json --Agent:Provider Claude
```

Roughly, per new roadmap:

| Model | $ per 1M tokens (input / output) | ≈ per roadmap |
|---|---|---|
| `claude-opus-5-5` (default) | 4 / 20 | ~$0.5 |
| `claude-sonnet-5-5` | 2 / 10 | ~$0.25 |
| `claude-haiku-5-5` | 0.10 / 0.50 | ~$0.05 (mostly searches) |

Plus web search: $10 per 1,000 searches. Check prices on the
[pricing page](https://platform.claude.com/docs/en/about-claude/pricing) and set a spend limit in the Claude Console.

## Settings (the `Agent` section)

| Key | Default | What it does |
|---|---|---|
| `Provider` | `OpenAICompatible` | `OpenAICompatible`, `Claude` or `Fake` |
| `ResearchCacheHours` | `168` | how long the research for a goal is cached |
| `CheckLinks` | `true` | HTTP link checking |
| `OpenAICompatible:BaseUrl` | Gemini | address of the OpenAI-compatible API |
| `OpenAICompatible:Model` | `gemini-3.8-flash` | free model id (`--list-models`) |
| `OpenAICompatible:FallbackModels` | 3 Gemini Flash models | tried in order when the model is busy, out of quota or gone |
| `OpenAICompatible:FallbackAfterSeconds` | `90` | switch to a fallback if the model has not answered by then |
| `OpenAICompatible:ApiKey` | — | if empty, `GEMINI_API_KEY` is used |
| `OpenAICompatible:MaxOutputTokens` | `32000` | output limit per request |
| `OpenAICompatible:ReasoningEffort` | `low` | `minimal`/`low`/`medium`/`high`; empty — provider default (slower) |
| `Tavily:ApiKey` | — | if empty, `TAVILY_API_KEY` is used; no key → no web search |
| `Tavily:MaxResultsPerQuery` | `5` | results per search |
| `Claude:Model` | `claude-opus-5-5` | Claude model |
| `Claude:Effort` | `medium` | `low`…`max` |
| `Claude:MaxSearches` | `6` | max web searches per research step |
| `Claude:UseServerFallbacks` | `true` | if Claude refuses, the API retries on a suitable model |
| `Claude:ApiKey` | — | if empty, `ANTHROPIC_API_KEY` is used |

Via environment variables, `:` becomes `__`: `Agent__OpenAICompatible__Model=...`.

## API

`POST /api/roadmaps` — start a generation, responds `202` with an id right away:

```json
{
  "goal": "Data Engineer",
  "skills": [{ "name": "SQL", "level": 3 }, { "name": "Python", "level": 1 }],
  "background": "2 years of C# backend development",
  "hours_per_week": 10,
  "deadline_weeks": 24,
  "language": "en",
  "free_resources_only": false
}
```

`language` is `en` (English) or `uk` (Ukrainian) — the language of the roadmap texts and preferred materials.

`GET /api/roadmaps/{id}` — status (`queued` → `running` → `done` / `failed`), progress text,
warnings, and when ready — `roadmap`:

```json
{
  "goal": "Data Engineer",
  "summary": "…",
  "total_hours": 180,
  "nodes": [
    {
      "id": "sql-window-functions",
      "title": "SQL window functions",
      "description": "…",
      "status": "refresh",
      "prerequisites": ["sql-basics"],
      "estimated_hours": 6,
      "why": "…",
      "resources": [{ "title": "…", "url": "https://…", "type": "docs", "free": true }]
    }
  ],
  "sources": [{ "title": "…", "url": "https://…" }],
  "generated_at": "…"
}
```

Node `status`: `known` — already known, `refresh` — needs a refresher, `todo` — to learn.
The client polls GET every 2–3 seconds. Generation takes 1–2 minutes.
Limit: 5 generations per IP per 10 minutes.

## Where to edit

- **Prompts** — `src/Roadmap.Agent/Claude/Prompts.cs` (used by every provider). After a change, run every profile in
  `samples/profiles` and compare the results.
- **Search queries** (free setup) — `Search/TavilyResearcher.cs` → `BuildQueries`.
- **Roadmap shape** — `Models/Roadmap.cs` + `Claude/RoadmapSchema.cs` (change them together and agree with the frontend).
- **Validation rules** — `Pipeline/RoadmapValidator.cs`.

## Deploying to Azure

The app goes to App Service or Container Apps (these can be paid for with student credits).
With the free setup the whole demo costs $0 on top of hosting.

App settings (secrets as Key Vault references):
- `Agent__OpenAICompatible__ApiKey`, `Agent__Tavily__ApiKey` — free setup
- or `Agent__Provider=Claude` and `ANTHROPIC_API_KEY` — paid setup (Azure credits do not cover Claude)
- `ASPNETCORE_ENVIRONMENT=Production` (no demo page)

## MVP limitations and next steps

- Jobs and the cache live in memory and are lost on restart → PostgreSQL (roadmaps as JSONB) + Hangfire.
- The client polls for status → SignalR so progress is pushed.
- No authentication; rate limit per IP → rate limit per user.
- The research cache normalizes the goal naively (case, whitespace): "data engineer" and "DE" are
  different keys → normalize the goal with a model.
- Free setup searches in English only; Ukrainian roadmaps get English sources.
- A set of test profiles with automatic checks (evals) → run after every prompt change.
- Rebuilding a single branch of the roadmap when the user marks progress.
