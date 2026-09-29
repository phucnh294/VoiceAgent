# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project purpose

VoiceAgent is a browser-based voice assistant for a **virtual representative**: the user speaks, the
browser does speech-to-text (STT), the text is sent to an LLM, and the streamed answer is read
back with text-to-speech (TTS) in the browser. STT and TTS run client-side (Web Speech API); the
backend only brokers the LLM call.

## Layout

`VoiceAgent.slnx` (Visual Studio solution, new XML format) ties together:

- `VoiceAgent.Api/` — ASP.NET Core Web API on **.NET 10** (`net10.0`). The only project with real code.
- `VoiceAgent.Application/`, `VoiceAgent.Domain/`, `VoiceAgent.Infrastructure/` — empty Clean
  Architecture placeholders (`Class1.cs` only). The Api does **not** reference them yet. When moving
  logic out of the Api, the intended direction is Api → Application → Domain, with Infrastructure
  (e.g. the Ollama client) implementing Application abstractions.
- `VoiceAgent-Client/` — Angular 21 front end (NgModule-based, `standalone: false` schematics),
  wrapped in a `.esproj` so Visual Studio can launch it.
- The solution has an empty `/test/` folder — there are no .NET test projects yet.
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

The backend needs an **Ollama** server. `VoiceAgent.Api/appsettings.json` sets `Ollama:BaseUrl`
(currently `http://localhost:8001`) and `Ollama:Model` (`qwen2.5:0.5b-instruct`); the model must be
pulled on that server. The first turn after Ollama starts is slow (~15s model load); warm turns
start streaming in under a second. `Assistant:SystemPrompt` holds the representative's persona.

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

1. `VoiceCallService` runs the loop: speak greeting → **listening** → **thinking** → **speaking** →
   listening … until `hangUp()`. The mic is off while the assistant speaks, so it never hears
   itself; `interrupt()` cuts a reply short and returns to listening.
2. `SpeechRecognizerService` wraps Web Speech recognition (Chrome/Edge desktop only) with
   `continuous = false`: the browser ends the session when the caller pauses, which is the
   turn-taking signal. Silence (`no-speech`) just re-listens; only permission/device/network errors
   end the call.
3. `ConversationApiService` `fetch`es `POST /api/conversation/stream` with the whole call history
   `{ messages: [{ role: 'user'|'assistant', content }] }` — the API is stateless, the client owns
   history. `fetch` is used instead of `HttpClient` to read the body incrementally.
4. `ConversationController` rejects any role other than user/assistant (400), prepends
   `Assistant:SystemPrompt`, trims to `MaxHistoryMessages`, and writes each token as plain UTF-8
   text, flushing per token. Ollama failures before the first byte → 502; after it the stream just
   ends.
5. `OllamaClient` (typed `HttpClient`) calls Ollama `POST /api/chat` with `stream: true` and parses
   its **NDJSON** (one JSON object per line, `message.content` fragment, `done: true` at the end).
6. Back in the client, `SentenceBuffer` turns fragments into whole sentences so
   `SpeechSpeakerService` starts talking on the first sentence while the rest streams in. The
   speaker queues sentences on its own promise chain (so `cancel()` drops everything) and keeps a
   reference to the live utterance (Chrome otherwise GCs it and never fires `onend`).

Tunables: greeting, error reply, language and history size are in `src/app/voice/voice.config.ts`;
persona and server-side history cap in the API's `Assistant` config section.

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
