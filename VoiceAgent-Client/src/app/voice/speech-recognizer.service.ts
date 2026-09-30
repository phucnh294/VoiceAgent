import { Injectable } from '@angular/core';

import {
  SpeechRecognitionConstructor,
  SpeechRecognitionLike,
} from './speech-recognition.types';
import { CALL_LANGUAGE } from './voice.config';

/** Recognition errors that will not go away by simply listening again. */
const FATAL_ERRORS: Record<string, string> = {
  'not-allowed': 'Microphone access was denied. Allow the microphone and start the call again.',
  'service-not-allowed': 'Speech recognition is blocked in this browser.',
  'audio-capture': 'No microphone was found.',
  network: 'Speech recognition needs an internet connection in this browser.',
};

/** Pause before reporting a session that failed to start, so callers can't spin in a hot loop. */
const START_FAILURE_BACKOFF_MS = 300;

/** Speech-to-text via the browser's Web Speech API (Chrome / Edge desktop). */
@Injectable({ providedIn: 'root' })
export class SpeechRecognizerService {
  private readonly ctor: SpeechRecognitionConstructor | undefined = (() => {
    const w = window as unknown as Record<string, SpeechRecognitionConstructor | undefined>;
    return w['SpeechRecognition'] ?? w['webkitSpeechRecognition'];
  })();

  private active: SpeechRecognitionLike | null = null;
  /** Settles when the most recent session has fully ended; the browser allows one at a time. */
  private lastSessionEnded: Promise<void> = Promise.resolve();

  get isSupported(): boolean {
    return this.ctor !== undefined;
  }

  /**
   * Listens for one caller utterance. The browser ends the session on its own once the caller
   * pauses, which is what gives the call its turn-taking. Resolves with the final transcript — an
   * empty string if nothing was said or the session was aborted — and rejects only on errors that
   * retrying cannot fix. Aborting `cancel` stops the session, or keeps it from starting at all.
   */
  async listen(onInterim: (text: string) => void, cancel?: AbortSignal): Promise<string> {
    if (!this.ctor) {
      throw new Error('Speech recognition is not supported in this browser.');
    }

    this.abort();
    await this.lastSessionEnded;
    if (cancel?.aborted) {
      return '';
    }

    const recognition = new this.ctor();
    recognition.lang = CALL_LANGUAGE;
    recognition.continuous = false;
    recognition.interimResults = true;
    recognition.maxAlternatives = 1;

    let markEnded: () => void = () => {};
    this.lastSessionEnded = new Promise((resolve) => (markEnded = resolve));

    return new Promise((resolve, reject) => {
      let finalText = '';
      let fatal: Error | null = null;

      recognition.onresult = (event) => {
        let interim = '';
        for (let i = event.resultIndex; i < event.results.length; i++) {
          const result = event.results[i];
          if (result.isFinal) {
            finalText += result[0].transcript;
          } else {
            interim += result[0].transcript;
          }
        }
        onInterim(`${finalText}${interim}`.trim());
      };

      recognition.onerror = (event) => {
        // 'no-speech' and 'aborted' are normal during a call; onend resolves with ''.
        const message = FATAL_ERRORS[event.error];
        if (message) {
          fatal = new Error(message);
        }
      };

      const onCancel = () => recognition.abort();
      cancel?.addEventListener('abort', onCancel, { once: true });

      recognition.onend = () => {
        cancel?.removeEventListener('abort', onCancel);
        if (this.active === recognition) {
          this.active = null;
        }
        markEnded();
        if (fatal) {
          reject(fatal);
        } else {
          resolve(finalText.trim());
        }
      };

      this.active = recognition;
      try {
        recognition.start();
      } catch (err) {
        console.warn('Speech recognition failed to start', err);
        cancel?.removeEventListener('abort', onCancel);
        this.active = null;
        markEnded();
        setTimeout(() => resolve(''), START_FAILURE_BACKOFF_MS);
      }
    });
  }

  /** Stops listening immediately; the pending `listen()` resolves with what was heard so far. */
  abort(): void {
    this.active?.abort();
  }
}
