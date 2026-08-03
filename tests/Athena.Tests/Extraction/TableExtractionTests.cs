using System.ClientModel.Primitives;
using System.Security.Cryptography;
using Athena.Ingestion.Extraction;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;

namespace Athena.Tests.Extraction;

public class TableMarkdownSerializerTests
{
    [Fact]
    public void Serialize_SimpleTable_ProducesValidMarkdown()
    {
        var cells = new List<TableCellData>
        {
            new(0, 0, 1, 1, "Metric"),
            new(0, 1, 1, 1, "Value"),
            new(1, 0, 1, 1, "Recall"),
            new(1, 1, 1, 1, "0.82"),
        };

        var md = TableMarkdownSerializer.Serialize(2, 2, cells);

        Assert.NotNull(md);
        var lines = md!.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("| Metric | Value |", lines[0]);
        Assert.Equal("| --- | --- |", lines[1]);
        Assert.Equal("| Recall | 0.82 |", lines[2]);
    }

    [Fact]
    public void Serialize_ColumnSpan_RepeatsContentAcrossCoveredCells()
    {
        // A header spanning both columns (common in BCBS annex tables) must label both.
        var cells = new List<TableCellData>
        {
            new(0, 0, 1, 2, "Principle 6"),
            new(1, 0, 1, 1, "Bank"),
            new(1, 1, 1, 1, "Requirement"),
        };

        var md = TableMarkdownSerializer.Serialize(2, 2, cells);

        Assert.NotNull(md);
        Assert.Equal("| Principle 6 | Principle 6 |", md!.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public void Serialize_RowSpan_RepeatsContentDownCoveredRows()
    {
        var cells = new List<TableCellData>
        {
            new(0, 0, 1, 1, "Category"),
            new(0, 1, 1, 1, "Item"),
            new(1, 0, 2, 1, "Resilience"),
            new(1, 1, 1, 1, "Mapping"),
            new(2, 1, 1, 1, "Testing"),
        };

        var md = TableMarkdownSerializer.Serialize(3, 2, cells);

        Assert.NotNull(md);
        var lines = md!.Split(Environment.NewLine);
        Assert.Equal("| Resilience | Mapping |", lines[2]);
        Assert.Equal("| Resilience | Testing |", lines[3]);
    }

    [Fact]
    public void Serialize_EscapesPipesAndNewlines()
    {
        var cells = new List<TableCellData>
        {
            new(0, 0, 1, 1, "a|b"),
            new(0, 1, 1, 1, "line1\nline2"),
            new(1, 0, 1, 1, "x"),
            new(1, 1, 1, 1, "y"),
        };

        var md = TableMarkdownSerializer.Serialize(2, 2, cells);

        Assert.NotNull(md);
        var lines = md!.Split(Environment.NewLine);
        Assert.Equal(@"| a\|b | line1 line2 |", lines[0]);
    }

    [Theory]
    [InlineData(0, 2)] // no rows
    [InlineData(2, 0)] // no columns
    public void Serialize_DegenerateDimensions_ReturnsNull(int rows, int cols)
    {
        var cells = new List<TableCellData> { new(0, 0, 1, 1, "x") };
        Assert.Null(TableMarkdownSerializer.Serialize(rows, cols, cells));
    }

    [Fact]
    public void Serialize_AllEmptyCells_ReturnsNull()
    {
        var cells = new List<TableCellData>
        {
            new(0, 0, 1, 1, ""),
            new(0, 1, 1, 1, "   "),
        };
        Assert.Null(TableMarkdownSerializer.Serialize(1, 2, cells));
    }

    [Fact]
    public void Serialize_NoCells_ReturnsNull()
    {
        Assert.Null(TableMarkdownSerializer.Serialize(3, 3, new List<TableCellData>()));
    }

    [Fact]
    public void Serialize_OutOfBoundsSpan_IsClippedNotThrown()
    {
        // DI should never report a span past the table edge, but a mangled merged cell must
        // clip rather than crash ingestion.
        var cells = new List<TableCellData>
        {
            new(0, 0, 1, 1, "h1"),
            new(0, 1, 1, 5, "wide"),
            new(1, 0, 9, 1, "tall"),
            new(1, 1, 1, 1, "v"),
        };

        var md = TableMarkdownSerializer.Serialize(2, 2, cells);

        Assert.NotNull(md);
        Assert.Equal("| h1 | wide |", md!.Split(Environment.NewLine)[0]);
    }
}

public class AzureTableExtractorTests
{
    private sealed class CapturingLogger : ILogger<AzureTableExtractor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task ExtractAsync_SerialisesGoodTable_AndWarnsOnDegenerateTable()
    {
        string tempDir = Directory.CreateTempSubdirectory("athena-table-cache-").FullName;
        string tempPdfPath = Path.Combine(tempDir, "fixture.pdf");

        try
        {
            // A results-style table (the RAPTOR/BCBS shape) on page 3.
            var goodTable = DocumentIntelligenceModelFactory.DocumentTable(
                rowCount: 2,
                columnCount: 2,
                cells: new[]
                {
                    DocumentIntelligenceModelFactory.DocumentTableCell(
                        kind: DocumentTableCellKind.ColumnHeader, rowIndex: 0, columnIndex: 0, content: "Model"),
                    DocumentIntelligenceModelFactory.DocumentTableCell(
                        kind: DocumentTableCellKind.ColumnHeader, rowIndex: 0, columnIndex: 1, content: "Accuracy"),
                    DocumentIntelligenceModelFactory.DocumentTableCell(
                        rowIndex: 1, columnIndex: 0, content: "RAPTOR"),
                    DocumentIntelligenceModelFactory.DocumentTableCell(
                        rowIndex: 1, columnIndex: 1, content: "62.3"),
                },
                boundingRegions: new[] { DocumentIntelligenceModelFactory.BoundingRegion(pageNumber: 3) });

            // A degenerate detection: dimensions but only whitespace content.
            var degenerateTable = DocumentIntelligenceModelFactory.DocumentTable(
                rowCount: 1,
                columnCount: 1,
                cells: new[]
                {
                    DocumentIntelligenceModelFactory.DocumentTableCell(rowIndex: 0, columnIndex: 0, content: " "),
                },
                boundingRegions: new[] { DocumentIntelligenceModelFactory.BoundingRegion(pageNumber: 5) });

            AnalyzeResult fakeResult = DocumentIntelligenceModelFactory.AnalyzeResult(
                content: "irrelevant",
                tables: new[] { goodTable, degenerateTable });

            // Same forced-cache-hit technique as the text-extraction test: the analyzer finds
            // the fixture at the content-hash path and never touches the network.
            byte[] pdfBytes = "table fixture bytes -- hashed, never parsed"u8.ToArray();
            await File.WriteAllBytesAsync(tempPdfPath, pdfBytes);
            string hash = Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();
            BinaryData cacheJson = ModelReaderWriter.Write(fakeResult, ModelReaderWriterOptions.Json);
            await File.WriteAllBytesAsync(Path.Combine(tempDir, hash + "-formulas.json"), cacheJson.ToArray());

            var client = new DocumentIntelligenceClient(
                new Uri("https://example.invalid"), new AzureKeyCredential("unused-in-this-test"));
            IDocumentAnalyzer analyzer = new AzureDocumentAnalyzer(client, tempDir);
            var logger = new CapturingLogger();
            var extractor = new AzureTableExtractor(analyzer, logger);

            IReadOnlyList<PageTable> tables = await extractor.ExtractAsync(tempPdfPath);

            var table = Assert.Single(tables);
            Assert.Equal(3, table.PageNumber);
            var lines = table.MarkdownTable.Split(Environment.NewLine);
            Assert.Equal("| Model | Accuracy |", lines[0]);
            Assert.Equal("| --- | --- |", lines[1]);
            Assert.Equal("| RAPTOR | 62.3 |", lines[2]);

            var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("degenerate", warning.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
