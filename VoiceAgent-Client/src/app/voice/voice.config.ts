/** BCP-47 language used for both recognition and speech synthesis. */
export const CALL_LANGUAGE = 'en-US';

/** Spoken as soon as the call connects, and kept as the first assistant turn. */
export const CALL_GREETING = 'Hello, thanks for calling. How can I help you today?';

/** Spoken when a turn fails, so the caller is never left in silence. */
export const CALL_ERROR_REPLY = "Sorry, I'm having trouble answering right now. Could you say that again?";

/** Only the most recent turns are sent to the API. */
export const MAX_HISTORY_TURNS = 20;
