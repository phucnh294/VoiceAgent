import { Injectable, computed, inject, signal } from '@angular/core';

import { ChatRole, ChatTurn, ConversationApiService } from './conversation-api.service';
import { SentenceBuffer } from './sentence-buffer';
import { SpeechRecognizerService } from './speech-recognizer.service';
import { SpeechSpeakerService } from './speech-speaker.service';
import {
  CALL_ERROR_REPLY,
  CALL_GREETING,
  MAX_HISTORY_TURNS,
} from './voice.config';

/**
 * idle → (start) → speaking greeting → listening → thinking → speaking → listening → …
 * The microphone is off while the assistant speaks, so it never hears (and answers) itself.
 */
export type CallState = 'idle' | 'listening' | 'thinking' | 'speaking';

export interface CallTurn extends ChatTurn {
  id: number;
}

/** Runs a phone-call-style, hands-free conversation with the assistant. */
@Injectable({ providedIn: 'root' })
export class VoiceCallService {
  private readonly recognizer = inject(SpeechRecognizerService);
  private readonly speaker = inject(SpeechSpeakerService);
  private readonly api = inject(ConversationApiService);

  readonly state = signal<CallState>('idle');
  readonly turns = signal<CallTurn[]>([]);
  /** What the caller is saying right now, before the browser finalises it. */
  readonly interim = signal('');
  readonly error = signal<string | null>(null);
  readonly elapsedSeconds = signal(0);
  readonly inCall = computed(() => this.state() !== 'idle');

  readonly isSupported = this.recognizer.isSupported && this.speaker.isSupported;

  /** Bumped on every start/hang-up so a stale loop from a previous call stops itself. */
  private callId = 0;
  private nextTurnId = 0;
  private replyAbort: AbortController | null = null;
  private timer: ReturnType<typeof setInterval> | null = null;

  async startCall(): Promise<void> {
    if (this.inCall() || !this.isSupported) {
      return;
    }

    const callId = ++this.callId;
    this.turns.set([]);
    this.interim.set('');
    this.error.set(null);
    this.startTimer();

    this.state.set('speaking');
    this.addTurn('assistant', CALL_GREETING);
    this.speaker.enqueue(CALL_GREETING);
    await this.speaker.whenIdle();

    await this.runCallLoop(callId);
  }

  hangUp(): void {
    if (!this.inCall()) {
      return;
    }
    this.callId++;
    this.replyAbort?.abort();
    this.replyAbort = null;
    this.recognizer.abort();
    this.speaker.cancel();
    this.stopTimer();
    this.interim.set('');
    this.state.set('idle');
  }

  /** Cuts the assistant off mid-answer and goes straight back to listening. */
  interrupt(): void {
    if (this.state() !== 'thinking' && this.state() !== 'speaking') {
      return;
    }
    this.replyAbort?.abort();
    this.speaker.cancel();
  }

  private async runCallLoop(callId: number): Promise<void> {
    while (this.isCurrent(callId)) {
      this.state.set('listening');
      this.interim.set('');

      let heard: string;
      try {
        heard = await this.recognizer.listen((text) => this.interim.set(text));
      } catch (err) {
        if (this.isCurrent(callId)) {
          this.error.set(errorMessage(err));
          this.hangUp();
        }
        return;
      }

      if (!this.isCurrent(callId)) {
        return;
      }
      this.interim.set('');
      if (!heard) {
        continue; // Silence: keep the line open.
      }

      this.addTurn('user', heard);
      await this.respond(callId);
    }
  }

  private async respond(callId: number): Promise<void> {
    const history: ChatTurn[] = this.turns()
      .filter((turn) => turn.content.trim())
      .slice(-MAX_HISTORY_TURNS)
      .map(({ role, content }) => ({ role, content }));

    this.state.set('thinking');
    this.error.set(null);
    const abort = new AbortController();
    this.replyAbort = abort;
    const replyId = this.addTurn('assistant', '');
    const sentences = new SentenceBuffer();
    let reply = '';

    const say = (sentence: string) => {
      if (this.isCurrent(callId)) {
        this.state.set('speaking');
        this.speaker.enqueue(sentence);
      }
    };

    try {
      for await (const fragment of this.api.streamReply(history, abort.signal)) {
        reply += fragment;
        this.updateTurn(replyId, reply);
        sentences.push(fragment).forEach(say);
      }
      const rest = sentences.flush();
      if (rest) {
        say(rest);
      }
    } catch (err) {
      if (!abort.signal.aborted && this.isCurrent(callId)) {
        this.error.set(errorMessage(err));
        if (!reply) {
          reply = CALL_ERROR_REPLY;
          this.updateTurn(replyId, reply);
          say(reply);
        }
      }
    } finally {
      if (this.replyAbort === abort) {
        this.replyAbort = null;
      }
    }

    if (!reply.trim()) {
      this.turns.update((turns) => turns.filter((turn) => turn.id !== replyId));
    }
    await this.speaker.whenIdle();
  }

  private isCurrent(callId: number): boolean {
    return callId === this.callId && this.inCall();
  }

  private addTurn(role: ChatRole, content: string): number {
    const id = this.nextTurnId++;
    this.turns.update((turns) => [...turns, { id, role, content }]);
    return id;
  }

  private updateTurn(id: number, content: string): void {
    this.turns.update((turns) =>
      turns.map((turn) => (turn.id === id ? { ...turn, content } : turn)),
    );
  }

  private startTimer(): void {
    this.stopTimer();
    this.elapsedSeconds.set(0);
    this.timer = setInterval(() => this.elapsedSeconds.update((s) => s + 1), 1000);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
