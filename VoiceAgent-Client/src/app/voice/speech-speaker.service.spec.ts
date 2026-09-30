import { SpeechSpeakerService } from './speech-speaker.service';

/** Plays instantly-ending fake audio and records which sentence each clip was. */
class FakeAudio {
  static played: string[] = [];
  onended: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(private readonly url: string) {}

  play(): Promise<void> {
    FakeAudio.played.push(this.url);
    setTimeout(() => this.onended?.(), 5);
    return Promise.resolve();
  }

  pause(): void {}
}

describe('SpeechSpeakerService (Kokoro)', () => {
  let spoken: string[];
  let originals: { fetch: typeof fetch; Audio: unknown; create: unknown; revoke: unknown };

  beforeEach(() => {
    FakeAudio.played = [];
    spoken = [];
    originals = {
      fetch: globalThis.fetch,
      Audio: (globalThis as Record<string, unknown>)['Audio'],
      create: URL.createObjectURL,
      revoke: URL.revokeObjectURL,
    };
    (globalThis as Record<string, unknown>)['Audio'] = FakeAudio;
    URL.createObjectURL = (blob: Blob) => (blob as Blob & { label: string }).label;
    URL.revokeObjectURL = () => {};
    (window as unknown as Record<string, unknown>)['speechSynthesis'] = {
      getVoices: () => [],
      cancel: () => {},
      speak: (utterance: { text: string; onend: () => void }) => {
        spoken.push(utterance.text);
        setTimeout(() => utterance.onend(), 1);
      },
    };
    (globalThis as Record<string, unknown>)['SpeechSynthesisUtterance'] = class {
      lang = '';
      rate = 1;
      voice: unknown = null;
      onend: (() => void) | null = null;
      onerror: (() => void) | null = null;
      constructor(readonly text: string) {}
    };
  });

  afterEach(() => {
    globalThis.fetch = originals.fetch;
    (globalThis as Record<string, unknown>)['Audio'] = originals.Audio;
    URL.createObjectURL = originals.create as typeof URL.createObjectURL;
    URL.revokeObjectURL = originals.revoke as typeof URL.revokeObjectURL;
  });

  function fakeKokoro(delays: Record<string, number>, failing: string[] = []): void {
    globalThis.fetch = ((_url: string, init: RequestInit) => {
      const text = (JSON.parse(init.body as string) as { text: string }).text;
      return new Promise((resolve) =>
        setTimeout(() => {
          const blob = Object.assign(new Blob([text]), { label: text });
          resolve({
            ok: !failing.includes(text),
            status: failing.includes(text) ? 502 : 200,
            text: () => Promise.resolve('down'),
            blob: () => Promise.resolve(blob),
          } as unknown as Response);
        }, delays[text] ?? 1),
      );
    }) as typeof fetch;
  }

  it('plays sentences in order even when a later one is synthesized first', async () => {
    fakeKokoro({ 'First.': 40, 'Second.': 1 });
    const speaker = new SpeechSpeakerService();
    speaker.configure({ provider: 'kokoro', browserVoiceName: null, browserRate: 1 });

    speaker.enqueue('First.');
    speaker.enqueue('Second.');
    await speaker.whenIdle();

    expect(FakeAudio.played).toEqual(['First.', 'Second.']);
  });

  it('drops queued sentences on cancel', async () => {
    fakeKokoro({ 'One.': 20, 'Two.': 20 });
    const speaker = new SpeechSpeakerService();
    speaker.configure({ provider: 'kokoro', browserVoiceName: null, browserRate: 1 });

    speaker.enqueue('One.');
    speaker.enqueue('Two.');
    speaker.cancel();
    await speaker.whenIdle();
    await new Promise((resolve) => setTimeout(resolve, 60));

    expect(FakeAudio.played).toEqual([]);
  });

  it('falls back to the browser voice when Kokoro fails', async () => {
    fakeKokoro({}, ['Hello.']);
    const speaker = new SpeechSpeakerService();
    speaker.configure({ provider: 'kokoro', browserVoiceName: null, browserRate: 1 });

    speaker.enqueue('Hello.');
    await speaker.whenIdle();

    expect(spoken).toEqual(['Hello.']);
    expect(speaker.warning()).not.toBeNull();
  });
});
