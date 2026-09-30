import { isLikelyEcho } from './echo-filter';

describe('isLikelyEcho', () => {
  const reply = 'We are open from eight in the morning until six in the evening.';

  it('treats words the assistant is saying as echo', () => {
    expect(isLikelyEcho('open from eight in the morning', reply)).toBe(true);
  });

  it('treats new words from the caller as a real interruption', () => {
    expect(isLikelyEcho('wait what about Saturday', reply)).toBe(false);
    expect(isLikelyEcho('stop', reply)).toBe(false);
  });

  it('treats empty input as echo so it never interrupts', () => {
    expect(isLikelyEcho('   ', reply)).toBe(true);
  });

  it('never treats speech as echo before the assistant has said anything', () => {
    expect(isLikelyEcho('hello', '')).toBe(false);
  });
});
