using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Athena.Ingestion.Rasterisation;

/// <summary>One rasterised page: tightly-packed 24-bit RGB pixels, row-major.</summary>
public readonly record struct RasterPage(int WidthPx, int HeightPx, byte[] Rgb);

/// <summary>
/// Minimal, dependency-free writer for an image-only PDF: one full-page RGB image XObject per
/// page, FlateDecode-compressed (PDF's FlateDecode is zlib, which .NET's ZLibStream emits
/// natively). No text layer, no fonts, no metadata — exactly the "scanned document" the brief
/// wants to manufacture. Pure function of its inputs, so it is unit-testable without touching
/// PDFium or the corpus.
/// </summary>
public static class ImageOnlyPdfWriter
{
    /// <param name="pages">Rasterised pages in order.</param>
    /// <param name="dpi">The DPI the rasters were rendered at; sets the PDF page size so the
    /// document keeps its original physical dimensions (points = pixels * 72 / dpi).</param>
    public static byte[] Write(IReadOnlyList<RasterPage> pages, int dpi)
    {
        if (pages.Count == 0)
            throw new ArgumentException("An image-only PDF needs at least one page.", nameof(pages));
        if (dpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));

        foreach (var p in pages)
        {
            if (p.Rgb.Length != p.WidthPx * p.HeightPx * 3)
                throw new ArgumentException(
                    $"Page buffer length {p.Rgb.Length} does not match {p.WidthPx}x{p.HeightPx} 24-bit RGB.",
                    nameof(pages));
        }

        // Object layout: 1 = Catalog, 2 = Pages, then per page i: (3+3i) = Page,
        // (4+3i) = content stream, (5+3i) = image XObject.
        var ms = new MemoryStream();
        var offsets = new long[3 + pages.Count * 3]; // 1-based; [0] unused

        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void BeginObj(int num)
        {
            offsets[num] = ms.Position;
            WriteAscii($"{num} 0 obj\n");
        }

        WriteAscii("%PDF-1.5\n");
        // Binary-content marker comment recommended by the spec so tools treat the file as binary.
        ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        BeginObj(1);
        WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObj(2);
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + 3 * i} 0 R"));
        WriteAscii($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            int pageObj = 3 + 3 * i, contentObj = pageObj + 1, imageObj = pageObj + 2;

            var wPt = (page.WidthPx * 72.0 / dpi).ToString("0.##", CultureInfo.InvariantCulture);
            var hPt = (page.HeightPx * 72.0 / dpi).ToString("0.##", CultureInfo.InvariantCulture);

            BeginObj(pageObj);
            WriteAscii(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {wPt} {hPt}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            // Scale the unit-square image XObject to fill the page exactly.
            var content = Encoding.ASCII.GetBytes($"q {wPt} 0 0 {hPt} 0 0 cm /Im0 Do Q");
            BeginObj(contentObj);
            WriteAscii($"<< /Length {content.Length} >>\nstream\n");
            ms.Write(content);
            WriteAscii("\nendstream\nendobj\n");

            var compressed = Deflate(page.Rgb);
            BeginObj(imageObj);
            WriteAscii(
                $"<< /Type /XObject /Subtype /Image /Width {page.WidthPx} /Height {page.HeightPx} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            ms.Write(compressed);
            WriteAscii("\nendstream\nendobj\n");
        }

        var xrefStart = ms.Position;
        var objectCount = offsets.Length; // includes the free object 0
        WriteAscii($"xref\n0 {objectCount}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var num = 1; num < objectCount; num++)
            WriteAscii($"{offsets[num]:0000000000} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }
}
