import { Component, computed, inject } from '@angular/core';

import { CallState, VoiceCallService } from './voice/voice-call.service';

const STATUS_LABELS: Record<CallState, string> = {
  idle: 'Ready to call',
  listening: 'Listening…',
  thinking: 'Thinking…',
  speaking: 'Speaking…',
};

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  standalone: false,
  styleUrl: './app.css',
})
export class App {
  protected readonly call = inject(VoiceCallService);

  protected readonly statusLabel = computed(() => STATUS_LABELS[this.call.state()]);

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
}
