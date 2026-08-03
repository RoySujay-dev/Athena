using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace Athena.Filters;

/// <summary>
/// Masks PII in rendered prompts BEFORE they leave the process for the model API (Part G).
/// A prompt-render filter is the one choke point every prompt function passes through, so the
/// masking cannot be forgotten by a new call site — same interception argument as the
/// grounding guard. Redaction is pattern-based (emails, phone numbers, card-like and SSN-like
/// digit runs); the corpus itself is public regulatory/research text, so the realistic PII
/// vector is the USER pasting personal data into a question, which is exactly what a rendered
/// prompt contains.
/// </summary>
public sealed partial class PiiRedactionFilter : IPromptRenderFilter
{
    public async Task OnPromptRenderAsync(PromptRenderContext context,
                                          Func<PromptRenderContext, Task> next)
    {
        await next(context);
        if (context.RenderedPrompt is { } prompt)
        {
            context.RenderedPrompt = Redact(prompt);
        }
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}")]
    private static partial Regex Email();

    // 13–16 digits allowing space/dash separators, anchored on digits at both ends so the
    // match never swallows trailing whitespace. Checked before the phone pattern so a card
    // number is not half-eaten as a phone number.
    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,15}\b")]
    private static partial Regex CardLike();

    // Phone-shaped: optional +, then 9–14 digits total with common separators. The 9-digit
    // floor keeps dates (8 digits) and document codes ("800-207") out of reach.
    [GeneratedRegex(@"(?<![\w.-])\+?\d(?:[ -.]?\(?\d\)?){8,13}(?!\w)")]
    private static partial Regex PhoneLike();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex SsnLike();

    internal static string Redact(string text)
    {
        text = Email().Replace(text, "[email redacted]");
        text = SsnLike().Replace(text, "[id redacted]");
        text = CardLike().Replace(text, "[number redacted]");
        text = PhoneLike().Replace(text, "[number redacted]");
        return text;
    }
}
