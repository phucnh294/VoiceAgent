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

/** Speech-to-text via the browser's Web Speech API (Chrome / Edge desktop). */
@Injectable({ providedIn: 'root' })
export class SpeechRecognizerService {
  private readonly ctor: SpeechRecognitionConstructor | undefined = (() => {
    const w = window as unknown as Record<string, SpeechRecognitionConstructor | undefined>;
    return w['SpeechRecognition'] ?? w['webkitSpeechRecognition'];
  })();

  private active: SpeechRecognitionLike | null = null;

  get isSupported(): boolean {
    return this.ctor !== undefined;
  }

  /**
   * Listens for one caller turn. The browser ends the session on its own once the caller pauses,
   * which is what gives the call its turn-taking. Resolves with the final transcript — an empty
   * string if nothing was said — and rejects only on errors that retrying cannot fix.
   */
  listen(onInterim: (text: string) => void): Promise<string> {
    if (!this.ctor) {
      return Promise.reject(new Error('Speech recognition is not supported in this browser.'));
    }

    const recognition = new this.ctor();
    recognition.lang = CALL_LANGUAGE;
    recognition.continuous = false;
    recognition.interimResults = true;
    recognition.maxAlternatives = 1;

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

      recognition.onend = () => {
        if (this.active === recognition) {
          this.active = null;
        }
        if (fatal) {
          reject(fatal);
        } else {
          resolve(finalText.trim());
        }
      };

      this.active = recognition;
      recognition.start();
    });
  }

  /** Stops listening immediately and discards anything heard so far. */
  abort(): void {
    this.active?.abort();
    this.active = null;
  }
}
