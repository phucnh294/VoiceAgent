# Voice Agent — Virtual Assistant Call

A browser voice assistant that works like a phone call with a virtual representative. Press
**Call**, speak naturally, and the assistant answers out loud. The conversation keeps going turn
after turn until you press **Hang up**.

- **Speech-to-text and text-to-speech** run in the browser (Web Speech API).
- **Answers** come from a local LLM served by [Ollama](https://ollama.com), through an ASP.NET
  Core API.

This folder is the Angular front end. The API lives next to it in `../VoiceAgent.Api`.

---

## Project structure

```
VoiceAgent/
├── VoiceAgent.slnx                 Visual Studio solution (API + client)
├── VoiceAgent.Api/                 ASP.NET Core API (.NET 10)
│   ├── Controllers/
│   │   └── ConversationController.cs   POST /api/conversation/stream (NDJSON events)
│   ├── Conversation/
│   │   ├── ConversationService.cs      builds the prompt, runs the tool loop
│   │   ├── FarewellDetector.cs         ends the call on "bye" even if the model forgets
│   │   └── ConversationEvent.cs        text / action / error events
│   ├── Tools/
│   │   ├── AssistantTools.cs           IServerTool / IClientTool interfaces
│   │   ├── EndCallTool.cs              end_call (runs in the browser)
│   │   └── CurrentDateTimeTool.cs      get_current_datetime (runs on the server)
│   ├── CallLogging/                    per-call JSON Lines log (prompts, responses, tools)
│   ├── Services/
│   │   ├── OllamaClient.cs             streams replies + tool calls from Ollama /api/chat
│   │   ├── IChatModel.cs               model abstraction (faked in tests)
│   │   ├── OllamaOptions.cs            "Ollama" config section
│   │   └── AssistantOptions.cs         "Assistant" config section
│   ├── Properties/launchSettings.json  ports + launch profiles
├── VoiceAgent.Application/ .Domain/ .Infrastructure/   empty placeholders (future layering)
└── VoiceAgent-Client/              this Angular 21 app
    ├── proxy.conf.json             forwards /api → API on :5043
    └── src/app/
        ├── app.ts / app.html / app.css     phone-call screen
        └── voice/
            ├── voice-call.service.ts       the call loop, barge-in, idle timeout, end call
            ├── speech-recognizer.service.ts   speech-to-text (microphone)
            ├── speech-speaker.service.ts      text-to-speech (speaker)
            ├── sentence-buffer.ts             splits streamed text into sentences
            ├── echo-filter.ts                 ignores the assistant's own voice on the mic
            ├── conversation-api.service.ts    reads the NDJSON reply stream
            └── voice.config.ts                greeting, language, timeouts
```

---

## How it works

```
 Browser (Chrome/Edge)                        API :5043                    Ollama :8001
 ─────────────────────                        ─────────                    ────────────
 🎤 you speak (or type)
 SpeechRecognition → text ──POST /api/conversation/stream──►
   (whole conversation)                       + persona + tool rules
                                              + last 20 turns  ──/api/chat──►  LLM
                                              ◄── text + tool calls ───────────
                                              runs server tools, loops
 ◄──── NDJSON: text fragments, end_call ──────
 SentenceBuffer → speechSynthesis → 🔊 assistant speaks
 …then listens again (or hangs up on end_call)
```

1. **Call**: the assistant says a greeting.
2. **Listening**: the browser transcribes what you say and shows it live. When you pause, your
   turn ends. You can also type a message.
3. **Thinking**: the whole conversation is sent to the API. The API adds the system instruction
   (the representative's persona and tool rules) and asks Ollama for a reply. If the model calls a
   server tool, the API runs it and asks the model again with the result.
4. **Speaking**: the reply streams back, and the assistant starts speaking as soon as the first
   sentence is complete.
5. Back to listening, until the call ends.

How a call ends:
- **You say goodbye** ("bye", "that's all", "I want to quit", …): the assistant says a short
  goodbye and hangs up. The model can end the call with the `end_call` tool, and the API also
  recognises goodbye phrases itself, because small models often forget the tool.
- **You stay silent for 60 seconds**: a countdown appears for the last 15 seconds, then the
  assistant says goodbye and hangs up.
- **You press Hang up**.

Interrupting the assistant:
- **By voice**: just start talking while it speaks. It stops, and what you say becomes your next
  message. The microphone also hears the assistant's own voice, so speech that repeats the
  assistant's words is ignored. This works best with **headphones**; turn off
  "Interrupt by voice" if the assistant keeps cutting itself off on speakers.
- **By typing**: send a message at any time; it interrupts the assistant and is answered next.
- **Interrupt button**: stops the assistant and goes back to listening.
- An interrupted reply stays in the transcript, marked *interrupted*, so the model knows what it
  had already said.

Other behaviour:
- **Silence** within the 60 seconds keeps the line open; the assistant simply keeps listening.
- **Errors**: if the model fails, the assistant says so and the call continues. If microphone
  access is denied, the call ends with a message.
- **No server state**: the API keeps nothing between requests. The browser holds the conversation
  and sends it with every turn.

Tools the assistant has:

| Tool | Runs in | What it does |
|---|---|---|
| `end_call` | browser | Says the farewell and hangs up. |
| `get_current_datetime` | API | Returns the real local date and time, so the assistant doesn't guess. |

To add a tool, implement `IServerTool` (runs in the API; its result goes back to the model) or
`IClientTool` (forwarded to the browser as an `action` event) in `VoiceAgent.Api/Tools/`, and
register it in `Program.cs` with `AddSingleton<IAssistantTool, YourTool>()`. A client tool also
needs handling in `voice-call.service.ts`.

The full design (decisions, gotchas, verification) is in
[`rag-ai-local/functionality-docs/09292026/01_voice-call-architecture.md`](../rag-ai-local/functionality-docs/09292026/01_voice-call-architecture.md).

---

## Prerequisites

| Requirement | Why |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Runs the API |
| [Node.js](https://nodejs.org) 20+ and npm | Runs the Angular app |
| [Ollama](https://ollama.com), with a model pulled | Generates the answers |
| **Chrome or Edge on desktop**, with a microphone | Web Speech recognition; Chrome's speech recognition needs internet |

Pull the model the API is configured for, on the Ollama server it points to:

```bash
ollama pull qwen2.5:0.5b-instruct
```

---

## Configuration

### API: `VoiceAgent.Api/appsettings.json`

```json
"Ollama": {
  "BaseUrl": "http://localhost:8001",
  "Model": "qwen2.5:0.5b-instruct"
},
"Assistant": {
  "SystemPrompt": "You are a friendly virtual representative talking with a caller on a live voice call. …",
  "ToolInstructions": "You have tools. When the caller asks the time, day or date, call get_current_datetime …",
  "FarewellInstruction": "The caller is ending the call. Reply with one short, warm goodbye sentence only …",
  "MaxHistoryMessages": 20,
  "MaxToolRounds": 3,
  "EndCallPhrases": ["bye", "goodbye", "that's all", "hang up", "quit", "…"]
}
```

| Key | What it does |
|---|---|
| `Ollama:BaseUrl` | Address of your Ollama server. Ollama's own default is `http://localhost:11434`; this project currently points to `8001`. |
| `Ollama:Model` | Model used for replies. It must already be pulled on that server. Bigger models (e.g. `qwen2.5:3b-instruct`) give much better answers **and use tools reliably**; the 0.5B model rarely calls tools. |
| `Assistant:SystemPrompt` | The representative's persona and speaking style. Add your company name, services, opening hours, etc. here; no code change is needed. Keep the "short spoken sentences, no markdown" rules, because everything is read aloud. |
| `Assistant:ToolInstructions` | When to use which tool. Sent as a separate system message after the persona, which small models follow more often. Update it when you add a tool. |
| `Assistant:FarewellInstruction` | Added when the caller says goodbye, so the model answers with a goodbye instead of another question. |
| `Assistant:MaxHistoryMessages` | How many recent turns are sent to the model. Higher gives more memory but slower replies. |
| `Assistant:MaxToolRounds` | Maximum model → tool → model round trips per reply, so a confused model can't loop forever. |
| `Assistant:EndCallPhrases` | Caller phrases that end the call. They only count in short utterances (12 words or fewer) and not after a negation ("don't hang up"). An empty list uses the built-in phrases. Change them if the call language changes. |
| `CallLog:Enabled` | Write a log file per call (see [Call logs](#call-logs)). Default `true`. |
| `CallLog:Directory` | Where call logs go, relative to `VoiceAgent.Api/`. Default `logs/calls`. |

Restart the API after changing these settings.

### Client: `src/app/voice/voice.config.ts`

| Constant | What it does |
|---|---|
| `CALL_LANGUAGE` | Speech language for listening and speaking (`en-US`). If you change it, also tell the model in `SystemPrompt` to reply in that language. |
| `CALL_GREETING` | First sentence spoken when the call connects. |
| `CALL_ERROR_REPLY` | What the assistant says when a reply fails. |
| `CALL_FAREWELL` | Goodbye spoken when the model ends the call without saying anything. |
| `IDLE_TIMEOUT_SECONDS` | Silence (no speech or typing) before the call ends; default 60. |
| `IDLE_WARNING_SECONDS` | How long before the idle hang-up the countdown appears; default 15. |
| `IDLE_GOODBYE` | What the assistant says before an idle hang-up. |
| `ECHO_WORD_OVERLAP` | While the assistant speaks, heard speech sharing at least this share of words with the reply (default 0.6) counts as the assistant's own echo. Lower it if the assistant keeps interrupting itself on speakers. |
| `MAX_HISTORY_TURNS` | How many turns the browser sends per request. |

### Ports

| What | URL | Set in |
|---|---|---|
| Angular app | http://127.0.0.1:53034 | `angular.json` → `serve.options.port` |
| API | http://localhost:5043 (https://localhost:7007 with the `https` profile) | `VoiceAgent.Api/Properties/launchSettings.json` |
| API proxy | `/api/*` from the Angular app → `http://localhost:5043` | `proxy.conf.json` |

If you change the API port, update `proxy.conf.json` to match. The client always calls the
relative `/api/...` path.

---

## How to start

Install the client dependencies once:

```bash
cd VoiceAgent-Client
npm install
```

Make sure Ollama is running at the `Ollama:BaseUrl` address. Then pick **one** of these ways to
start; each starts both the API and the front end.

**A. From the client folder (recommended)**

```bash
cd VoiceAgent-Client
npm start
```

This runs the API and `ng serve` side by side (log prefixes `[api]` and `[web]`). Stopping it
with Ctrl+C stops both.

**B. From the API**

```bash
dotnet run --project VoiceAgent.Api
```

The API starts on :5043 and launches the Angular dev server automatically (ASP.NET SpaProxy). If
the Angular app is already running, the API reuses it.

**C. Visual Studio**: open `VoiceAgent.slnx`, set `VoiceAgent.Api` as the startup project and
press F5.

Then open **http://127.0.0.1:53034** in Chrome or Edge, press **Call** and allow the microphone.

The first reply after Ollama starts can take about 15 seconds while the model loads. Later replies
start in under a second.

### Run one side only

| Command (in `VoiceAgent-Client/`) | Starts |
|---|---|
| `npm run serve` | Angular only |
| `npm run serve:api` | API only (no auto-launch of Angular) |

---

## Call logs

Every call is logged so you can check exactly what the model was given and what it answered.

**Where:** `VoiceAgent.Api/logs/calls/<yyyy-MM-dd>/<callId>.jsonl`, one file per call. The
browser creates the call ID when you press Call; the API also returns it in the `X-Call-Id`
response header. The `logs/` folder is git-ignored.

**Format:** JSON Lines, one record per line, each with `type`, `timestamp` and `callId`:

| `type` | Written when | Contains |
|---|---|---|
| `model_request` | before each request to the model | `turn`, `round`, `model`, the **full `messages` list exactly as sent** (system prompt, tool instructions, history, farewell instruction) and the tool names |
| `model_response` | after the model answers (or is cut off) | `text`, `toolCalls`, `status`, `durationMs` |
| `tool_call` | the model called a tool | `tool`, `runsOn` (`server`/`browser`), `arguments`, `result`, `durationMs` |
| `turn` | end of each caller turn | `callerSaid`, `assistantReplied` (what the browser received), `actions` with their `source` (`model` or `farewell_detector`), `modelRounds`, `status`, `durationMs` |
| `call_end` | the browser reports the end of the call | `reason` (`caller`, `goodbye`, `idle`, `error`, `closed`), `durationSeconds`, and the full `transcript` as the caller saw it, including the greeting, idle goodbye, *interrupted* replies and *typed* messages |

`status` is `completed`, `cancelled` (you interrupted or hung up while the assistant was
answering; the partial answer is kept) or `failed` (the model errored; details are in the API
console log).

Example turn, abbreviated:

```json
{"type":"model_request","turn":2,"round":1,"model":"qwen2.5:0.5b-instruct","messages":[{"role":"system","content":"You are a friendly virtual representative…"},{"role":"system","content":"You have tools…"},{"role":"assistant","content":"Hello, thanks for calling…"},{"role":"user","content":"Okay thanks, bye"},{"role":"system","content":"The caller is ending the call…"}],"tools":["end_call","get_current_datetime"],"timestamp":"…","callId":"…"}
{"type":"model_response","turn":2,"round":1,"text":"Goodbye, have a great day!","toolCalls":[],"status":"completed","durationMs":12033,…}
{"type":"turn","turn":2,"status":"completed","callerSaid":"Okay thanks, bye","assistantReplied":"Goodbye, have a great day!","actions":[{"name":"end_call","arguments":{},"source":"farewell_detector"}],"modelRounds":1,"durationMs":12040,…}
```

Reading them, for example with [jq](https://jqlang.org):

```bash
cd VoiceAgent.Api/logs/calls/2026-09-29
jq -c 'select(.type=="turn") | {turn, status, callerSaid, assistantReplied}' <callId>.jsonl
jq '.messages' <callId>.jsonl | head -50          # exact prompts sent to the model
```

The logs contain everything callers say. Treat them as personal data: don't share them, and
delete old folders when you no longer need them. Set `CallLog:Enabled` to `false` to turn
logging off.

---

## Other commands

Run these in `VoiceAgent-Client/` unless noted.

```bash
npm run build                          # production build → dist/VoiceAgent-Client/browser/
npx ng test --watch=false              # unit tests (Vitest)
npx ng test --watch=false --include src/app/voice/sentence-buffer.spec.ts   # one spec
npx prettier --write src               # format
dotnet build ../VoiceAgent.slnx        # build the API
dotnet test ../VoiceAgent.Api.Tests    # API unit tests (tool loop, farewell detection)
```

Quick API check without the browser (with the app running):

```bash
curl -N -X POST -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Okay thanks, bye!"}]}' \
  http://127.0.0.1:53034/api/conversation/stream
```

The reply is NDJSON, one event per line:

```
{"type":"text","text":"Goodbye! "}
{"type":"text","text":"Have a great day!"}
{"type":"action","name":"end_call","arguments":{}}
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Port 53034 is already in use` | Another `ng serve` is running. Stop it, or use it and start only the API (`npm run serve:api`). |
| Assistant says "Sorry, I'm having trouble answering…" / API returns 502 | Ollama isn't reachable at `Ollama:BaseUrl`, or the model isn't pulled. Check with `curl <BaseUrl>/api/tags`. |
| "Speech recognition is not supported" | Use Chrome or Edge on desktop. Firefox and Safari aren't supported. |
| "Microphone access was denied" | Allow the microphone in the browser's site settings, then press Call again. Use `127.0.0.1` or `localhost`; a LAN IP over http can't use the microphone. |
| "Speech recognition needs an internet connection" | Chrome's recognition runs on Google's servers. Connect to the internet. |
| Answers are poor or off-topic | The 0.5B model is very small. Set a larger `Ollama:Model` and make `SystemPrompt` more specific. |
| Assistant gives a wrong time or date | The model answered without calling `get_current_datetime`. The 0.5B model often skips tools; use a larger model. |
| Assistant keeps interrupting itself | The microphone hears the speakers. Use headphones, turn off "Interrupt by voice", or lower `ECHO_WORD_OVERLAP`. |
| Call ends when you didn't mean to | A short sentence contained a goodbye phrase. Edit `Assistant:EndCallPhrases`. |
| Angular build fails with `Unexpected end of file in JSON` in `../package.json` | The root `package.json` must be valid JSON (at least `{}`). |
