using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.Conversation;

/// <summary>
/// Deterministic backstop for ending the call. Small models often answer "Bye!" without calling
/// the <c>end_call</c> tool, so a short caller utterance containing a farewell phrase also ends
/// the call.
/// </summary>
public sealed partial class FarewellDetector
{
    private static readonly string[] DefaultPhrases =
    [
        "bye", "goodbye", "good bye", "see you", "talk to you later", "that's all", "that is all",
        "hang up", "end the call", "end call", "quit", "i'm done", "i am done", "no more questions",
    ];

    // "no" is deliberately absent: "No, that's all, bye" is a farewell.
    private static readonly HashSet<string> Negations =
        ["not", "don't", "dont", "never", "won't", "can't", "cannot", "didn't", "shouldn't"];

    /// <summary>Farewells are short; long utterances merely mentioning "quit" are real requests.</summary>
    private const int MaxWords = 12;

    private const int NegationWindow = 3;

    private readonly string[][] _phrases;

    public FarewellDetector(IOptions<AssistantOptions> options)
    {
        var phrases = options.Value.EndCallPhrases is { Count: > 0 } configured
            ? configured
            : (IEnumerable<string>)DefaultPhrases;
        _phrases = phrases.Select(Tokenize).Where(p => p.Length > 0).ToArray();
    }

    public bool IsFarewell(string utterance)
    {
        var words = Tokenize(utterance);
        if (words.Length == 0 || words.Length > MaxWords)
        {
            return false;
        }

        for (var i = 0; i < words.Length; i++)
        {
            foreach (var phrase in _phrases)
            {
                if (MatchesAt(words, i, phrase) && !IsNegated(words, i))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool MatchesAt(string[] words, int start, string[] phrase) =>
        start + phrase.Length <= words.Length
        && phrase.Select((word, offset) => words[start + offset] == word).All(match => match);

    private static bool IsNegated(string[] words, int phraseStart) =>
        words[Math.Max(0, phraseStart - NegationWindow)..phraseStart].Any(Negations.Contains);

    private static string[] Tokenize(string text) =>
        WordSeparator().Split(text.ToLowerInvariant().Replace('’', '\''))
            .Select(word => word.Trim('\''))
            .Where(word => word.Length > 0)
            .ToArray();

    [GeneratedRegex("[^a-z']+")]
    private static partial Regex WordSeparator();
}
