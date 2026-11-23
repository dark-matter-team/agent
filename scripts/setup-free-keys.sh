#!/usr/bin/env bash
# Saves the free-setup keys into .NET user secrets (shared by the API and the CLI).
# Keys are typed without echo and are stored outside the repository, so they never end up in git.
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="$(command -v dotnet || echo "$HOME/.dotnet/dotnet")"
PROJECT=src/Roadmap.Api

save() {
  "$DOTNET" user-secrets set "$1" "$2" --project "$PROJECT" >/dev/null
}

echo "Free setup: keys are hidden while you type or paste them. Press Enter to skip one."
echo

read -r -s -p "Gemini API key (https://aistudio.google.com/apikey): " gemini
echo
if [[ -n "$gemini" ]]; then
  save "Agent:OpenAICompatible:ApiKey" "$gemini" && echo "  Gemini key saved."
else
  echo "  Gemini key skipped."
fi

read -r -s -p "Tavily API key, optional (https://app.tavily.com): " tavily
echo
if [[ -n "$tavily" ]]; then
  save "Agent:Tavily:ApiKey" "$tavily" && echo "  Tavily key saved."
else
  echo "  Tavily key skipped: roadmaps will be built without web search."
fi

echo
echo "Saved settings (values hidden):"
"$DOTNET" user-secrets list --project "$PROJECT" | sed -E 's/= .+$/= ***/'
