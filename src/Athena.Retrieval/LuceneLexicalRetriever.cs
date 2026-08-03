using Athena.Core.Records;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Athena.Retrieval;

/// <summary>
/// BM25 over the same chunks the dense retriever searches (§7). The Lucene index is built
/// in-memory from the ingested <see cref="ChunkRecord"/>s — call <see cref="Index"/> once
/// after ingestion (mirrors the InMemory vector store: both indexes are rebuilt per process
/// from the ingestion output, so they can never drift apart).
/// </summary>
public sealed class LuceneLexicalRetriever : ILexicalRetriever, IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    // StandardAnalyzer lowercases and splits on non-alphanumerics but keeps mixed tokens like
    // "d516" and "ndcg" whole — exactly the identifiers this corpus's lexical queries hinge on.
    private readonly Analyzer _analyzer = new StandardAnalyzer(Version);
    private readonly RAMDirectory _directory = new();
    private DirectoryReader? _reader;
    private IndexSearcher? _searcher;

    /// <summary>Builds (or rebuilds) the index. Synchronous CPU-bound work, no I/O.</summary>
    public void Index(IEnumerable<ChunkRecord> chunks)
    {
        var config = new IndexWriterConfig(Version, _analyzer)
        {
            OpenMode = OpenMode.CREATE,
            // BM25 must be set at WRITE time too: the similarity chosen here decides how field
            // length norms are encoded, and searching BM25 over norms written for the default
            // TF-IDF similarity silently degrades the scores.
            Similarity = new BM25Similarity(),
        };

        using (var writer = new IndexWriter(_directory, config))
        {
            foreach (ChunkRecord chunk in chunks)
            {
                writer.AddDocument(new Document
                {
                    new StringField("chunkId", chunk.ChunkId, Field.Store.YES),
                    // StringField (not analyzed) so the docId filter is an exact term match.
                    new StringField("docId", chunk.DocId, Field.Store.YES),
                    new StoredField("title", chunk.Title),
                    new StoredField("page", chunk.PageNumber),
                    new StoredField("endPage", chunk.EndPage),
                    new TextField("text", chunk.Text, Field.Store.YES),
                });
            }

            writer.Commit();
        }

        _reader?.Dispose();
        _reader = DirectoryReader.Open(_directory);
        _searcher = new IndexSearcher(_reader) { Similarity = new BM25Similarity() };
    }

    public Task<IReadOnlyList<Passage>> SearchAsync(
        string query, int topK, string? docId = null, CancellationToken ct = default)
    {
        // In-memory Lucene search is synchronous CPU work; the async signature exists only to
        // satisfy the shared retriever contract.
        ct.ThrowIfCancellationRequested();
        if (_searcher is null)
        {
            throw new InvalidOperationException(
                "Lexical index is empty. Call Index(chunks) after ingestion before searching.");
        }

        IReadOnlyList<string> terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Passage>>([]);
        }

        var textQuery = new BooleanQuery();
        foreach (string term in terms)
        {
            textQuery.Add(new TermQuery(new Term("text", term)), Occur.SHOULD);
        }

        Query finalQuery = textQuery;
        if (docId is not null)
        {
            // Lucene 4.8 has no Occur.FILTER; the MUST docId clause does add a score component,
            // but it is identical for every surviving hit, so the BM25 ordering is unchanged.
            finalQuery = new BooleanQuery
            {
                { textQuery, Occur.MUST },
                { new TermQuery(new Term("docId", docId)), Occur.MUST },
            };
        }

        var passages = new List<Passage>(topK);
        foreach (ScoreDoc scoreDoc in _searcher.Search(finalQuery, topK).ScoreDocs)
        {
            Document doc = _searcher.Doc(scoreDoc.Doc);
            passages.Add(new Passage(
                doc.Get("chunkId"),
                doc.Get("docId"),
                doc.Get("title"),
                doc.GetField("page").GetInt32Value() ?? 0,
                doc.Get("text"),
                scoreDoc.Score,
                doc.GetField("endPage")?.GetInt32Value() ?? 0));
        }

        return Task.FromResult<IReadOnlyList<Passage>>(passages);
    }

    /// <summary>
    /// Runs the query through the SAME analyzer that indexed the chunks, then ORs the terms.
    /// Building the query by hand instead of QueryParser means user text can never trigger a
    /// parse exception (or query-syntax injection) via characters like ':' or '/'.
    /// </summary>
    private IReadOnlyList<string> Tokenize(string query)
    {
        var terms = new List<string>();
        using TokenStream stream = _analyzer.GetTokenStream("text", query);
        ICharTermAttribute termAttribute = stream.AddAttribute<ICharTermAttribute>();
        stream.Reset();
        while (stream.IncrementToken())
        {
            terms.Add(termAttribute.ToString());
        }

        stream.End();
        return terms;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _directory.Dispose();
        _analyzer.Dispose();
    }
}
