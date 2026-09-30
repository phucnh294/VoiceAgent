/** BCP-47 language used for both recognition and speech synthesis. */
export const CALL_LANGUAGE = 'en-US';

/**
 * Spoken as soon as the call connects. The greeting is normally set in the Settings panel; this
 * is only used when the API's call settings can't be loaded.
 */
export const CALL_GREETING = 'Hello, thanks for calling. How can I help you today?';

/** Spoken when a turn fails, so the caller is never left in silence. */
export const CALL_ERROR_REPLY = "Sorry, I'm having trouble answering right now. Could you say that again?";

/** Spoken before hanging up when the model ends the call without saying goodbye itself. */
export const CALL_FAREWELL = 'Thank you for calling. Goodbye!';

/** Only the most recent turns are sent to the API. */
export const MAX_HISTORY_TURNS = 20;

/** The call ends after this long without the caller saying or typing anything. */
export const IDLE_TIMEOUT_SECONDS = 60;

/** A countdown is shown during the last seconds before an idle hang-up. */
export const IDLE_WARNING_SECONDS = 15;

/** Spoken before an idle hang-up. */
export const IDLE_GOODBYE = "I haven't heard from you for a while, so I'll end the call now. Goodbye!";

/**
 * While the assistant speaks, the microphone also picks up the assistant's own voice. Heard
 * speech whose words overlap the reply by at least this share counts as echo and doesn't
 * interrupt.
 */
export const ECHO_WORD_OVERLAP = 0.6;
