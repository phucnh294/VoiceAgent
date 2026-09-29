import { Injectable } from '@angular/core';

import { CALL_LANGUAGE } from './voice.config';

/**
 * Text-to-speech via the browser's speechSynthesis, fed one sentence at a time.
 *
 * Sentences are queued on a promise chain rather than the browser's own queue so that
 * `cancel()` reliably drops everything pending, and so short utterances avoid Chrome cutting
 * off long ones after ~15 seconds.
 */
@Injectable({ providedIn: 'root' })
export class SpeechSpeakerService {
  private tail: Promise<void> = Promise.resolve();
  private generation = 0;
  // Chrome garbage-collects in-flight utterances and then never fires `onend`; hold a reference.
  private current: SpeechSynthesisUtterance | null = null;

  get isSupported(): boolean {
    return 'speechSynthesis' in window;
  }

  /** Queues a sentence to be spoken after anything already queued. */
  enqueue(text: string): void {
    const generation = this.generation;
    this.tail = this.tail.then(() => this.speakNow(text, generation));
  }

  /** Resolves once everything queued so far has been spoken (or cancelled). */
  whenIdle(): Promise<void> {
    return this.tail;
  }

  /** Stops the current sentence and drops everything queued. */
  cancel(): void {
    this.generation++;
    this.tail = Promise.resolve();
    window.speechSynthesis.cancel();
  }

  private speakNow(text: string, generation: number): Promise<void> {
    if (generation !== this.generation) {
      return Promise.resolve();
    }

    return new Promise((resolve) => {
      const utterance = new SpeechSynthesisUtterance(text);
      utterance.lang = CALL_LANGUAGE;
      const done = () => {
        if (this.current === utterance) {
          this.current = null;
        }
        resolve();
      };
      // A cancel() fires onerror ('interrupted' / 'canceled'); either way this sentence is over.
      utterance.onend = done;
      utterance.onerror = done;
      this.current = utterance;
      window.speechSynthesis.speak(utterance);
    });
  }
}
