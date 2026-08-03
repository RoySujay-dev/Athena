using System.Text.Json;
using Athena.Core.Records;
using Athena.Ingestion.Chunking;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Athena.Ingestion.Enrichment;

/// <summary>
/// Summary + topics via a Semantic Kernel prompt function (the brief's "wraps a prompt
/// function", §6.5). Deterministic-ish: temperature 0 and JSON response format.
/// </summary>
public sealed class SkDocumentSummariser : IDocumentSummariser
{
    // Trade-off: we summarise only the leading ~6k tokens of a document. For C3 (88 pages)
    // that skips most of the body — but the title, abstract and introduction carry the thesis,
    // and a 150-word summary cannot represent 88 pages anyway. The alternative (map-reduce
    // over every chunk) multiplies LLM cost per document for marginal summary gain.
    private const int MaxInputTokens = 6000;

    // The brief's §6.1 DocRecord contract: Summary <= 150 words, Topics 3-6 tags. The prompt
    // asks for both; these enforce them (see Parse).
    private const int MaxSummaryWords = 150;
    private const int MaxTopics = 6;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string PromptTemplate =
        """
        You are indexing a research/regulatory PDF library.

        Document title: {{$title}}

        Document text (may be truncated):
        ---
        {{$text}}
        ---

        Return a JSON object with exactly these fields:
        - "summary": a factual abstract of the document in AT MOST 150 words. State what the
          document is (regulation, survey, method paper, evaluation framework...), what it
          covers, and who published it if evident. No marketing language.
        - "topics": an array of 3 to 6 short topic tags (2-4 words each, lowercase) that
          distinguish this document within a library that spans banking regulation and
          retrieval-augmented generation research.
        """;

    private readonly KernelFunction _summarise;
    private readonly Kernel _kernel;
    private readonly ITokenCounter _tokenCounter;

    public SkDocumentSummariser(Kernel kernel, ITokenCounter tokenCounter)
    {
        _kernel = kernel;
        _tokenCounter = tokenCounter;
        _summarise = KernelFunctionFactory.CreateFromPrompt(
            PromptTemplate,
            new OpenAIPromptExecutionSettings
            {
                Temperature = 0,
                ResponseFormat = "json_object",
            },
            functionName: "summarise_document");
    }

    public async Task<DocumentSummary> SummariseAsync(
        DocumentMetadata meta, string documentText, CancellationToken ct = default)
    {
        FunctionResult result = await _summarise.InvokeAsync(_kernel, new KernelArguments
        {
            ["title"] = meta.Title,
            ["text"] = TruncateToTokenBudget(documentText),
        }, ct);

        return Parse(result.ToString());
    }

    private string TruncateToTokenBudget(string text)
    {
        if (_tokenCounter.CountTokens(text) <= MaxInputTokens)
        {
            return text;
        }

        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(words.Length);
        int tokens = 0;
        foreach (string word in words)
        {
            tokens += _tokenCounter.CountTokens(word);
            if (tokens > MaxInputTokens)
            {
                break;
            }

            kept.Add(word);
        }

        return string.Join(' ', kept);
    }

    internal static DocumentSummary Parse(string raw)
    {
        // json_object mode should return bare JSON, but strip code fences defensively.
        string json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = json.IndexOf('\n');
            int lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            json = json[(firstNewline + 1)..lastFence].Trim();
        }

        SummaryDto dto = JsonSerializer.Deserialize<SummaryDto>(json, JsonOptions)
            ?? throw new InvalidOperationException("Summariser returned null JSON.");
        if (string.IsNullOrWhiteSpace(dto.Summary) || dto.Topics is not { Count: >= 1 })
        {
            throw new InvalidOperationException($"Summariser returned incomplete JSON: {raw}");
        }

        // The prompt asks for <=150 words and 3-6 topics; a model is free to ignore both.
        // Enforce at the seam rather than trust: an over-long summary or a 12-tag topic list
        // silently changes what the composite doc vector represents (§6.4) and what the
        // recommender's topic-overlap reason claims. Trim rather than throw — a slightly
        // over-length summary is still usable, and failing ingestion over prose length would
        // be a worse trade. Under-length topic lists are accepted as-is: we can drop excess
        // tags honestly, but we cannot invent missing ones.
        return new DocumentSummary(
            TrimToWords(dto.Summary.Trim(), MaxSummaryWords),
            dto.Topics.Take(MaxTopics).ToList());
    }

    /// <summary>Truncates at a word boundary, keeping whole words.</summary>
    internal static string TrimToWords(string text, int maxWords)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? text : string.Join(' ', words.Take(maxWords)) + " […]";
    }

    private sealed record SummaryDto(string? Summary, IReadOnlyList<string>? Topics);
}
