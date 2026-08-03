using Docnet.Core;
using Docnet.Core.Models;

namespace Athena.Ingestion.Rasterisation;

/// <summary>
/// <c>dotnet run --project src/Athena.Ingestion -- rasterise</c>: manufacture the scanned test
/// document (brief §4.1). Rasterises pages 1–12 of corpus/A1.pdf (BCBS d516) at 200 DPI and
/// reassembles them into an image-only corpus/A1-scanned.pdf, later ingested under DocId
/// "A1-SCAN". With Azure DI extracting both copies, OCR Delta = Recall(A1-SCAN) − Recall(A1)
/// measures DI's image-OCR path against its text-layer path — the same diagnostic the brief
/// intended, DI-vs-DI instead of Tesseract-vs-PdfPig.
///
/// Docnet.Core is used here for exactly and only this: manufacturing the test artifact.
/// It is not an extractor and must never become one (extraction is Azure DI, see Extraction/).
/// </summary>
public static class RasteriseCommand
{
    private const int Dpi = 200;              // per brief §4.1
    private const int MaxPages = 12;          // per brief §4.1 ("pages 1–12 of A1")
    private const string SourceFile = "A1.pdf";
    private const string TargetFile = "A1-scanned.pdf";

    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var corpusDir = CorpusLocator.LocateCorpusDirectory();
        if (corpusDir is null)
        {
            Console.Error.WriteLine("error: could not find corpus/manifest.json walking up from the current directory.");
            return 2;
        }

        var sourcePath = Path.Combine(corpusDir, SourceFile);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"error: {sourcePath} not found. Run the fetch verb first:");
            Console.Error.WriteLine("       dotnet run --project src/Athena.Ingestion -- fetch");
            return 2;
        }

        var targetPath = Path.Combine(corpusDir, TargetFile);
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            Console.WriteLine($"  skip  {TargetFile} already present ({new FileInfo(targetPath).Length:N0} bytes)");
            return 0;
        }

        // PDFium (via Docnet) is synchronous native code; the loop just checks for cancellation
        // between pages. Scaling factor: PDF user space is 72/inch, so 200 DPI = 200/72.
        var pages = new List<RasterPage>();
        using (var docReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(Dpi / 72.0)))
        {
            var pageCount = Math.Min(MaxPages, docReader.GetPageCount());
            for (var i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var pageReader = docReader.GetPageReader(i);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                var bgra = pageReader.GetImage();

                pages.Add(new RasterPage(width, height, BgraToRgbOverWhite(bgra, width, height)));
                Console.WriteLine($"  raster page {i + 1,2}: {width}x{height} px");
            }
        }

        var pdf = ImageOnlyPdfWriter.Write(pages, Dpi);
        await File.WriteAllBytesAsync(targetPath, pdf, ct);

        Console.WriteLine();
        Console.WriteLine($"wrote {TargetFile}: {pages.Count} pages, {pdf.Length:N0} bytes -> {targetPath}");
        return 0;
    }

    /// <summary>
    /// Docnet returns BGRA with unpainted areas left transparent (alpha 0). Composite over
    /// white — paper — while swapping to RGB, or the "scanned" pages would render black in
    /// viewers and to DI.
    /// </summary>
    private static byte[] BgraToRgbOverWhite(byte[] bgra, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int src = 0, dst = 0; src < bgra.Length; src += 4, dst += 3)
        {
            int b = bgra[src], g = bgra[src + 1], r = bgra[src + 2], a = bgra[src + 3];
            rgb[dst] = (byte)((r * a + 255 * (255 - a)) / 255);
            rgb[dst + 1] = (byte)((g * a + 255 * (255 - a)) / 255);
            rgb[dst + 2] = (byte)((b * a + 255 * (255 - a)) / 255);
        }
        return rgb;
    }
}
