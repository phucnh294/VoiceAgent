import { Injectable, signal } from '@angular/core';

import { VoicePlayback } from '../settings/settings.models';
import { CALL_LANGUAGE } from './voice.config';

const DEFAULT_PLAYBACK: VoicePlayback = { provider: 'browser', browserVoiceName: null, browserRate: 1 };

/**
 * Speaks the assistant's reply one sentence at a time, with Kokoro (human-sounding, via
 * `POST /api/speech`) or the browser's built-in `speechSynthesis`.
 *
 * Sentences are queued on a promise chain rather than the browser's own queue so that `cancel()`
 * reliably drops everything pending. With Kokoro, each sentence's audio is requested as soon as it
 * is queued, so the next sentence is usually ready before the current one finishes playing.
 */
@Injectable({ providedIn: 'root' })
export class SpeechSpeakerService {
  /** Set when Kokoro failed and the browser voice was used instead; cleared on the next success. */
  readonly warning = signal<string | null>(null);

  private playback: VoicePlayback = DEFAULT_PLAYBACK;
  private tail: Promise<void> = Promise.resolve();
  private generation = 0;
  private readonly pendingFetches = new Set<AbortController>();
  private stopCurrent: (() => void) | null = null;
  // Chrome garbage-collects in-flight utterances and then never fires `onend`; hold a reference.
  private current: SpeechSynthesisUtterance | null = null;

  get isSupported(): boolean {
    return 'speechSynthesis' in window;
  }

  /** Applies voice settings from the next sentence on. */
  configure(playback: VoicePlayback): void {
    this.playback = playback;
  }

  /** Queues a sentence to be spoken after anything already queued. */
  enqueue(text: string): void {
    const generation = this.generation;
    const audio = this.playback.provider === 'kokoro' ? this.fetchAudio(text) : null;
    this.tail = this.tail.then(() => this.play(text, audio, generation));
  }

  /** Resolves once everything queued so far has been spoken (or cancelled). */
  whenIdle(): Promise<void> {
    return this.tail;
  }

  /** Stops the current sentence and drops everything queued. */
  cancel(): void {
    this.generation++;
    this.tail = Promise.resolve();
    this.pendingFetches.forEach((controller) => controller.abort());
    this.pendingFetches.clear();
    this.stopCurrent?.();
    window.speechSynthesis.cancel();
  }

  private async play(text: string, audio: Promise<Blob | null> | null, generation: number): Promise<void> {
    if (generation !== this.generation) {
      return;
    }
    if (audio) {
      const blob = await audio;
      if (generation !== this.generation) {
        return;
      }
      if (blob) {
        return this.playBlob(blob);
      }
    }
    return this.speakWithBrowser(text);
  }

  private fetchAudio(text: string): Promise<Blob | null> {
    const controller = new AbortController();
    this.pendingFetches.add(controller);
    return fetch('/api/speech', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text }),
      signal: controller.signal,
    })
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}: ${await response.text()}`);
        }
        this.warning.set(null);
        return response.blob();
      })
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          console.warn('Kokoro speech failed; using the browser voice', err);
          this.warning.set('The Kokoro voice is unavailable, so the browser voice is being used.');
        }
        return null;
      })
      .finally(() => this.pendingFetches.delete(controller));
  }

  private playBlob(blob: Blob): Promise<void> {
    return new Promise((resolve) => {
      const url = URL.createObjectURL(blob);
      const audio = new Audio(url);
      const done = () => {
        audio.onended = null;
        audio.onerror = null;
        if (this.stopCurrent === stop) {
          this.stopCurrent = null;
        }
        URL.revokeObjectURL(url);
        resolve();
      };
      const stop = () => {
        audio.pause();
        done();
      };
      audio.onended = done;
      audio.onerror = done;
      this.stopCurrent = stop;
      audio.play().catch(done);
    });
  }

  private speakWithBrowser(text: string): Promise<void> {
    return new Promise((resolve) => {
      const utterance = new SpeechSynthesisUtterance(text);
      utterance.lang = CALL_LANGUAGE;
      utterance.rate = this.playback.browserRate;
      const voice = window.speechSynthesis
        .getVoices()
        .find((candidate) => candidate.name === this.playback.browserVoiceName);
      if (voice) {
        utterance.voice = voice;
        utterance.lang = voice.lang;
      }
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
