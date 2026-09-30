---
title: Voice Call — Tool Calling, End Call, Idle Timeout and Barge-in
date: 2026-09-29
type: functionality
area: voice-call
status: implementation-complete
session_id: 7e4ab707-faf5-4b23-8d13-2c483b1b1977
tags: [voice, tools, tool-calling, barge-in, ollama, angular, aspnetcore]
keywords: [end_call, get_current_datetime, IServerTool, IClientTool, IAssistantTool, IChatModel, ConversationService, FarewellDetector, EndCallPhrases, ToolInstructions, FarewellInstruction, MaxToolRounds, application/x-ndjson, ActionEvent, isLikelyEcho, ECHO_WORD_OVERLAP, IDLE_TIMEOUT_SECONDS, listenOnce, watchForCaller, voiceInterrupt, tool_calls]
files:
  - VoiceAgent.Api/Conversation/ConversationService.cs
  - VoiceAgent.Api/Conversation/FarewellDetector.cs
  - VoiceAgent.Api/Conversation/ConversationEvent.cs
  - VoiceAgent.Api/Tools/AssistantTools.cs
  - VoiceAgent.Api/Tools/EndCallTool.cs
  - VoiceAgent.Api/Tools/CurrentDateTimeTool.cs
  - VoiceAgent.Api/Services/IChatModel.cs
  - VoiceAgent.Api/Services/OllamaClient.cs
  - VoiceAgent.Api/Controllers/ConversationController.cs
  - VoiceAgent.Api/appsettings.json
  - VoiceAgent.Api.Tests/Conversation/ConversationServiceTests.cs
  - VoiceAgent.Api.Tests/Conversation/FarewellDetectorTests.cs
  - VoiceAgent-Client/src/app/voice/voice-call.service.ts
  - VoiceAgent-Client/src/app/voice/echo-filter.ts
  - VoiceAgent-Client/src/app/voice/speech-recognizer.service.ts
  - VoiceAgent-Client/src/app/voice/conversation-api.service.ts
  - VoiceAgent-Client/src/app/voice/voice.config.ts
version: 1
last_updated: 2026-09-29
extraction_method: authored-from-implementation
related_docs: [rag-ai-local/functionality-docs/09292026/01_voice-call-architecture.md]
---

## TL;DR
- **What:** The voice call assistant gained LLM tool calling (`end_call` in the browser, `get_current_datetime` on the server), a server-side goodbye detector, a 60-second idle hang-up, and caller barge-in by voice or typed message.
- **Why:** A call must end naturally when the caller says goodbye or goes silent, and the caller must be able to cut in mid-answer like on a real phone call.
- **Where:** API `Conversation/` and `Tools/` (tool loop, NDJSON events); client `voice-call.service.ts` (call loop), `echo-filter.ts`, `speech-recognizer.service.ts`.
- **Impact:** `/api/conversation/stream` now returns NDJSON events instead of plain text. Goodbyes end the call even with the 0.5B model, which never calls `end_call` itself.

## Tool calling architecture in the voice call API

The voice call API's tool calling works through two interfaces in `VoiceAgent.Api/Tools/AssistantTools.cs`, both registered in DI as `IAssistantTool`:

- **`IServerTool`** runs inside the API. `ConversationService` executes it, appends the result as a `tool` role message, and calls the model again so it can use the result. Example: `get_current_datetime` (`CurrentDateTimeTool`, uses `TimeProvider`).
- **`IClientTool`** isn't executed by the API. The call is forwarded to the browser as an `action` event, because only the browser can perform it. Example: `end_call` (`EndCallTool`, optional `farewell` argument).

`ConversationService.StreamReplyAsync` runs the loop: model → any server tools → model … up to `Assistant:MaxToolRounds` (3). A round with only client tool calls ends the reply. An unknown tool name or a throwing tool becomes an `Error: …` tool result, so the model can apologise instead of the turn failing. `OllamaClient` (behind `IChatModel`) sends the tool definitions to Ollama `/api/chat`, and tool calls arrive as `message.tool_calls` inside the NDJSON stream.

To add a tool: implement `IServerTool` or `IClientTool`, register it with `AddSingleton<IAssistantTool, YourTool>()` in `Program.cs`, and describe when to use it in `Assistant:ToolInstructions`. A client tool also needs a handler in `voice-call.service.ts` (`assistantTurn` currently handles only `end_call`).

## Voice call API response protocol (NDJSON events)

`POST /api/conversation/stream` returns `application/x-ndjson`, one JSON event per line (`ConversationEvent.cs`, polymorphic on `type`):

```
{"type":"text","text":"Goodbye! "}
{"type":"action","name":"end_call","arguments":{"farewell":"Goodbye!"}}
{"type":"error","message":"The assistant stopped responding."}
```

A model failure before the first event returns HTTP 502 with a plain-text message. A failure after streaming started becomes an `error` event, because the status code is already sent. *Rejected:* keeping the plain-text stream and adding in-band markers, which would be fragile and could be spoken aloud.

## Ending the voice call: end_call tool plus FarewellDetector backstop

The voice call ends on a goodbye through two independent paths:

1. **Model path**: the model calls `end_call` → the API forwards an `action` event → the browser speaks the reply text, or the tool's `farewell` argument, or `CALL_FAREWELL`, then hangs up with reason `goodbye`.
2. **Deterministic path**: `FarewellDetector` checks the caller's last message against `Assistant:EndCallPhrases`. If it matches and the model didn't call `end_call`, `ConversationService` emits the `end_call` action itself.

The farewell is also detected **before** the model is called, and `Assistant:FarewellInstruction` ("reply with one short goodbye only") is added as the last system message. Without it, the 0.5B model answered "I want to quit now" with "…Is there anything else I can assist you with today?" and then the call hung up.

FarewellDetector rules and why:
- Only utterances of **12 words or fewer** count, because farewells are short. "My wife said I should quit my old plan and move to your company" is a real request.
- A phrase preceded within 3 words by a negation (not, don't, never, won't, can't…) doesn't count: "Don't hang up".
- "no" is deliberately **not** a negation: "No, that's all, bye" is a farewell.
- An empty `EndCallPhrases` uses the built-in English list; a configured list **replaces** it. Update the list when `CALL_LANGUAGE` changes.

*Rejected:* relying on the model alone. Measured on 2026-09-29, `qwen2.5:0.5b-instruct` called `end_call` in **0** tests across several prompt layouts; it just says "Bye!".

## Idle timeout of the voice call

The voice call hangs up after `IDLE_TIMEOUT_SECONDS` (60) without caller activity. Activity means speech (the first non-echo interim result), a typed message, or the end of an assistant turn. The idle deadline is `lastCallerActivity + 60s` and survives across recognition sessions: Chrome ends a silent session after about 8 seconds (`no-speech`), and the loop keeps listening against the same deadline. The last `IDLE_WARNING_SECONDS` (15) show "No response — ending in Ns". At the deadline the assistant speaks `IDLE_GOODBYE` and hangs up with reason `idle`. The deadline only runs while listening, so a long assistant answer never counts as caller silence.

## Barge-in: interrupting the voice assistant by voice or typed message

While the voice assistant thinks or speaks, `watchForCaller()` waits for the caller in parallel:

- **By voice** (when `voiceInterrupt` is on, the default): recognition sessions keep running during the assistant turn. The first interim result that isn't echo cuts the turn off: it aborts the reply `fetch`, cancels speech, and sets state to `listening`. The caller's final transcript becomes the next user turn.
- **By typing**: `sendText()` delivers to whoever is currently waiting for the caller (a listening phase or a barge-in watcher), or queues if no one is waiting.
- **Interrupt button**: cuts off without any caller input and returns to normal listening.

The partial assistant reply stays in the transcript and history, marked `interrupted`, so the model knows what it already said.

All caller input goes through one primitive, `listenOnce()`. It races one recognition session, typed text, an optional idle deadline and stop signals (call hang-up, turn finished), and returns a `CallerInput` (`text`, `silence`, `idle`, `stopped` or `error`). *Rejected:* separate code paths for speech, typing and timeouts, which produced race conditions between them.

## Echo filtering: why voice barge-in doesn't hear the assistant itself

The voice assistant speaks through `speechSynthesis`, which isn't routed through WebRTC echo cancellation, so on speakers the microphone hears the assistant. `isLikelyEcho(heard, replyText)` in `echo-filter.ts` treats heard speech as echo when at least `ECHO_WORD_OVERLAP` (0.6) of its words appear in the reply being spoken. The caller's real words ("wait, what about Saturday", "stop") share few words with the reply, so they interrupt.

Limits: this is a heuristic. On loud speakers, or when the caller repeats the assistant's words, it can misfire. Headphones avoid the problem entirely; the "Interrupt by voice" toggle turns barge-in off. *Rejected for now:* `getUserMedia` with `echoCancellation` plus our own voice-activity detection. Web Speech recognition doesn't accept a custom audio stream, so that needs server-side streaming STT.

## Gotchas in the voice call tool and barge-in implementation

- **Only one recognition session at a time.** Barge-in restarts sessions back to back, so `SpeechRecognizerService.listen()` aborts the active session and awaits its `onend` before starting. It also takes a cancel signal, so a session requested and then cancelled while waiting never starts, and it backs off 300ms if `start()` throws, preventing a hot loop.
- **Tool rules buried in a long persona prompt are ignored by small models.** With the full persona, the 0.5B model called `get_current_datetime` on 0 of 4 time questions. With the rules as a separate system message (`ToolInstructions`), it was 2 of 4. It still invents times ("10:30 AM", "Monday") when it skips the tool, and only a larger model fixes that.
- **Barge-in on speakers with a very chatty reply** may still self-interrupt when echo transcription is poor; see the echo filtering limits above.
- **The echo check compares against the generated text, not the spoken text.** Generation runs ahead of speech, so the comparison set is a superset of what has been said — the safe direction.

## Verification of tool calling, end call, idle and barge-in

API unit tests (xUnit, scripted `IChatModel` fake, no Ollama needed):

```bash
dotnet test VoiceAgent.Api.Tests
# Passed! - Failed: 0, Passed: 23
```

They cover the server tool result being fed back, `end_call` forwarded without another round, the backstop when the model skips the tool, the farewell instruction position, the prompt layout, unknown tools, `MaxToolRounds`, and FarewellDetector phrases and negations.

End to end against Ollama (`npm start` in `VoiceAgent-Client/`), measured 2026-09-29 with `qwen2.5:0.5b-instruct`:

| Caller says | Assistant said | Event |
|---|---|---|
| "Okay thanks, that's all. Bye!" | "Goodbye! If you need anything else, feel free to ask." | `end_call` |
| "I want to quit now" | "Alright, goodbye! … Have a great day!" | `end_call` |
| "No more questions, thank you" | "You're welcome! Have a great day!" | `end_call` |
| "Don't hang up, I have another question" | "Sure thing! I'm here to assist you. …" | none |

Client checks: `echo-filter.spec.ts` and `sentence-buffer.spec.ts` (Vitest). Vitest workers don't start inside the Claude Code sandbox, so the assertions were also run in Node via esbuild and passed. The production build passes the CSS budget.

Not verified automatically, needs a manual browser test: voice barge-in and echo behaviour on real speakers or headphones, the idle countdown and hang-up, and typed messages during speech.
