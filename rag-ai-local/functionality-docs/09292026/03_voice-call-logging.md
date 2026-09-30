---
title: Voice Call Logging — Prompts, Responses and Tool Calls per Call
date: 2026-09-29
type: functionality
area: voice-call
status: implementation-complete
session_id: 7e4ab707-faf5-4b23-8d13-2c483b1b1977
tags: [voice, logging, audit, verification, aspnetcore]
keywords: [ICallLog, JsonlCallLog, NullCallLog, CallLogRecord, model_request, model_response, tool_call, turn, call_end, TurnStatus, X-Call-Id, callId, CallLog:Enabled, CallLog:Directory, reportCallEnd, keepalive, pagehide, jsonl]
files:
  - VoiceAgent.Api/CallLogging/CallLog.cs
  - VoiceAgent.Api/CallLogging/CallLogRecords.cs
  - VoiceAgent.Api/Conversation/ConversationService.cs
  - VoiceAgent.Api/Controllers/ConversationController.cs
  - VoiceAgent.Api/Program.cs
  - VoiceAgent.Api.Tests/CallLogging/JsonlCallLogTests.cs
  - VoiceAgent.Api.Tests/Conversation/ConversationServiceTests.cs
  - VoiceAgent-Client/src/app/voice/conversation-api.service.ts
  - VoiceAgent-Client/src/app/voice/voice-call.service.ts
version: 1
last_updated: 2026-09-29
extraction_method: authored-from-implementation
related_docs: [rag-ai-local/functionality-docs/09292026/02_voice-call-tools-endcall-bargein.md, rag-ai-local/functionality-docs/09292026/01_voice-call-architecture.md]
---

## TL;DR
- **What:** Every voice call writes a JSON Lines log: each exact model request (system prompt included), each model response, each tool call, a per-turn summary, and the browser's end-of-call report with the full transcript.
- **Why:** To verify what the model was actually given and what it answered, e.g. whether a wrong answer came from the prompt, the model, or a tool.
- **Where:** API `CallLogging/` (`ICallLog`, `JsonlCallLog`), written from `ConversationService`; the client sends `callId` and reports the call end.
- **Impact:** One file per call at `VoiceAgent.Api/logs/calls/<yyyy-MM-dd>/<callId>.jsonl` (git-ignored). Turned on by default and switched off with `CallLog:Enabled=false`.

## What the voice call log records

The voice call log has one JSON object per line. Each has `type`, `timestamp` (server local time) and `callId`:

| `type` | Written by | Purpose |
|---|---|---|
| `model_request` | `ConversationService`, before each model call | The exact `messages` list sent to the model: `SystemPrompt`, `ToolInstructions`, trimmed history, and `FarewellInstruction` when present. Also `model`, the tool names, `turn` (count of caller messages) and `round` (tool-loop iteration). |
| `model_response` | `ConversationService`, after each model call | `text`, `toolCalls`, `status`, `durationMs`. Written with the partial text if the turn is cut short. |
| `tool_call` | `ConversationService` | `tool`, `runsOn` (`server` or `browser`), `arguments`, `result` (server tools only), `durationMs`. |
| `turn` | `ConversationService`, in the iterator's `finally` | `callerSaid`, `assistantReplied` (all text streamed to the browser), `actions` with `source` (`model` or `farewell_detector`), `modelRounds`, `status`, `durationMs`. |
| `call_end` | `POST /api/conversation/{callId}/end` from the browser | `reason` (`caller`, `goodbye`, `idle`, `error`, `closed`), `durationSeconds`, and the browser's `transcript` including `interrupted` and `typed` flags. |

`status` is `completed`, `cancelled` (the browser aborted the request because the caller interrupted or hung up) or `failed` (the model threw; the exception is in the API console log, not the call log).

## Why the voice call log needs the browser's call_end report

The API only sees what passes through `/api/conversation/stream`. The greeting and the idle goodbye are spoken by the browser, and the idle and hang-up decisions are made there too. So on every hang-up, `VoiceCallService.hangUp()` sends `reportCallEnd()` with the reason and the transcript as the caller saw it. It uses `fetch` with `keepalive: true`, so a report sent while the tab closes still goes out; the `pagehide` listener hangs up with reason `closed`. The report is fire-and-forget, so a failure never disturbs the call.

*Rejected:* storing the call server-side to build the transcript there. The API is deliberately stateless (the client owns the history), and the browser already has the exact transcript.

## How the voice call ID works

The browser generates the call ID with `crypto.randomUUID()` when Call is pressed and sends it as `callId` on every `/stream` request and in the `/end` URL. The API binds it as `Guid?`. A malformed value is rejected with 400 by model binding, so it can never be used for path traversal in the file name. If it's missing (e.g. manual curl tests), the API issues a new ID per request. The ID used is echoed in the `X-Call-Id` response header.

`JsonlCallLog` fixes the date folder at a call's first record (cached per call ID and released on `call_end`), so a call that crosses midnight stays in one file.

## Design decisions for the voice call log

- **Explicit logging in `ConversationService`, not a decorator around `IChatModel`.** The service knows the call ID, turn number, tool results and which actions came from the farewell backstop; a model decorator would need all of that passed in through ambient context.
- **The turn summary is written in `finally`.** Async iterators can't `catch` around `yield`, but `finally` (with `await`) still runs when the browser aborts or the model throws. That's how cancelled turns get logged with their partial output. Log writes don't use the request's cancellation token, so they still complete after an abort.
- **Logging never breaks a call.** `JsonlCallLog` catches `IOException` and `UnauthorizedAccessException`, reports them through `ILogger`, and carries on.
- **One global write lock.** Turns of one call are sequential, but the `call_end` report can race the last turn's summary. At this app's volume a single `SemaphoreSlim` is simpler than per-file locks.
- **JSON Lines, not a database.** Files are appendable, greppable, `jq`-friendly, and one file per call is easy to hand over when checking a specific call.

## Gotchas in the voice call log

- **Logs contain everything callers say.** They're personal data: `logs/` is in `.gitignore`, and old date folders should be deleted when no longer needed. Nothing is redacted.
- **A cancelled turn before the first token has empty text.** If the caller interrupts while the model is still loading, `model_response.text` is `""`; that's expected, not a logging bug.
- **Enum casing.** `status` is written lowercase by the `JsonStringEnumConverter(SnakeCaseLower)` on `JsonlCallLog`'s serializer options. Don't add a `[JsonConverter]` attribute on `TurnStatus`: an attribute overrides the options converter and brings back `"Completed"`.
- **The log directory is relative to the API content root** (`VoiceAgent.Api/`), not to where the process was started.

## Verification of the voice call log

Unit tests (`dotnet test VoiceAgent.Api.Tests`, 30 passing) cover:
- the full prompt including the system prompt, with every record under one call ID;
- the record order request → response → tool_call → request → response → turn for a server tool;
- the farewell backstop action source;
- a cancelled turn logging its partial text;
- the JSONL file layout and lowercase status.

End to end, run on 2026-09-29 against Ollama: a simulated two-turn call produced 7 lines:
- 2 × `model_request` with both system messages and the farewell instruction on turn 2;
- 2 × `model_response`;
- 2 × `turn`, the second with `end_call(farewell_detector)`;
- 1 × `call_end` with 5 transcript entries.

A request aborted by the client after 1.2s produced `model_response` and `turn` with status `cancelled`.

To check a real call: make a call in the browser, hang up, then open the newest file under `VoiceAgent.Api/logs/calls/<today>/`.
