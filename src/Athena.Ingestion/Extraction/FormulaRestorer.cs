using System.Text;
using Azure.AI.DocumentIntelligence;

namespace Athena.Ingestion.Extraction;

/// <summary>
/// With the DI formulas add-on, layout text replaces every equation with a literal
/// <c>:formula:</c> placeholder and returns the LaTeX separately (per page). This splices the
/// LaTeX back into element text: candidate formulas are matched by span containment, ordered
/// by offset, and substituted into the element's placeholders in reading order. LaTeX is
/// wrapped in <c>\( … \)</c> / <c>\[ … \]</c> so downstream renderers (the chat UI's KaTeX)
/// can typeset it. Pure function — unit-tested directly.
/// </summary>
public static class FormulaRestorer
{
    private const string Placeholder = ":formula:";

    public static string Restore(string content, IReadOnlyList<DocumentSpan> elementSpans,
                                 IReadOnlyList<DocumentFormula> formulas)
    {
        if (formulas.Count == 0 || !content.Contains(Placeholder, StringComparison.Ordinal))
        {
            return content;
        }

        // Span containment (formula start inside one of the element's spans) rather than a
        // global queue: an element only ever consumes its own formulas, so a miscount in one
        // paragraph cannot shift every later substitution in the document.
        var candidates = formulas
            .Where(f => elementSpans.Any(s =>
                f.Span.Offset >= s.Offset && f.Span.Offset < s.Offset + s.Length))
            .OrderBy(f => f.Span.Offset)
            .ToList();

        var sb = new StringBuilder(content.Length);
        int position = 0, next = 0, index;
        while ((index = content.IndexOf(Placeholder, position, StringComparison.Ordinal)) >= 0)
        {
            sb.Append(content, position, index - position);
            if (next < candidates.Count && !string.IsNullOrWhiteSpace(candidates[next].Value))
            {
                DocumentFormula formula = candidates[next];
                sb.Append(formula.Kind == DocumentFormulaKind.Display
                    ? $"\\[ {formula.Value} \\]"
                    : $"\\( {formula.Value} \\)");
            }
            else
            {
                // No (or empty) LaTeX for this slot — keep the placeholder rather than
                // silently deleting the evidence that an equation stood here.
                sb.Append(Placeholder);
            }

            next++;
            position = index + Placeholder.Length;
        }

        sb.Append(content, position, content.Length - position);
        return sb.ToString();
    }
}
