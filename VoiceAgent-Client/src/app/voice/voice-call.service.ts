import { Injectable, computed, inject, signal } from '@angular/core';

import {
  ChatRole,
  ChatTurn,
  ConversationApiService,
  ReplyEvent,
} from './conversation-api.service';
import { isLikelyEcho } from './echo-filter';
import { SentenceBuffer } from './sentence-buffer';
import { SpeechRecognizerService } from './speech-recognizer.service';
import { SpeechSpeakerService } from './speech-speaker.service';
import {
  CALL_ERROR_REPLY,
  CALL_FAREWELL,
  CALL_GREETING,
  IDLE_GOODBYE,
  IDLE_TIMEOUT_SECONDS,
  IDLE_WARNING_SECONDS,
  MAX_HISTORY_TURNS,
} from './voice.config';

/**
 * idle → (start) → speaking greeting → listening → thinking → speaking → listening → …
 * While the assistant thinks or speaks, the caller can still cut in by voice (if enabled) or by
 * typing; the partial reply is kept and the caller's words become the next turn.
 */
export type CallState = 'idle' | 'listening' | 'thinking' | 'speaking';

/** Why the last call ended ('closed' = the page was closed mid-call). */
export type CallEndReason = 'caller' | 'goodbye' | 'idle' | 'error' | 'closed';

export interface CallTurn extends ChatTurn {
  id: number;
  /** The assistant was cut off before finishing this reply. */
  interrupted?: boolean;
  /** The caller typed this turn instead of saying it. */
  typed?: boolean;
}

type CallerInput =
  | { kind: 'text'; text: string; typed: boolean }
  | { kind: 'silence' }
  | { kind: 'idle' }
  | { kind: 'stopped' }
  | { kind: 'error'; error: unknown };

type TurnOutcome =
  | { kind: 'completed'; endCall: boolean }
  | { kind: 'interrupted'; input: CallerInput };

interface ListenOptions {
  useMic: boolean;
  /** Ends the wait early (e.g. the assistant turn finished without being interrupted). */
  stop?: AbortSignal;
  /** Heard speech that returns true is ignored (the assistant's own voice). */
  isEcho?: (heard: string) => boolean;
  /** Called once, as soon as the caller starts talking or sends typed text. */
  onCallerActive?: () => void;
  /** Give up with `idle` at this timestamp (ms) if the caller hasn't started talking. */
  idleAt?: number;
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
  readonly endReason = signal<CallEndReason | null>(null);
  /** Seconds until an idle hang-up, shown only during the final warning window. */
  readonly idleSecondsLeft = signal<number | null>(null);
  /** Listen while the assistant speaks so the caller can cut in by voice (best with headphones). */
  readonly voiceInterrupt = signal(true);
  readonly inCall = computed(() => this.state() !== 'idle');

  readonly isSupported = this.recognizer.isSupported && this.speaker.isSupported;

  /** Bumped on every start/hang-up so a stale loop from a previous call stops itself. */
  private callId = 0;
  /** Identifies the current call in the API's call log. */
  private callLogId = '';
  private callAbort: AbortController | null = null;
  private nextTurnId = 0;
  private lastCallerActivity = 0;
  private timer: ReturnType<typeof setInterval> | null = null;
  private cutOffCurrentTurn: (() => void) | null = null;
  /** Receives typed text while something is waiting for the caller; otherwise it queues. */
  private typedReceiver: ((text: string) => void) | null = null;
  private typedQueue: string[] = [];

  constructor() {
    // Closing or reloading the tab mid-call still gets the call logged as ended.
    window.addEventListener('pagehide', () => this.hangUp('closed'));
  }

  async startCall(): Promise<void> {
    if (this.inCall() || !this.isSupported) {
      return;
    }

    const callId = ++this.callId;
    this.callLogId = crypto.randomUUID();
    this.callAbort = new AbortController();
    this.turns.set([]);
    this.typedQueue = [];
    this.interim.set('');
    this.error.set(null);
    this.endReason.set(null);
    this.lastCallerActivity = Date.now();
    this.startTimer();
    this.state.set('speaking');

    const greeting = await this.assistantTurn(callId, () => fixedReply(CALL_GREETING));
    if (!this.isCurrent(callId)) {
      return;
    }
    await this.runCallLoop(callId, greeting.kind === 'interrupted' ? greeting.input : null);
  }

  hangUp(reason: CallEndReason = 'caller'): void {
    if (!this.inCall()) {
      return;
    }
    this.api.reportCallEnd(this.callLogId, {
      reason,
      durationSeconds: this.elapsedSeconds(),
      transcript: this.turns()
        .filter((turn) => turn.content.trim())
        .map(({ role, content, interrupted, typed }) => ({ role, content, interrupted, typed })),
    });
    this.callId++;
    this.callAbort?.abort();
    this.callAbort = null;
    this.cutOffCurrentTurn = null;
    this.recognizer.abort();
    this.speaker.cancel();
    this.stopTimer();
    this.typedQueue = [];
    this.interim.set('');
    this.idleSecondsLeft.set(null);
    this.endReason.set(reason);
    this.state.set('idle');
  }

  /** Cuts the assistant off mid-answer and goes straight back to listening. */
  interrupt(): void {
    this.cutOffCurrentTurn?.();
  }

  /** Sends a typed message as the caller's next turn, interrupting the assistant if needed. */
  sendText(text: string): void {
    const trimmed = text.trim();
    if (!trimmed || !this.inCall()) {
      return;
    }
    if (this.typedReceiver) {
      this.typedReceiver(trimmed);
    } else {
      this.typedQueue.push(trimmed);
    }
  }

  private async runCallLoop(callId: number, carried: CallerInput | null): Promise<void> {
    let next = carried;

    while (this.isCurrent(callId)) {
      let input = next;
      next = null;
      if (!input) {
        this.state.set('listening');
        this.interim.set('');
        input = await this.listenOnce({
          useMic: true,
          idleAt: this.lastCallerActivity + IDLE_TIMEOUT_SECONDS * 1000,
          onCallerActive: () => (this.lastCallerActivity = Date.now()),
        });
      }

      if (!this.isCurrent(callId)) {
        return;
      }
      this.interim.set('');

      switch (input.kind) {
        case 'stopped':
          return;
        case 'error':
          this.error.set(errorMessage(input.error));
          this.hangUp('error');
          return;
        case 'idle':
          await this.sayAndHangUp(callId, IDLE_GOODBYE, 'idle');
          return;
        case 'silence':
          continue; // Keep the line open; the idle deadline keeps counting.
      }

      this.lastCallerActivity = Date.now();
      this.addTurn('user', input.text, { typed: input.typed });
      const history = this.historyForApi();

      const outcome = await this.assistantTurn(callId, (signal) =>
        this.api.streamReply(this.callLogId, history, signal),
      );
      this.lastCallerActivity = Date.now();

      if (!this.isCurrent(callId)) {
        return;
      }
      if (outcome.kind === 'interrupted') {
        next = outcome.input.kind === 'text' ? outcome.input : null;
      } else if (outcome.endCall) {
        this.hangUp('goodbye');
        return;
      }
    }
  }

  /**
   * Streams and speaks one assistant reply while watching for the caller to cut in. Resolves
   * when the reply has been fully spoken, or — if interrupted — with what the caller said.
   */
  private async assistantTurn(
    callId: number,
    source: (signal: AbortSignal) => AsyncIterable<ReplyEvent>,
  ): Promise<TurnOutcome> {
    this.state.set('thinking');
    this.error.set(null);

    const replyAbort = new AbortController();
    const watchStop = new AbortController();
    const replyId = this.addTurn('assistant', '');
    const sentences = new SentenceBuffer();
    let reply = '';
    let endCall = false;
    let farewell: string | null = null;
    let interrupted = false;

    const cutOff = () => {
      if (interrupted || !this.isCurrent(callId)) {
        return;
      }
      interrupted = true;
      replyAbort.abort();
      this.speaker.cancel();
      this.state.set('listening');
    };
    this.cutOffCurrentTurn = () => {
      // Manual interrupt: nothing was said yet, so hand over to the normal listening loop.
      cutOff();
      watchStop.abort();
    };

    const bargeIn = this.watchForCaller(callId, watchStop.signal, () => interrupted, {
      isEcho: (heard) => isLikelyEcho(heard, reply),
      onCallerActive: cutOff,
    });

    const say = (sentence: string) => {
      if (!interrupted && this.isCurrent(callId)) {
        this.state.set('speaking');
        this.speaker.enqueue(sentence);
      }
    };

    try {
      for await (const event of source(replyAbort.signal)) {
        if (interrupted) {
          break;
        }
        if (event.type === 'text') {
          reply += event.text;
          this.updateTurn(replyId, { content: reply });
          sentences.push(event.text).forEach(say);
        } else if (event.type === 'action' && event.name === 'end_call') {
          endCall = true;
          const argument = event.arguments['farewell'];
          farewell = typeof argument === 'string' && argument.trim() ? argument.trim() : null;
        } else if (event.type === 'error') {
          this.error.set(event.message);
        }
      }
      if (!interrupted) {
        const rest = sentences.flush();
        if (rest) {
          say(rest);
        }
      }
    } catch (err) {
      if (!replyAbort.signal.aborted && this.isCurrent(callId)) {
        this.error.set(errorMessage(err));
        if (!reply.trim()) {
          reply = CALL_ERROR_REPLY;
          this.updateTurn(replyId, { content: reply });
          say(reply);
        }
      }
    }

    // The model may end the call with a bare tool call; never hang up without a goodbye.
    if (endCall && !interrupted && !reply.trim()) {
      reply = farewell ?? CALL_FAREWELL;
      this.updateTurn(replyId, { content: reply });
      say(reply);
    }

    await this.speaker.whenIdle();
    this.cutOffCurrentTurn = null;
    if (!interrupted) {
      watchStop.abort();
    }
    const callerInput = await bargeIn;

    if (!reply.trim()) {
      this.turns.update((turns) => turns.filter((turn) => turn.id !== replyId));
    } else if (interrupted) {
      this.updateTurn(replyId, { interrupted: true });
    }

    return interrupted
      ? { kind: 'interrupted', input: callerInput }
      : { kind: 'completed', endCall };
  }

  /**
   * Listens for the caller during an assistant turn: by voice when `voiceInterrupt` is on
   * (sessions restart as they time out or only hear echo) and always by typed text.
   */
  private async watchForCaller(
    callId: number,
    stop: AbortSignal,
    isInterrupted: () => boolean,
    options: Pick<ListenOptions, 'isEcho' | 'onCallerActive'>,
  ): Promise<CallerInput> {
    let useMic = this.voiceInterrupt();
    while (this.isCurrent(callId) && !stop.aborted) {
      const input = await this.listenOnce({ ...options, useMic, stop });
      if (input.kind === 'text' || input.kind === 'stopped') {
        return input;
      }
      if (input.kind === 'error') {
        useMic = false; // Mic trouble shouldn't break the reply; typed input still works.
        continue;
      }
      if (isInterrupted()) {
        return { kind: 'silence' }; // The caller cut in but the final transcript came back empty.
      }
    }
    return { kind: 'stopped' };
  }

  /** Waits for one caller input: speech (one recognition session), typed text, idle or stop. */
  private listenOnce(options: ListenOptions): Promise<CallerInput> {
    const queued = this.typedQueue.shift();
    if (queued !== undefined) {
      options.onCallerActive?.();
      return Promise.resolve({ kind: 'text', text: queued, typed: true });
    }

    return new Promise((resolve) => {
      const sessionCancel = new AbortController();
      const stopSignals = [this.callAbort?.signal, options.stop].filter(
        (signal): signal is AbortSignal => signal !== undefined,
      );
      let settled = false;
      let callerStarted = false;
      let idleTimer: ReturnType<typeof setTimeout> | undefined;

      const markCallerActive = () => {
        if (!callerStarted) {
          callerStarted = true;
          options.onCallerActive?.();
        }
      };
      const finish = (input: CallerInput) => {
        if (settled) {
          return;
        }
        settled = true;
        sessionCancel.abort();
        clearTimeout(idleTimer);
        stopSignals.forEach((signal) => signal.removeEventListener('abort', onStop));
        if (this.typedReceiver === onTyped) {
          this.typedReceiver = null;
        }
        resolve(input);
      };
      const onStop = () => finish({ kind: 'stopped' });
      const onTyped = (text: string) => {
        markCallerActive();
        finish({ kind: 'text', text, typed: true });
      };

      if (stopSignals.some((signal) => signal.aborted)) {
        finish({ kind: 'stopped' });
        return;
      }
      stopSignals.forEach((signal) => signal.addEventListener('abort', onStop, { once: true }));
      this.typedReceiver = onTyped;

      if (options.idleAt !== undefined) {
        idleTimer = setTimeout(() => {
          if (!callerStarted) {
            finish({ kind: 'idle' });
          }
        }, Math.max(0, options.idleAt - Date.now()));
      }

      if (!options.useMic) {
        return;
      }

      this.recognizer
        .listen((heard) => {
          if (settled) {
            return;
          }
          if (!callerStarted && (!heard || options.isEcho?.(heard))) {
            return;
          }
          markCallerActive();
          this.interim.set(heard);
        }, sessionCancel.signal)
        .then(
          (heard) => {
            if (heard && (callerStarted || !options.isEcho?.(heard))) {
              markCallerActive();
              finish({ kind: 'text', text: heard, typed: false });
            } else {
              finish({ kind: 'silence' });
            }
          },
          (error: unknown) => finish({ kind: 'error', error }),
        );
    });
  }

  private async sayAndHangUp(callId: number, text: string, reason: CallEndReason): Promise<void> {
    this.state.set('speaking');
    this.addTurn('assistant', text);
    this.speaker.enqueue(text);
    await this.speaker.whenIdle();
    if (this.isCurrent(callId)) {
      this.hangUp(reason);
    }
  }

  private historyForApi(): ChatTurn[] {
    return this.turns()
      .filter((turn) => turn.content.trim())
      .slice(-MAX_HISTORY_TURNS)
      .map(({ role, content }) => ({ role, content }));
  }

  private isCurrent(callId: number): boolean {
    return callId === this.callId && this.inCall();
  }

  private addTurn(role: ChatRole, content: string, extra: Partial<CallTurn> = {}): number {
    const id = this.nextTurnId++;
    this.turns.update((turns) => [...turns, { ...extra, id, role, content }]);
    return id;
  }

  private updateTurn(id: number, changes: Partial<CallTurn>): void {
    this.turns.update((turns) =>
      turns.map((turn) => (turn.id === id ? { ...turn, ...changes } : turn)),
    );
  }

  private startTimer(): void {
    this.stopTimer();
    this.elapsedSeconds.set(0);
    this.idleSecondsLeft.set(null);
    this.timer = setInterval(() => {
      this.elapsedSeconds.update((s) => s + 1);
      this.updateIdleCountdown();
    }, 1000);
  }

  private updateIdleCountdown(): void {
    if (this.state() !== 'listening' || this.interim()) {
      this.idleSecondsLeft.set(null);
      return;
    }
    const idleAt = this.lastCallerActivity + IDLE_TIMEOUT_SECONDS * 1000;
    const left = Math.ceil((idleAt - Date.now()) / 1000);
    this.idleSecondsLeft.set(left <= IDLE_WARNING_SECONDS ? Math.max(0, left) : null);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}

async function* fixedReply(text: string): AsyncGenerator<ReplyEvent> {
  yield { type: 'text', text };
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
