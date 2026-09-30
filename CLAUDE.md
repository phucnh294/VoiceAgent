# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

VoiceAgent is a browser-based voice assistant for a **virtual representative**: the user speaks, the
browser does speech-to-text (Web Speech API), the text goes to an LLM (Ollama or Google Gemini) that
can call tools, and the streamed answer is spoken with Kokoro TTS (proxied by the API) or the
browser's `speechSynthesis` as fallback. Model, voice, prompts and tools are runtime settings
edited in the UI.

## Layout

`VoiceAgent.slnx` (Visual Studio solution, new XML format) ties together:

- `VoiceAgent.Api/` — ASP.NET Core Web API on **.NET 10** (`net10.0`): `Controllers/`,
  `Settings/` (runtime settings store), `Conversation/` (reply + tool loop), `Tools/`,
  `Services/` (Ollama/Gemini chat models, Kokoro client), `CallLogging/`.
- `docker-compose.tts.yml` — Kokoro TTS container on :8880.
- `VoiceAgent.Application/`, `VoiceAgent.Domain/`, `VoiceAgent.Infrastructure/` — empty Clean
  Architecture placeholders (`Class1.cs` only). The Api does **not** reference them yet. When moving
  logic out of the Api, the intended direction is Api → Application → Domain, with Infrastructure
  (e.g. the Ollama client) implementing Application abstractions.
- `VoiceAgent-Client/` — Angular 21 front end (NgModule-based, `standalone: false` schematics),
  wrapped in a `.esproj` so Visual Studio can launch it.
- `VoiceAgent.Api.Tests/` — xUnit tests for the API (solution folder `/test/`).
- Root `package.json` is a placeholder solution item (`{ "private": true }`), not a real npm project.

## Commands

Starting either side starts both:

```bash
# From repo root: API on :5043 + SpaProxy auto-launches Angular (`npm run serve`) on :53034
dotnet run --project VoiceAgent.Api                          # "http" profile
dotnet run --project VoiceAgent.Api --launch-profile https   # https://localhost:7007 + 5043

# From VoiceAgent-Client/: concurrently runs the API ("api-only" profile) + ng serve
npm start
```

- `http`/`https` launch profiles set `ASPNETCORE_HOSTINGSTARTUPASSEMBLIES=Microsoft.AspNetCore.SpaProxy`;
  SpaProxy reads `SpaRoot`/`SpaProxyLaunchCommand`/`SpaProxyServerUrl` from `VoiceAgent.Api.csproj`,
  reuses an already-running dev server on :53034, and otherwise opens it in a new console window.
- The `api-only` profile has no SpaProxy — `npm start` uses it so the two launchers don't both
  spawn `ng serve`. `npm run serve` = Angular only; `npm run serve:api` = API only.
- `ng serve` proxies `/api/*` to `http://localhost:5043` (`VoiceAgent-Client/proxy.conf.json`), so
  client code should call relative `/api/...` URLs rather than a hard-coded backend origin.
- The root `package.json` must stay valid JSON: Angular's esbuild reads it, and an empty file
  breaks the client build with "Unexpected end of file in JSON".

```bash
dotnet build VoiceAgent.slnx
```

**Runtime settings** (model provider + model + Gemini key, voice, system prompt, greeting, tool
instructions, built-in tool toggles, webhook tools) live in `VoiceAgent.Api/data/settings.json`
(git-ignored; holds secrets), edited through the Settings panel (`/api/settings`, `[LocalOnly]`).
`SettingsStore` seeds that file on first run from the `Ollama`/`Assistant` sections of
`appsettings.json`; after that those sections are ignored — change settings via the panel/API or
delete `data/settings.json` to re-seed. Current setup: Ollama at `http://localhost:8001`
(`qwen2.5:0.5b-instruct`, in Docker container `rag-llm-model`); the first turn after Ollama starts
is slow (~15s model load).

**Kokoro TTS**: Docker Desktop's organization policy on this machine denies every `ghcr.io` pull
(Docker Hub and nvcr.io work), so `docker-compose.tts.yml` builds Kokoro from the official source
cloned at `../Kokoro-FastAPI` (`kokoro-fastapi-cpu:local`, container `kokoro-tts`, port 8880). Don't
try to bypass the policy. The CPU build is slow on this laptop (~3.5–4.5s per sentence); a GPU
variant is available as `--profile gpu` (Quadro T2000 passthrough works) but its first build
downloads ~6–8 GB — that build once filled C: and crashed Docker, after which Docker's disk image
was moved to `D:\Docker\DockerDesktopWSL`. If Kokoro is down, the app falls back to the browser voice
per sentence and shows a notice.

Frontend (from `VoiceAgent-Client/`):

```bash
npm install
npm run serve                  # Angular only, http://127.0.0.1:53034 (port set in angular.json, not 4200)
npm run build                  # production build -> dist/VoiceAgent-Client/browser/
npx ng test --watch=false      # unit tests (Vitest via @angular/build:unit-test)
npx ng test --watch=false --include src/app/voice/sentence-buffer.spec.ts   # single spec file
npx prettier --write src       # formatting (printWidth 100, single quotes)
```

Specs use Vitest globals (`tsconfig.spec.json`); `karma.conf.js` is an unused leftover. Vitest's
forked workers failed to start inside the Claude Code sandbox ("Timeout waiting for worker"), so
tests may need to be run from a normal terminal.

## The voice call (end to end)

The UI is a phone-call metaphor: press Call, talk hands-free, Hang up. All logic lives in
`VoiceAgent-Client/src/app/voice/`; `App` only renders `VoiceCallService` signals (the app is
zoneless, so UI state must be signals).

1. `VoiceCallService` runs the loop: greeting → **listening** → **thinking** → **speaking** →
   listening … until the call ends. Every wait for the caller goes through `listenOnce()`, which
   races one recognition session against typed text (`sendText()`), the idle deadline and stop
   signals, so every kind of caller input is handled the same way.
2. **Barge-in**: during an assistant turn, `watchForCaller()` keeps listening (when
   `voiceInterrupt` is on) and accepts typed text. Heard speech that mostly repeats the reply's
   words is treated as the assistant's own voice (`echo-filter.ts`). Real speech or typed text cuts
   the turn off (aborts the fetch, cancels speech), marks the partial reply `interrupted`, and
   becomes the next user turn. The Interrupt button cuts off and returns to normal listening.
3. **Ending the call**: an `end_call` action from the API → speak the farewell (the tool's
   `farewell` argument or `CALL_FAREWELL` if the model said nothing) → `hangUp('goodbye')`.
   **Idle**: no caller speech or typing for `IDLE_TIMEOUT_SECONDS` (60) while listening → spoken
   `IDLE_GOODBYE` → `hangUp('idle')`, with a countdown shown for the last 15s.
4. `SpeechRecognizerService` wraps Web Speech recognition (Chrome/Edge desktop only) with
   `continuous = false`: the browser ends the session when the caller pauses, which is the
   turn-taking signal. Only one session may run at a time, so `listen()` aborts and awaits the
   previous one first, and takes a cancel signal so an unwanted session never starts. Silence
   (`no-speech`) just re-listens; only permission/device/network errors end the call.
5. `ConversationApiService` `fetch`es `POST /api/conversation/stream` with the whole history
   `{ messages: [{ role: 'user'|'assistant', content }] }` (the API is stateless) and parses the
   **NDJSON** reply: `{"type":"text","text"}`, `{"type":"action","name":"end_call","arguments"}`,
   `{"type":"error","message"}`.
6. API: `ConversationController` rejects any role other than user/assistant (400) and writes
   `ConversationService` events as NDJSON (failure before the first event → 502; after it → an
   `error` event). `ConversationService` builds the prompt — `SystemPrompt`, then
   `ToolInstructions` as a second system message, then the last `MaxHistoryMessages` turns, then
   `FarewellInstruction` when the caller is leaving — and runs the **tool loop** (up to
   `MaxToolRounds`): `IServerTool` results are fed back to the model; `IClientTool` calls are
   forwarded to the browser as `action` events. Each turn takes one `ISettingsProvider.Current`
   snapshot and gets its model from `IChatModelResolver` and its tools from `IToolRegistry`
   (built-ins if enabled + one `WebhookTool` per enabled webhook), so settings saves apply from the
   next turn. `WebhookTool` POSTs `{tool, arguments, callId}` with the configured headers and
   per-tool timeout, returns the body (≤ 4,000 chars) to the model, and turns failures into
   `Error: …` results instead of throwing.
7. `FarewellDetector` is a deterministic backstop: small models answer "Bye!" without calling
   `end_call`, so a short (≤ 12 words), non-negated caller utterance matching the configured
   goodbye phrases also emits `end_call` (only when `end_call` is enabled).
8. Chat models (`IChatModel`, created per turn): `OllamaClient` → Ollama `POST /api/chat` (NDJSON,
   `message.tool_calls`); `GeminiChatModel` → `streamGenerateContent?alt=sse` with
   `x-goog-api-key`. The Gemini mapping merges all system messages into `systemInstruction`, maps
   tool results to `functionResponse` parts, merges consecutive same-role turns, prepends a user
   placeholder because calls open with the assistant's greeting, sends schemas as
   `parametersJsonSchema` (omitted for no-arg tools), and round-trips each function call's
   `thoughtSignature` (`ChatToolCall.ThoughtSignature`, `[JsonIgnore]` so Ollama never sees it).
9. `SentenceBuffer` turns fragments into whole sentences for `SpeechSpeakerService`. With Kokoro it
   requests each sentence's MP3 from `POST /api/speech` as soon as it's queued (prefetch) but plays
   in order on its promise chain; if Kokoro fails it speaks that sentence with `speechSynthesis`
   and sets `warning`. `cancel()` aborts pending fetches, stops audio, and bumps a generation
   counter. `VoiceCallService.refreshCallSettings()` (`GET /api/call-settings`: greeting + voice,
   no secrets) runs at call start and after the Settings panel saves.
10. **Settings panel** (`src/app/settings/`, gear button): template-driven forms (`FormsModule`) on
   a draft object held in a signal. Secrets are write-only: the API returns `apiKeySet`/`apiKeyHint`
   (and `valueSet`/`valueHint` for webhook headers) and on PUT treats `null` = keep, `""` = clear,
   anything else = replace; webhooks carry a stable `id` so header secrets survive renames.
   `SettingsValidator` returns field paths (`tools.webhooks[0].name`) shown inline.

11. **Call log**: `ConversationService` writes every model request (full `messages`, system prompt
    included), model response, tool call and a per-turn summary to `ICallLog`
    (`JsonlCallLog` → `VoiceAgent.Api/logs/calls/<date>/<callId>.jsonl`, git-ignored). The browser
    creates the call ID (`crypto.randomUUID()`), sends it as `callId` on every turn, and on hang-up
    POSTs `/api/conversation/{callId}/end` with the reason and its own transcript (greeting and
    idle goodbye never pass through the API). A turn cut short by the browser is logged in the
    iterator's `finally` with status `cancelled` and the partial text. Disable with
    `CallLog:Enabled=false` (swaps in `NullCallLog`).

`qwen2.5:0.5b-instruct` is unreliable with built-in tools: it never called `end_call` in testing
(the backstop covers it) and called `get_current_datetime` only occasionally, inventing the time
otherwise. It did call a well-described webhook tool (`lookup_order`) correctly 3/3 times.

Tunables: `src/app/voice/voice.config.ts` (language, idle timeout, echo threshold, fallback
greeting/farewells, history size); everything else is in the Settings panel.

API tests: `dotnet test VoiceAgent.Api.Tests`; single class:
`dotnet test VoiceAgent.Api.Tests --filter "FullyQualifiedName~GeminiChatModelTests"`.
`ConversationService` is tested with fake `IChatModelResolver`/`IToolRegistry`/`ISettingsProvider`;
Gemini, webhook and Kokoro HTTP are tested with fake `HttpMessageHandler`s, never the network.

Client specs can't run under Vitest inside the Claude Code sandbox (workers time out). They were
verified by bundling them with esbuild and running them under Node with a small
describe/it/expect shim; `speech-speaker.service.spec.ts` fakes `fetch`, `Audio` and
`speechSynthesis`.

When writing Markdown or code containing backticks through Bash, never put it inside a
double-quoted `node -e "…"` string: bash runs the backticks as command substitution. Use the
Edit/Write tools instead.

CORS is wide open (`AllowAnyOrigin`) in `Program.cs`, though the dev proxy makes it unnecessary.
`WeatherForecastController` / `WeatherForecast.cs` / `VoiceAgent.Api.http` are template leftovers.

## Documentation conventions (from `.claude/rules/file-organization.md`)

- Knowledge Markdown goes only under `rag-ai-local/QandA/` (`MMDDYYYY_<Topic>.md`) or
  `rag-ai-local/functionality-docs/MMDDYYYY/NN_<slug>.md`, each starting with the YAML frontmatter +
  TL;DR block defined in that rule. Do not create knowledge `.md` files elsewhere.
- Use the project skills to write them: `write-qanda-doc`, `write-functionality-doc`,
  `write-handoff-doc`, `write-business-rule-doc`. Sessions should end with a handoff doc.
- `rag-ai-local/` (including the `template/` folder and `_METADATA_SCHEMA.md` the rule references)
  and `must-read.md` do not exist yet — create them as needed when the first doc is written.
