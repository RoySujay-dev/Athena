using System.ComponentModel;
using System.Text;
using Athena.Core.Records;
using Athena.Retrieval;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;

namespace Athena.Plugins;

/// <summary>
/// The QA plugin surface (brief §7.1/§8). Thin adapter: retrieval lives in HybridRetriever,
/// answering lives in prompts/answer.yaml — this class only shapes arguments and serialises
/// results for the model. The [Description] strings are the brief's verbatim: they are the
/// ENTIRE routing signal FunctionChoiceBehavior.Auto() sees (hard constraint 1), and the
/// answer_question description's final sentence exists purely to disambiguate it from
/// hybrid_search.
/// </summary>
public sealed class SearchPlugin
{
    // Loaded once per process from the build-copied prompt file. Static because the prompt is
    // immutable configuration, not per-session state.
    private static readonly Lazy<KernelFunction> AnswerFunction = new(() =>
        KernelFunctionYaml.FromPromptYaml(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "prompts", "answer.yaml"))));

    private readonly HybridRetriever _retriever;
    private readonly IRetrievedContextAccessor? _retrievedContext;
    private readonly PageReader? _pageReader;
    private readonly VectorStoreCollection<string, DocRecord>? _docs;

    /// <summary>
    /// DEVIATION from the brief's §7.1 ctor (documented in README): the optional accessor is
    /// how the §8.1 grounding guard later learns what was retrieved this turn without this
    /// plugin knowing the guard exists; the optional page reader backs read_pages, the
    /// location-addressed complement to similarity search; the optional doc catalog resolves
    /// user-facing identifiers ("d516") to DocIds before they become filters.
    /// </summary>
    public SearchPlugin(HybridRetriever retriever, IRetrievedContextAccessor? retrievedContext = null,
                        PageReader? pageReader = null,
                        VectorStoreCollection<string, DocRecord>? docs = null)
    {
        _retriever = retriever;
        _retrievedContext = retrievedContext;
        _pageReader = pageReader;
        _docs = docs;
    }

    [KernelFunction("hybrid_search")]
    [Description("Searches the research PDF library for passages relevant to a query. " +
                 "Returns passages with document title and page number. Use this whenever the " +
                 "user asks a factual question about the contents of the library.")]
    public async Task<string> HybridSearchAsync(
        [Description("Natural-language search query")] string query,
        [Description("Number of passages to return")] int topK = 6,
        [Description("Optional: restrict the search to one document — its library id (e.g. A1) " +
                     "or any identifier from its title (e.g. d516, RAPTOR)")]
            string? docId = null,
        CancellationToken ct = default)
    {
        if (docId is not null && !string.IsNullOrWhiteSpace(docId))
        {
            (docId, string? error) = await ResolveDocIdAsync(docId, ct);
            if (error is not null)
            {
                return error;
            }
        }

        IReadOnlyList<Passage> passages = await _retriever.RetrieveAsync(query, topK, docId, ct);
        _retrievedContext?.Set(passages);
        return passages.Count == 0 ? "No passages found." : FormatPassages(passages);
    }

    [KernelFunction("answer_question")]
    // Part E hardening beyond the brief's verbatim text: the final sentence is a NEGATIVE
    // boundary naming the competing function, per §10 — utterance 4 ("What should I read about
    // evaluating RAG systems?") contains a topic and looks like a question, and this sentence
    // is what keeps it out of answer_question. Description text is the only routing lever.
    [Description("Answers a factual question about the research library, grounded strictly in " +
                 "retrieved passages, with [Title, p.N] citations. Prefer this over hybrid_search " +
                 "when the user wants an answer rather than a list of passages. Do NOT use this " +
                 "when the user asks what they should read about a topic — that is a reading " +
                 "request, not a factual question; use recommend_for_query instead.")]
    public async Task<string> AnswerQuestionAsync(
        Kernel kernel,
        [Description("The user's question, restated as a standalone question")] string question,
        CancellationToken ct = default)
    {
        IReadOnlyList<Passage> passages = await _retriever.RetrieveAsync(question, topK: 6,
            docId: null, ct);
        _retrievedContext?.Set(passages);
        if (passages.Count == 0)
        {
            // Rule 2 without spending a model call: nothing retrieved is the one case we can
            // decide here. Every other insufficiency judgement belongs to the prompt.
            return "INSUFFICIENT_CONTEXT";
        }

        FunctionResult result = await AnswerFunction.Value.InvokeAsync(kernel, new KernelArguments
        {
            ["question"] = question,
            ["context"] = FormatPassages(passages),
        }, ct);
        return result.ToString();
    }

    [KernelFunction("read_pages")]
    // Location-addressed lookup, described in explicit contrast to the similarity functions:
    // a page number has no meaning in embedding space, so "page 16 of B1" must route HERE and
    // never to hybrid_search. Description text is the only routing lever (hard constraint 1).
    [Description("Returns the full text of a specific page or page range of ONE named document. " +
                 "Use when the user points at a document AND a page number or page range " +
                 "('page 16 of B1', 'pages 3-5 of the RAG paper') — including counting or " +
                 "listing things at that location. Do NOT use for topical questions where no " +
                 "page is named; those go to hybrid_search or answer_question.")]
    public async Task<string> ReadPagesAsync(
        [Description("The document — its library id (e.g. B1) or any identifier from its " +
                     "title (e.g. d516, RAPTOR)")] string docId,
        [Description("First page to read (1-based)")] int fromPage,
        [Description("Last page to read; omit or repeat fromPage for a single page")] int toPage = 0,
        CancellationToken ct = default)
    {
        if (_pageReader is null)
        {
            return "Page reading is not available in this host.";
        }

        (string? resolved, string? error) = await ResolveDocIdAsync(docId, ct);
        if (error is not null)
        {
            return error;
        }

        docId = resolved!;
        IReadOnlyList<Passage> passages = await _pageReader.ReadAsync(docId, fromPage, toPage, ct);
        _retrievedContext?.Set(passages);
        return passages.Count == 0
            ? $"No content found for document '{docId}' pages {fromPage}-{(toPage < fromPage ? fromPage : toPage)}. " +
              "Check the document id and that the document is ingested."
            : FormatPassages(passages);
    }

    /// <summary>
    /// Resolves a user-facing identifier to a catalog DocId via <see cref="DocIdResolver"/>.
    /// Without a catalog the identifier passes through unchanged (test hosts). Unresolved or
    /// ambiguous identifiers return an error TEXT rather than throwing — the model can relay
    /// it or retry without the filter, and a silent zero-match filter is exactly the failure
    /// this resolution exists to prevent.
    /// </summary>
    private async Task<(string? DocId, string? Error)> ResolveDocIdAsync(
        string identifier, CancellationToken ct)
    {
        if (_docs is null)
        {
            return (identifier, null);
        }

        var docs = new List<DocRecord>();
        await foreach (DocRecord doc in _docs.GetAsync(_ => true, top: int.MaxValue, cancellationToken: ct))
        {
            docs.Add(doc);
        }

        DocIdResolution resolution = DocIdResolver.Resolve(docs, identifier);
        if (resolution.Match is not null)
        {
            return (resolution.Match.DocId, null);
        }

        return resolution.Ambiguous.Count > 1
            ? (null, $"Multiple documents match '{identifier}': " +
                     string.Join("; ", resolution.Ambiguous.Select(d => $"{d.DocId} ({d.Title})")) +
                     ". Ask again with one of these ids.")
            : (null, $"No document with id or title '{identifier}' exists in the library.");
    }

    /// <summary>
    /// Each passage is headed by the exact [Title, p.N] tag the answer prompt is told to copy
    /// into citations — the format here and the format rule in answer.yaml are one contract.
    /// </summary>
    internal static string FormatPassages(IReadOnlyList<Passage> passages)
    {
        var sb = new StringBuilder();
        foreach (Passage passage in passages)
        {
            sb.AppendLine($"[{passage.Title}, p.{passage.PageNumber}] (doc: {passage.DocId})");
            sb.AppendLine(passage.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
