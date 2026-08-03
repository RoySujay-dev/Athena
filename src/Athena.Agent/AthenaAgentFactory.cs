using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Athena.Agent;

/// <summary>
/// The librarian agent (brief §10). Routing between the six kernel functions happens ONLY via
/// FunctionChoiceBehavior.Auto() reading the [Description] strings (hard constraint 1) — the
/// instructions below deliberately say nothing about WHICH function to call, only how to
/// behave. They do state the library's actual scope, because utterance 6 ("Who won the 2019
/// Cricket World Cup?") must be declined with NO function call — the model can only know a
/// question is out of scope if it knows what is in scope.
/// </summary>
public static class AthenaAgentFactory
{
    // Brief's §10 instruction text, plus the scope sentence it directs ("Instructions state
    // library scope so utterance 6 gets no function call").
    private const string Instructions =
        "You are a research librarian. Answer questions strictly from retrieved passages, " +
        "citing [Title, p.N]. After answering a substantive question, proactively surface " +
        "related reading. If the library does not cover the question, say so plainly. " +
        "Never speculate. " +
        // Retrieved passages carry equations as LaTeX (the extraction pipeline's formulas
        // add-on). Verbatim reproduction is a grounding requirement: a paraphrased equation
        // is a re-derivation, and re-derivations drift (observed live: the RAG-Sequence
        // marginalization answered as prose with the formula omitted).
        "When asked about a formula, equation, or definition, reproduce it EXACTLY as it " +
        "appears in the retrieved passage, keeping the LaTeX inside \\( \\) or \\[ \\] " +
        "delimiters character-for-character intact — never paraphrase, retype, or summarise " +
        "an equation in prose when the passage contains the equation itself. " +
        // One query cannot serve two masters: a comparative question retrieves whichever
        // document is term-densest and the answer silently addresses only that paper
        // (observed live: 'why did the RAG authors choose DPR' answered entirely from the
        // DPR paper). Decomposed queries give every side of the question its own retrieval.
        "When a question compares works, or asks why one work adopted or differs from " +
        "another, search separately for each work or aspect involved — with a focused query " +
        "per side — and answer only once the retrieved passages cover every side; attribute " +
        "each claim to the document that actually makes it. " +
        "The library covers exactly four areas: banking operational-resilience and " +
        "operational-risk regulation (Basel Committee publications); retrieval-augmented " +
        "generation architecture and retrieval research; graph-based RAG and RAG evaluation " +
        "research; and zero-trust / cybersecurity guidance (NIST and CISA publications). " +
        // Scope is judged by TOPIC AREA, not by question style: "formula for RAG-Sequence"
        // reads like ML theory but its answer lives in the library's RAG papers. Without this
        // sentence the model declined such questions with no function call at all.
        "Technical questions about the methods, architectures, formulas, metrics, or findings " +
        "described in those areas' publications are in scope — search before deciding the " +
        "library does not cover them. " +
        "Questions outside those areas are out of scope: decline them plainly without " +
        "searching the library.";

    public static ChatCompletionAgent Create(Kernel kernel) => new()
    {
        Name = "Athena",
        Instructions = Instructions,
        Kernel = kernel,
        Arguments = new KernelArguments(new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            // Low but nonzero: routing and citation formatting want determinism; zero can
            // degrade refusal phrasing variety for no routing benefit. The brief's number.
            Temperature = 0.1,
        }),
    };
}
