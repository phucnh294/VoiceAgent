---
title: Runtime Settings — Model Picker (Ollama/Gemini), Kokoro Voice, Webhook Tools
date: 2026-09-29
type: functionality
area: voice-call
status: implementation-complete
session_id: 7e4ab707-faf5-4b23-8d13-2c483b1b1977
tags: [voice, settings, gemini, ollama, tts, kokoro, tools, webhooks, security]
keywords: [SettingsStore, ISettingsProvider, AgentSettings, SettingsView, SettingsMapper, SettingsValidator, LocalOnlyAttribute, data/settings.json, GeminiChatModel, streamGenerateContent, x-goog-api-key, thoughtSignature, parametersJsonSchema, functionResponse, systemInstruction, ChatModelResolver, IChatModelResolver, KokoroClient, /api/speech, /api/call-settings, /api/settings/models, /api/settings/voices, WebhookTool, ToolRegistry, IToolRegistry, SpeechSpeakerService, docker-compose.tts.yml, kokoro-fastapi-cpu, ghcr.io denied, Registry Access Management]
files:
  - VoiceAgent.Api/Settings/AgentSettings.cs
  - VoiceAgent.Api/Settings/SettingsStore.cs
  - VoiceAgent.Api/Settings/SettingsView.cs
  - VoiceAgent.Api/Settings/SettingsValidator.cs
  - VoiceAgent.Api/Settings/LocalOnlyAttribute.cs
  - VoiceAgent.Api/Controllers/SettingsController.cs
  - VoiceAgent.Api/Controllers/SpeechController.cs
  - VoiceAgent.Api/Services/GeminiChatModel.cs
  - VoiceAgent.Api/Services/OllamaClient.cs
  - VoiceAgent.Api/Services/ChatModelResolver.cs
  - VoiceAgent.Api/Services/KokoroClient.cs
  - VoiceAgent.Api/Tools/WebhookTool.cs
  - VoiceAgent.Api/Tools/ToolRegistry.cs
  - VoiceAgent.Api/Conversation/ConversationService.cs
  - VoiceAgent-Client/src/app/settings/settings-panel.ts
  - VoiceAgent-Client/src/app/settings/settings-api.service.ts
  - VoiceAgent-Client/src/app/voice/speech-speaker.service.ts
  - docker-compose.tts.yml
version: 1
last_updated: 2026-09-29
extraction_method: authored-from-implementation
related_docs: [rag-ai-local/functionality-docs/09292026/02_voice-call-tools-endcall-bargein.md, rag-ai-local/functionality-docs/09292026/03_voice-call-logging.md]
---

## TL;DR
- **What:** The model (Ollama or Google Gemini, with API key and model dropdown), voice (Kokoro or browser, voice and speed), system prompt, greeting, tool instructions, built-in tool toggles and custom **HTTP webhook tools** are edited at runtime in a Settings panel.
- **Why:** Switching models, voices or tools no longer needs file edits or restarts, and webhook tools let the assistant use business systems without code.
- **Where:** API `Settings/` (store, masked view, validation), `SettingsController`, `SpeechController`, `GeminiChatModel`, `WebhookTool`/`ToolRegistry`; client `src/app/settings/` and `speech-speaker.service.ts`.
- **Impact:** Settings persist in `VoiceAgent.Api/data/settings.json` (git-ignored; holds keys). Every turn uses a fresh settings snapshot. Kokoro is built from source (ghcr.io is blocked by the Docker Desktop org policy) and runs on CPU, at ~3.5–4.5s per sentence on this laptop; a GPU build is prepared but not yet built.

## Settings store and secret handling for the voice assistant

Runtime settings are one immutable `AgentSettings` record tree held by `SettingsStore`: Assistant, Llm {Ollama, Gemini}, Voice {Kokoro, Browser}, Tools {toggles, Webhooks}.
- **Saving** writes to a temp file and renames it (`File.Move(overwrite: true)`), then swaps the snapshot.
- **Reading:** `ConversationService` reads `Current` once per turn, so a save mid-turn applies from the next turn and a turn never mixes old and new settings.
- **First run:** with no file yet, it is seeded from `appsettings.json` (`Ollama`, `Assistant`). After that `appsettings.json` no longer affects these values; delete the file to re-seed.
- **Corrupt file:** the app logs an error, runs on defaults, and leaves the file untouched for the operator to fix.

Secrets (the Gemini API key, webhook header values) never go back to the browser. `SettingsMapper.ToView` sends `apiKeySet`/`apiKeyHint` ("…1234") instead. `SettingsMapper.Apply` treats a secret in a PUT as:

| Sent value | Effect |
|---|---|
| `null` | Keep the stored secret. |
| `""` | Clear it. |
| Anything else | Replace it. |

Webhooks have a stable `id`, so header secrets survive a rename.

`/api/settings` is `[LocalOnly]`: requests from any non-loopback address (with IPv4-mapped IPv6 handled) get 403, because the app has no login and this endpoint changes keys and prompts. Requests through the `ng serve` proxy come from loopback. `GET /api/call-settings` (greeting and voice choice, no secrets) is public, because the call page needs it.

*Rejected:* keeping keys in browser localStorage and sending them with each request. Keys would sit in the browser and travel on every call.

## Model providers: Ollama and Google Gemini behind IChatModel

`IChatModelResolver` creates the turn's `IChatModel` from `Llm.Provider`:
- **`OllamaClient`**: `POST {BaseUrl}/api/chat`, NDJSON stream. It uses per-request absolute URLs instead of a fixed `HttpClient.BaseAddress`, so a URL change applies immediately.
- **`GeminiChatModel`**: native REST, `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:streamGenerateContent?alt=sse`, with header `x-goog-api-key`.

Gemini mapping rules, checked against Google's `generateContent` reference on 2026-09-29:
- All `system` messages are merged in order into one `systemInstruction`, since Gemini has a single system instruction.
- `assistant` messages become role `model`; `tool` results become `functionResponse {id, name, response: {result}}` parts in a `user` turn.
- Consecutive same-role messages are merged into one turn (e.g. several tool results).
- A call opens with the assistant's greeting, but Gemini wants a user turn first, so the placeholder "(The call has connected.)" is prepended.
- Tool schemas go in `parametersJsonSchema`, which accepts full JSON Schema, so user-written webhook schemas pass. A no-argument tool omits it, because an empty `properties` object is rejected.
- **Thought signatures:** a `functionCall` part can carry `thoughtSignature`, which must be sent back unchanged in the next request. It's stored on `ChatToolCall.ThoughtSignature` (`[JsonIgnore]`, so Ollama and the call log never see it) and re-attached to the part.
- Parts with `thought: true` are never spoken. `promptFeedback.blockReason` becomes a `ChatModelException`, and HTTP errors surface Google's `error.message`.

Model lists: Ollama `GET /api/tags`; Gemini `GET /v1beta/models`, filtered to models whose `supportedGenerationMethods` contains `generateContent`. Nothing is hard-coded, because Gemini model ids change often.

**Not yet verified live:** Gemini was tested with fake HTTP handlers only (11 tests); no API key was available in this session.

## Voice: Kokoro TTS through the API, browser voice as fallback

`POST /api/speech {text, voice?, speed?}` → `KokoroClient` → Kokoro `POST {BaseUrl}/v1/audio/speech {model: "kokoro", input, voice, speed, response_format: "mp3"}`, streamed back as `audio/mpeg`. Proxying keeps the TTS server private and avoids CORS. `voice`/`speed` overrides exist for the Settings "Test voice" button.

`SpeechSpeakerService` keeps its public API (`enqueue`/`whenIdle`/`cancel`), so the call loop didn't change:
- In Kokoro mode, `enqueue()` starts that sentence's fetch immediately (prefetch), while playback stays ordered on the promise chain.
- `cancel()` aborts pending fetches, pauses audio and bumps a generation counter.
- If Kokoro fails for a sentence, that sentence is spoken with `speechSynthesis` and `warning` is set, which shows as a notice on the call screen.
- In browser mode it uses the selected `SpeechSynthesisVoice` and rate.

**Kokoro deployment gotcha:** `docker-compose.tts.yml` uses `ghcr.io/remsky/kokoro-fastapi-cpu:latest`. On this machine every ghcr.io pull fails with `error from registry: denied`, including unrelated images, even anonymously. Meanwhile an anonymous registry token and the manifest fetched with curl succeed (HTTP 200) and Docker Hub pulls work. Docker Desktop routes through `http.docker.internal:3128`, which points to an organization Registry Access Management policy. The options are an admin allowlist for `ghcr.io` or building the image from the Kokoro-FastAPI repo's Dockerfile. Bypassing the policy (e.g. with a third-party mirror) was deliberately not done.

**Resolution (2026-09-30):** `docker-compose.tts.yml` now builds from the official source cloned at `../Kokoro-FastAPI`: `docker/cpu/Dockerfile.optimized` with `INCLUDE_JAPANESE=false`. It uses only Docker Hub base images; nvcr.io is also allowed for the GPU variant.
- **CPU image:** `kokoro-fastapi-cpu:local`, 72 voices. Measured on this laptop, synthesis takes 3.5s for "Sure." and 12.4s for an 80-character sentence (Kokoro's own log). That's about 3× slower than real time, so each reply starts with a noticeable pause.
- **GPU variant:** `--profile gpu`, image `kokoro-fastapi-gpu:local`. GPU passthrough to containers works (Quadro T2000, 4GB).
- **Disk incident:** the first GPU build downloads ~6–8 GB (CUDA devel image and PyTorch cu126 wheels). That filled C: (Docker's `docker_data.vhdx` was 112 GB on C:) and crashed the Docker engine. Docker's disk image was then moved to `D:\Docker\DockerDesktopWSL`. **The GPU build still has to be rerun.**

## Webhook tools defined in the Settings UI

A webhook tool has: name, description, JSON Schema parameters, URL, headers (secrets), timeout (1–60s) and enabled. `IToolRegistry.Build(settings.Tools, callId)` creates the turn's tools: `EndCallTool`/`CurrentDateTimeTool` if enabled, plus one `WebhookTool` per enabled webhook.

Contract: `POST {url}` with body `{"tool": name, "arguments": {...}, "callId": "<guid>"}` and the configured headers.
- A 2xx body, truncated to 4,000 chars, is returned to the model as the tool result.
- A non-2xx status, timeout or unreachable URL becomes an `Error: …` result, never an exception, so the model can apologise instead of the turn failing.

The existing tool loop and call log (`tool_call` records) handle webhooks unchanged. The webhook `HttpClient` has an infinite timeout; each tool enforces its own with a linked `CancellationTokenSource`.

Validation (`SettingsValidator`):
- the name matches `^[a-zA-Z_][a-zA-Z0-9_]{0,63}$`, is unique case-insensitively, and isn't a built-in name;
- the description is required;
- the URL is absolute http/https;
- the schema must have `"type": "object"`;
- header names are HTTP tokens.

Errors come back as field paths (`tools.webhooks[0].url`) shown inline in the panel.

**Security note:** webhook URLs are unrestricted http(s), so the API will POST to internal addresses. That's acceptable because only a local operator can edit settings (`[LocalOnly]`). Add an allowlist before exposing settings to anyone else.

## Settings panel (client)

`SettingsPanel` (`src/app/settings/`) opens from the gear button as an overlay on the phone screen. It uses template-driven forms (`FormsModule`) on a draft `SettingsView` held in a signal. ngModel mutates the draft in place, so anything derived from it (like the active-tools hint) is a plain method, not a `computed`.
- The Gemini key field is a password input; blank keeps the saved key, and a checkbox removes it.
- Webhook header values: blank on a saved header means keep.
- Parameter JSON is edited as text, validated on blur and before save.
- After a save, the panel calls `VoiceCallService.refreshCallSettings()`, so the voice changes from the next sentence even mid-call.
- Form control styles live in `styles.css`, scoped under `app-settings-panel`, to keep component CSS under the 4kB budget.

## Verification of runtime settings, Gemini, Kokoro and webhooks

- `dotnet test VoiceAgent.Api.Tests`: **72 passed**. Coverage: store seed/save/corrupt file, mapper secret semantics, validator, local-only filter, Gemini request mapping/SSE/errors/model list, webhook success/non-2xx/timeout/truncation/unreachable, registry, plus the earlier conversation and log tests.
- Client specs, run under Node with an esbuild shim (Vitest can't start here): 12 passed, including Kokoro ordering with out-of-order synthesis, cancel, and fallback. `npx ng build` is clean and within budgets.
- Live on 2026-09-29:
  - The settings file was seeded and git-ignored, and the Ollama model list worked.
  - An invalid PUT returned 400 with 5 field errors.
  - A header secret was masked as `…4321`, kept on a re-save that sent `null`, and stored only on disk.
  - Kokoro endpoints returned a clear 502 while the container was down.
  - The **webhook tool worked end to end with qwen2.5:0.5b**: 3 of 3 order questions called `lookup_order` with the right `orderId`. The webhook received `Authorization: Bearer …` and the `callId`, and the assistant spoke the result.
- Kokoro (CPU build) verified on 2026-09-30: `/api/speech` returned valid MP3 (ID3) for the saved voice and for preview overrides (`am_michael` at 1.3×), in ~4.5s per sentence.
- **Still to verify** (needs the user): a Gemini turn with a real key (tool calls and thought signatures live), the GPU Kokoro build and its latency, and the Settings panel UI in the browser.
