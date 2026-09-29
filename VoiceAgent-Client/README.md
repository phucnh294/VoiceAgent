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
│   │   └── ConversationController.cs   POST /api/conversation/stream
│   ├── Services/
│   │   ├── OllamaClient.cs             streams replies from Ollama /api/chat
│   │   ├── OllamaOptions.cs            "Ollama" config section
│   │   ├── AssistantOptions.cs         "Assistant" config section (system prompt)
│   │   └── ChatMessage.cs, OllamaException.cs
│   ├── Properties/launchSettings.json  ports + launch profiles
│   └── appsettings.json                Ollama URL, model, system prompt
├── VoiceAgent.Application/ .Domain/ .Infrastructure/   empty placeholders (future layering)
└── VoiceAgent-Client/              this Angular 21 app
    ├── proxy.conf.json             forwards /api → API on :5043
    └── src/app/
        ├── app.ts / app.html / app.css     phone-call screen
        └── voice/
            ├── voice-call.service.ts       the call loop (listen → think → speak)
            ├── speech-recognizer.service.ts   speech-to-text (microphone)
            ├── speech-speaker.service.ts      text-to-speech (speaker)
            ├── sentence-buffer.ts             splits streamed text into sentences
            ├── conversation-api.service.ts    streams the reply from the API
            └── voice.config.ts                greeting, language, history size
```

---

## How it works

```
 Browser (Chrome/Edge)                        API :5043                    Ollama :8001
 ─────────────────────                        ─────────                    ────────────
 🎤 you speak
 SpeechRecognition → text ──POST /api/conversation/stream──►
   (whole conversation)                       + system prompt
                                              + last 20 turns  ──/api/chat──►  LLM
                                              ◄── text stream ─────────────────
 ◄────────── plain text, token by token ──────
 SentenceBuffer → speechSynthesis → 🔊 assistant speaks
 …then listens again
```

1. **Call**: the assistant says a greeting.
2. **Listening**: the browser transcribes what you say and shows it live. When you pause, your
   turn ends.
3. **Thinking**: the whole conversation is sent to the API. The API adds the system instruction
   (the representative's persona) and asks Ollama for a reply.
4. **Speaking**: the reply streams back, and the assistant starts speaking as soon as the first
   sentence is complete.
5. Back to listening, until you hang up.

Behaviour to know:
- **Half-duplex**: the microphone is off while the assistant speaks, so it never hears itself.
  Use **Interrupt** to cut it off and talk.
- **Silence** keeps the line open; the assistant simply keeps listening.
- **Errors**: if the model fails, the assistant says so and the call continues. If microphone
  access is denied, the call ends with a message.
- **No server state**: the API keeps nothing between requests. The browser holds the conversation
  and sends it with every turn.

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
  "MaxHistoryMessages": 20
}
```

| Key | What it does |
|---|---|
| `Ollama:BaseUrl` | Address of your Ollama server. Ollama's own default is `http://localhost:11434`; this project currently points to `8001`. |
| `Ollama:Model` | Model used for replies. It must already be pulled on that server. Bigger models (e.g. `qwen2.5:3b-instruct`) give much better answers. |
| `Assistant:SystemPrompt` | The representative's persona and speaking style. Add your company name, services, opening hours, etc. here; no code change is needed. Keep the "short spoken sentences, no markdown" rules, because everything is read aloud. |
| `Assistant:MaxHistoryMessages` | How many recent turns are sent to the model. Higher gives more memory but slower replies. |

Restart the API after changing these settings.

### Client: `src/app/voice/voice.config.ts`

| Constant | What it does |
|---|---|
| `CALL_LANGUAGE` | Speech language for listening and speaking (`en-US`). If you change it, also tell the model in `SystemPrompt` to reply in that language. |
| `CALL_GREETING` | First sentence spoken when the call connects. |
| `CALL_ERROR_REPLY` | What the assistant says when a reply fails. |
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

## Other commands

Run these in `VoiceAgent-Client/` unless noted.

```bash
npm run build                          # production build → dist/VoiceAgent-Client/browser/
npx ng test --watch=false              # unit tests (Vitest)
npx ng test --watch=false --include src/app/voice/sentence-buffer.spec.ts   # one spec
npx prettier --write src               # format
dotnet build ../VoiceAgent.slnx        # build the API
```

Quick API check without the browser (with the app running):

```bash
curl -N -X POST -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello, who am I talking to?"}]}' \
  http://127.0.0.1:53034/api/conversation/stream
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
| Angular build fails with `Unexpected end of file in JSON` in `../package.json` | The root `package.json` must be valid JSON (at least `{}`). |
