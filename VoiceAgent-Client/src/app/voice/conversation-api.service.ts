import { Injectable } from '@angular/core';

export type ChatRole = 'user' | 'assistant';

export interface ChatTurn {
  role: ChatRole;
  content: string;
}

/**
 * Talks to `POST /api/conversation/stream`. Uses fetch rather than HttpClient because the reply
 * has to be consumed incrementally as plain-text fragments. The relative URL goes through the
 * `ng serve` proxy (proxy.conf.json) to the API.
 */
@Injectable({ providedIn: 'root' })
export class ConversationApiService {
  private readonly endpoint = '/api/conversation/stream';

  async *streamReply(history: ChatTurn[], signal: AbortSignal): AsyncGenerator<string> {
    const response = await fetch(this.endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ messages: history }),
      signal,
    });

    if (!response.ok || !response.body) {
      const detail = await response.text();
      throw new Error(`Assistant unavailable (${response.status}): ${detail}`);
    }

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) {
          return;
        }
        if (value) {
          yield value;
        }
      }
    } finally {
      reader.releaseLock();
    }
  }
}
