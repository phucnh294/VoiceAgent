import { Injectable } from '@angular/core';

import { ModelInfo, SettingsView, ValidationErrors } from './settings.models';

/** Thrown when the API rejects settings; carries field-level messages. */
export class SettingsValidationError extends Error {
  constructor(readonly errors: ValidationErrors) {
    super('Some settings are invalid.');
  }
}

/** The Settings panel's API (`/api/settings`, loopback-only on the server). */
@Injectable({ providedIn: 'root' })
export class SettingsApiService {
  private readonly baseUrl = '/api/settings';

  load(): Promise<SettingsView> {
    return this.request<SettingsView>(this.baseUrl);
  }

  async save(settings: SettingsView): Promise<SettingsView> {
    const response = await fetch(this.baseUrl, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(settings),
    });
    if (response.status === 400) {
      const problem = (await response.json()) as { errors?: ValidationErrors };
      throw new SettingsValidationError(problem.errors ?? {});
    }
    return this.parse<SettingsView>(response);
  }

  /** Lists models, trying the given URL/key before they are saved. */
  async listModels(provider: string, baseUrl?: string, apiKey?: string | null): Promise<ModelInfo[]> {
    const result = await this.request<{ models: ModelInfo[] }>(`${this.baseUrl}/models`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ provider, baseUrl, apiKey: apiKey || null }),
    });
    return result.models;
  }

  async listVoices(baseUrl: string): Promise<string[]> {
    const query = new URLSearchParams({ baseUrl });
    const result = await this.request<{ voices: string[] }>(`${this.baseUrl}/voices?${query}`);
    return result.voices;
  }

  /** Synthesizes a sample with unsaved voice settings (the "Test voice" button). */
  async previewKokoro(text: string, voice: string, speed: number): Promise<Blob> {
    const response = await fetch('/api/speech', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text, voice, speed }),
    });
    if (!response.ok) {
      throw new Error(await errorText(response));
    }
    return response.blob();
  }

  private async request<T>(url: string, init?: RequestInit): Promise<T> {
    return this.parse<T>(await fetch(url, init));
  }

  private async parse<T>(response: Response): Promise<T> {
    if (!response.ok) {
      throw new Error(await errorText(response));
    }
    return (await response.json()) as T;
  }
}

async function errorText(response: Response): Promise<string> {
  const text = await response.text();
  try {
    const body = JSON.parse(text) as { error?: string; title?: string };
    return body.error ?? body.title ?? text;
  } catch {
    return text || `HTTP ${response.status}`;
  }
}
