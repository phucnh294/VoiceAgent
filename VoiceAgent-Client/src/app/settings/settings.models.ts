// Mirrors the API's SettingsView (VoiceAgent.Api/Settings/SettingsView.cs). Secrets are
// write-only: on load they are null with `…Set`/`…Hint`; on save, null keeps the stored value,
// '' clears it and any other value replaces it.

export type LlmProvider = 'ollama' | 'gemini';
export type VoiceProvider = 'kokoro' | 'browser';

export interface SettingsView {
  assistant: AssistantSettings;
  llm: LlmView;
  voice: VoiceSettings;
  tools: ToolsView;
}

export interface AssistantSettings {
  systemPrompt: string;
  greeting: string;
  toolInstructions: string;
  farewellInstruction: string;
  endCallPhrases: string[];
  maxHistoryMessages: number;
  maxToolRounds: number;
}

export interface LlmView {
  provider: LlmProvider;
  ollama: { baseUrl: string; model: string };
  gemini: { apiKey: string | null; apiKeySet: boolean; apiKeyHint: string | null; model: string };
}

export interface VoiceSettings {
  provider: VoiceProvider;
  kokoro: { baseUrl: string; voice: string; speed: number };
  browser: { voiceName: string | null; rate: number };
}

export interface ToolsView {
  endCallEnabled: boolean;
  dateTimeEnabled: boolean;
  webhooks: WebhookView[];
}

export interface WebhookView {
  id: string | null;
  name: string;
  description: string;
  parameters: Record<string, unknown> | null;
  url: string;
  headers: HeaderView[];
  timeoutSeconds: number;
  enabled: boolean;
}

export interface HeaderView {
  name: string;
  value: string | null;
  valueSet: boolean;
  valueHint: string | null;
}

export interface ModelInfo {
  id: string;
  displayName: string;
}

/** Non-secret settings a call needs (GET /api/call-settings). */
export interface CallSettings {
  greeting: string;
  voice: VoicePlayback;
}

export interface VoicePlayback {
  provider: VoiceProvider;
  browserVoiceName: string | null;
  browserRate: number;
}

/** Field path → messages, from the API's validation problem details. */
export type ValidationErrors = Record<string, string[]>;
