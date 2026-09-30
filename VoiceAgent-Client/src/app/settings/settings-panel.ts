import { Component, OnInit, inject, output, signal } from '@angular/core';

import { VoiceCallService } from '../voice/voice-call.service';
import { SettingsApiService, SettingsValidationError } from './settings-api.service';
import { ModelInfo, SettingsView, ValidationErrors, WebhookView } from './settings.models';

const NEW_TOOL_SCHEMA = {
  type: 'object',
  properties: {
    orderId: { type: 'string', description: 'The order number the caller mentions' },
  },
  required: ['orderId'],
};

/** Side panel for runtime settings: prompt, model + API key, voice, and tools. */
@Component({
  selector: 'app-settings-panel',
  templateUrl: './settings-panel.html',
  styleUrl: './settings-panel.css',
  standalone: false,
})
export class SettingsPanel implements OnInit {
  readonly closed = output<void>();

  private readonly api = inject(SettingsApiService);
  private readonly call = inject(VoiceCallService);

  protected readonly draft = signal<SettingsView | null>(null);
  protected readonly status = signal<{ kind: 'info' | 'error'; text: string } | null>(null);
  protected readonly errors = signal<ValidationErrors>({});
  protected readonly saving = signal(false);

  protected readonly models = signal<ModelInfo[]>([]);
  protected readonly modelsError = signal<string | null>(null);
  protected readonly kokoroVoices = signal<string[]>([]);
  protected readonly voicesError = signal<string | null>(null);
  protected readonly browserVoices = signal<SpeechSynthesisVoice[]>([]);
  protected readonly testing = signal(false);

  /** Typed but unsaved Gemini key; blank keeps the saved key. */
  protected geminiKeyInput = '';
  protected clearGeminiKey = false;
  /** One entry per webhook: its parameter schema as editable JSON text. */
  protected paramsText: string[] = [];
  protected readonly paramsErrors = signal<(string | null)[]>([]);
  protected phrasesText = '';

  /** A plain method, not `computed`: ngModel edits the draft in place, which signals can't see. */
  protected activeTools(): string {
    const tools = this.draft()?.tools;
    if (!tools) {
      return '';
    }
    return [
      tools.endCallEnabled ? 'end_call' : null,
      tools.dateTimeEnabled ? 'get_current_datetime' : null,
      ...tools.webhooks.filter((w) => w.enabled && w.name).map((w) => w.name),
    ]
      .filter(Boolean)
      .join(', ');
  }

  async ngOnInit(): Promise<void> {
    this.loadBrowserVoices();
    try {
      this.applyLoaded(await this.api.load());
    } catch (err) {
      this.status.set({ kind: 'error', text: `Could not load settings: ${message(err)}` });
      return;
    }
    const draft = this.draft()!;
    void this.refreshModels();
    if (draft.voice.provider === 'kokoro') {
      void this.refreshVoices();
    }
  }

  protected err(field: string): string | null {
    return this.errors()[field]?.join(' ') ?? null;
  }

  protected async refreshModels(): Promise<void> {
    const draft = this.draft();
    if (!draft) {
      return;
    }
    const provider = draft.llm.provider;
    this.modelsError.set(null);
    if (provider === 'gemini' && !this.geminiKeyInput.trim() && !draft.llm.gemini.apiKeySet) {
      this.models.set([]);
      this.modelsError.set('Enter your Gemini API key, then load the models.');
      return;
    }
    try {
      const models = await this.api.listModels(
        provider,
        draft.llm.ollama.baseUrl,
        provider === 'gemini' ? this.geminiKeyInput.trim() : null,
      );
      const selected = provider === 'gemini' ? draft.llm.gemini.model : draft.llm.ollama.model;
      // Keep the saved model selectable even if the server no longer lists it.
      this.models.set(
        selected && !models.some((m) => m.id === selected)
          ? [{ id: selected, displayName: `${selected} (not found)` }, ...models]
          : models,
      );
    } catch (err) {
      this.models.set([]);
      this.modelsError.set(message(err));
    }
  }

  protected onProviderChange(): void {
    this.models.set([]);
    void this.refreshModels();
  }

  protected async refreshVoices(): Promise<void> {
    const draft = this.draft();
    if (!draft) {
      return;
    }
    this.voicesError.set(null);
    try {
      this.kokoroVoices.set(await this.api.listVoices(draft.voice.kokoro.baseUrl));
    } catch (err) {
      this.kokoroVoices.set([]);
      this.voicesError.set(message(err));
    }
  }

  protected async testVoice(): Promise<void> {
    const draft = this.draft();
    if (!draft || this.testing()) {
      return;
    }
    this.testing.set(true);
    const sample = draft.assistant.greeting || 'Hello, this is how I sound.';
    try {
      if (draft.voice.provider === 'kokoro') {
        const blob = await this.api.previewKokoro(sample, draft.voice.kokoro.voice, draft.voice.kokoro.speed);
        await playBlob(blob);
      } else {
        await speakWithBrowser(sample, draft.voice.browser.voiceName, draft.voice.browser.rate);
      }
    } catch (err) {
      this.status.set({ kind: 'error', text: `Voice test failed: ${message(err)}` });
    } finally {
      this.testing.set(false);
    }
  }

  protected addWebhook(): void {
    const draft = this.draft()!;
    draft.tools.webhooks.push({
      id: null,
      name: '',
      description: '',
      parameters: NEW_TOOL_SCHEMA,
      url: '',
      headers: [],
      timeoutSeconds: 10,
      enabled: true,
    });
    this.paramsText.push(JSON.stringify(NEW_TOOL_SCHEMA, null, 2));
    this.paramsErrors.update((errors) => [...errors, null]);
  }

  protected removeWebhook(index: number): void {
    this.draft()!.tools.webhooks.splice(index, 1);
    this.paramsText.splice(index, 1);
    this.paramsErrors.update((errors) => errors.filter((_, i) => i !== index));
  }

  protected addHeader(webhook: WebhookView): void {
    webhook.headers.push({ name: '', value: '', valueSet: false, valueHint: null });
  }

  protected checkParams(index: number): void {
    const error = parseSchema(this.paramsText[index]).error;
    this.paramsErrors.update((errors) => errors.map((e, i) => (i === index ? error : e)));
  }

  protected async save(): Promise<void> {
    const current = this.draft();
    if (!current || this.saving()) {
      return;
    }
    const settings = structuredClone(current);

    const schemaErrors = this.paramsText.map((text) => parseSchema(text).error);
    this.paramsErrors.set(schemaErrors);
    if (schemaErrors.some(Boolean)) {
      this.status.set({ kind: 'error', text: 'Fix the tool parameter JSON first.' });
      return;
    }
    settings.tools.webhooks.forEach((webhook, i) => {
      webhook.parameters = parseSchema(this.paramsText[i]).schema;
      // A blank value on a saved header means "keep"; the API keeps secrets sent as null.
      webhook.headers = webhook.headers
        .filter((header) => header.name.trim())
        .map((header) => ({ ...header, value: header.valueSet && !header.value ? null : header.value }));
    });
    settings.llm.gemini.apiKey = this.clearGeminiKey ? '' : this.geminiKeyInput.trim() || null;
    settings.assistant.endCallPhrases = this.phrasesText
      .split('\n')
      .map((phrase) => phrase.trim())
      .filter(Boolean);

    this.saving.set(true);
    this.status.set(null);
    try {
      this.applyLoaded(await this.api.save(settings));
      this.errors.set({});
      this.status.set({ kind: 'info', text: 'Saved. Changes apply from the next reply.' });
      await this.call.refreshCallSettings();
    } catch (err) {
      if (err instanceof SettingsValidationError) {
        this.errors.set(err.errors);
        this.status.set({ kind: 'error', text: 'Some settings need fixing (see the red messages).' });
      } else {
        this.status.set({ kind: 'error', text: `Could not save: ${message(err)}` });
      }
    } finally {
      this.saving.set(false);
    }
  }

  private applyLoaded(settings: SettingsView): void {
    this.draft.set(settings);
    this.geminiKeyInput = '';
    this.clearGeminiKey = false;
    this.paramsText = settings.tools.webhooks.map((w) => JSON.stringify(w.parameters ?? {}, null, 2));
    this.paramsErrors.set(settings.tools.webhooks.map(() => null));
    this.phrasesText = settings.assistant.endCallPhrases.join('\n');
  }

  private loadBrowserVoices(): void {
    const load = () =>
      this.browserVoices.set(
        window.speechSynthesis.getVoices().filter((voice) => voice.lang.startsWith('en')),
      );
    load();
    // Chrome fills the voice list asynchronously.
    window.speechSynthesis.addEventListener('voiceschanged', load, { once: true });
  }
}

function parseSchema(text: string): { schema: Record<string, unknown> | null; error: string | null } {
  if (!text.trim()) {
    return { schema: null, error: null };
  }
  try {
    const schema = JSON.parse(text) as unknown;
    if (typeof schema !== 'object' || schema === null || Array.isArray(schema)) {
      return { schema: null, error: 'Must be a JSON object.' };
    }
    if ((schema as Record<string, unknown>)['type'] !== 'object') {
      return { schema: null, error: 'Needs "type": "object".' };
    }
    return { schema: schema as Record<string, unknown>, error: null };
  } catch (err) {
    return { schema: null, error: `Invalid JSON: ${message(err)}` };
  }
}

function playBlob(blob: Blob): Promise<void> {
  const url = URL.createObjectURL(blob);
  const audio = new Audio(url);
  return new Promise<void>((resolve, reject) => {
    audio.onended = () => resolve();
    audio.onerror = () => reject(new Error('The audio could not be played.'));
    audio.play().catch(reject);
  }).finally(() => URL.revokeObjectURL(url));
}

function speakWithBrowser(text: string, voiceName: string | null, rate: number): Promise<void> {
  return new Promise((resolve) => {
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.rate = rate;
    const voice = window.speechSynthesis.getVoices().find((v) => v.name === voiceName);
    if (voice) {
      utterance.voice = voice;
    }
    utterance.onend = () => resolve();
    utterance.onerror = () => resolve();
    window.speechSynthesis.cancel();
    window.speechSynthesis.speak(utterance);
  });
}

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
