using Athena.Ingestion.Extraction;
using Azure.AI.DocumentIntelligence;

namespace Athena.Tests.Ingestion;

public sealed class FormulaRestorerTests
{
    // Azure's reader models have internal ctors; the SDK's model factory is the test seam.
    private static DocumentSpan Span(int offset, int length)
        => DocumentIntelligenceModelFactory.DocumentSpan(offset, length);

    private static DocumentFormula Formula(DocumentFormulaKind kind, string value, int offset)
        => DocumentIntelligenceModelFactory.DocumentFormula(
            kind, value, polygon: [], span: Span(offset, value.Length), confidence: 0.9f);

    [Fact]
    public void Restore_SplicesInlineAndDisplayLatexInOrder()
    {
        // Element covers offsets [0, 100); both formulas start inside it.
        string restored = FormulaRestorer.Restore(
            "where :formula: is marginalized as :formula:",
            [Span(0, 100)],
            [
                Formula(DocumentFormulaKind.Display, @"\sum_{z} p_{\eta}(z|x)", offset: 40),
                Formula(DocumentFormulaKind.Inline, @"p_{\eta}(z|x)", offset: 10),
            ]);

        // Substitution order follows span offsets (inline at 10 first), not input order.
        Assert.Equal(
            @"where \( p_{\eta}(z|x) \) is marginalized as \[ \sum_{z} p_{\eta}(z|x) \]",
            restored);
    }

    [Fact]
    public void Restore_IgnoresFormulasOutsideElementSpans()
    {
        string restored = FormulaRestorer.Restore(
            "prose :formula: prose",
            [Span(0, 30)],
            [Formula(DocumentFormulaKind.Inline, "x^2", offset: 500)]);

        // The other element's formula must not leak in; the placeholder survives as evidence.
        Assert.Equal("prose :formula: prose", restored);
    }

    [Fact]
    public void Restore_KeepsPlaceholderWhenLatexIsEmpty()
    {
        string restored = FormulaRestorer.Restore(
            "a :formula: b :formula: c",
            [Span(0, 50)],
            [
                Formula(DocumentFormulaKind.Inline, " ", offset: 2),
                Formula(DocumentFormulaKind.Inline, "y_i", offset: 14),
            ]);

        // Empty slot consumed without shifting the later substitution.
        Assert.Equal(@"a :formula: b \( y_i \) c", restored);
    }

    [Fact]
    public void Restore_NoPlaceholders_ReturnsContentUnchanged()
    {
        const string text = "plain prose without math";

        Assert.Same(text, FormulaRestorer.Restore(
            text, [Span(0, 100)],
            [Formula(DocumentFormulaKind.Inline, "x", offset: 3)]));
    }
}
