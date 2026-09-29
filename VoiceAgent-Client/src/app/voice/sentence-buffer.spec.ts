import { SentenceBuffer, cleanForSpeech } from './sentence-buffer';

describe('SentenceBuffer', () => {
  it('releases a sentence once whitespace follows its punctuation', () => {
    const buffer = new SentenceBuffer();
    expect(buffer.push('Hello there.')).toEqual([]);
    expect(buffer.push(' How')).toEqual(['Hello there.']);
    expect(buffer.push(' are you?')).toEqual([]);
    expect(buffer.flush()).toBe('How are you?');
  });

  it('does not split decimal numbers', () => {
    const buffer = new SentenceBuffer();
    expect(buffer.push('It costs 3.5 dollars. ')).toEqual(['It costs 3.5 dollars.']);
  });

  it('splits on line breaks', () => {
    const buffer = new SentenceBuffer();
    expect(buffer.push('First line\nSecond')).toEqual(['First line']);
    expect(buffer.flush()).toBe('Second');
  });

  it('returns null on flush when nothing is left', () => {
    const buffer = new SentenceBuffer();
    buffer.push('Done! ');
    expect(buffer.flush()).toBeNull();
  });
});

describe('cleanForSpeech', () => {
  it('strips markdown symbols and collapses whitespace', () => {
    expect(cleanForSpeech('**Sure**,   `here`  # you go')).toBe('Sure, here you go');
  });
});
