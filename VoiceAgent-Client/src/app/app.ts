import { Component, computed, inject, signal } from '@angular/core';

import { SpeechSpeakerService } from './voice/speech-speaker.service';
import { CallEndReason, CallState, VoiceCallService } from './voice/voice-call.service';

const STATUS_LABELS: Record<CallState, string> = {
  idle: 'Ready to call',
  listening: 'Listening…',
  thinking: 'Thinking…',
  speaking: 'Speaking…',
};

const END_LABELS: Record<CallEndReason, string> = {
  caller: 'Call ended',
  goodbye: 'Call ended — goodbye',
  idle: 'Call ended after no response',
  error: 'Call ended because of an error',
  closed: 'Call ended',
};

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  standalone: false,
  styleUrl: './app.css',
})
export class App {
  protected readonly call = inject(VoiceCallService);
  protected readonly speaker = inject(SpeechSpeakerService);
  protected readonly settingsOpen = signal(false);

  protected readonly statusLabel = computed(() => {
    const reason = this.call.endReason();
    if (this.call.state() === 'idle' && reason) {
      return END_LABELS[reason];
    }
    const idleLeft = this.call.idleSecondsLeft();
    if (idleLeft !== null) {
      return `No response — ending in ${idleLeft}s`;
    }
    return STATUS_LABELS[this.call.state()];
  });

  protected readonly duration = computed(() => {
    const total = this.call.elapsedSeconds();
    const minutes = Math.floor(total / 60).toString().padStart(2, '0');
    const seconds = (total % 60).toString().padStart(2, '0');
    return `${minutes}:${seconds}`;
  });

  /** Newest first: the transcript uses column-reverse so it stays pinned to the latest turn. */
  protected readonly turnsNewestFirst = computed(() => [...this.call.turns()].reverse());

  protected readonly canInterrupt = computed(
    () => this.call.state() === 'thinking' || this.call.state() === 'speaking',
  );

  protected send(input: HTMLInputElement): void {
    this.call.sendText(input.value);
    input.value = '';
  }

  protected toggleVoiceInterrupt(event: Event): void {
    this.call.voiceInterrupt.set((event.target as HTMLInputElement).checked);
  }
}
