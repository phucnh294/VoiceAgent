/**
 * Collects streamed text fragments and releases whole sentences, so speech can start on the
 * first sentence while the rest of the reply is still arriving. A sentence ends at `.`, `!` or
 * `?` followed by whitespace (so "3.5" is not split), or at a line break.
 */
export class SentenceBuffer {
  private buffer = '';

  push(fragment: string): string[] {
    this.buffer += fragment;

    const boundary = /([.!?]+)\s+|\n+/g;
    const sentences: string[] = [];
    let start = 0;
    let match: RegExpExecArray | null;

    while ((match = boundary.exec(this.buffer)) !== null) {
      const end = match.index + (match[1]?.length ?? 0);
      const sentence = cleanForSpeech(this.buffer.slice(start, end));
      if (sentence) {
        sentences.push(sentence);
      }
      start = match.index + match[0].length;
    }

    this.buffer = this.buffer.slice(start);
    return sentences;
  }

  /** Returns whatever is left once the stream has ended. */
  flush(): string | null {
    const rest = cleanForSpeech(this.buffer);
    this.buffer = '';
    return rest || null;
  }
}

/** Strips markdown symbols the model may emit despite the prompt; they sound awful read aloud. */
export function cleanForSpeech(text: string): string {
  return text
    .replace(/[*_#`>~]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}
