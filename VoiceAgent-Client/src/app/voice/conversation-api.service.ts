import { Injectable } from '@angular/core';

export type ChatRole = 'user' | 'assistant';

export interface ChatTurn {
  role: ChatRole;
  content: string;
}

/** One NDJSON line of the API reply stream (mirrors the API's ConversationEvent). */
export type ReplyEvent =
  | { type: 'text'; text: string }
  | { type: 'action'; name: string; arguments: Record<string, unknown> }
  | { type: 'error'; message: string };

/** Sent when a call ends, so the API's call log records why and what the caller saw. */
export interface CallEndReport {
  reason: string;
  durationSeconds: number;
  transcript: { role: ChatRole; content: string; interrupted?: boolean; typed?: boolean }[];
}

/**
 * Talks to the conversation API. Uses fetch rather than HttpClient because the reply has to be
 * consumed incrementally. Relative URLs go through the `ng serve` proxy (proxy.conf.json).
 */
@Injectable({ providedIn: 'root' })
export class ConversationApiService {
  private readonly baseUrl = '/api/conversation';

  /** `callId` groups every turn of one call into one API call log file. */
  async *streamReply(
    callId: string,
    history: ChatTurn[],
    signal: AbortSignal,
  ): AsyncGenerator<ReplyEvent> {
    const response = await fetch(`${this.baseUrl}/stream`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ callId, messages: history }),
      signal,
    });

    if (!response.ok || !response.body) {
      const detail = await response.text();
      throw new Error(`Assistant unavailable (${response.status}): ${detail}`);
    }

    const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
    let buffered = '';
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        buffered += value;
        const lines = buffered.split('\n');
        buffered = lines.pop() ?? '';
        for (const line of lines) {
          if (line.trim()) {
            yield JSON.parse(line) as ReplyEvent;
          }
        }
      }
      if (buffered.trim()) {
        yield JSON.parse(buffered) as ReplyEvent;
      }
    } finally {
      reader.releaseLock();
    }
  }

  /**
   * Fire-and-forget: a failed report must never disturb the caller. `keepalive` lets the request
   * finish even when it is sent while the page is closing.
   */
  reportCallEnd(callId: string, report: CallEndReport): void {
    fetch(`${this.baseUrl}/${callId}/end`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(report),
      keepalive: true,
    }).catch((err: unknown) => console.warn('Could not report call end', err));
  }
}
