import { ECHO_WORD_OVERLAP } from './voice.config';

/**
 * True when `heard` is probably the microphone picking up the assistant's own voice rather than
 * the caller talking over it: most of its words also appear in what the assistant is saying.
 */
export function isLikelyEcho(heard: string, assistantText: string): boolean {
  const heardWords = words(heard);
  if (heardWords.length === 0) {
    return true;
  }
  const spoken = new Set(words(assistantText));
  if (spoken.size === 0) {
    return false;
  }
  const overlap = heardWords.filter((word) => spoken.has(word)).length;
  return overlap / heardWords.length >= ECHO_WORD_OVERLAP;
}

function words(text: string): string[] {
  return text
    .toLowerCase()
    .replace(/’/g, "'")
    .split(/[^a-z0-9']+/)
    .filter((word) => word.length > 0);
}
