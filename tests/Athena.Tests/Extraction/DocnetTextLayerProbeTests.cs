using System.Text;
using Athena.Ingestion.Extraction;
using Athena.Ingestion.Rasterisation;

namespace Athena.Tests.Extraction;

/// <summary>
/// The probe against both PDF species it must tell apart: an image-only PDF manufactured by
/// the same writer that produces A1-scanned.pdf, and a hand-assembled PDF with a real text
/// layer. Real PDFium reads, no corpus dependency.
/// </summary>
public sealed class DocnetTextLayerProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("athena-probe-").FullName;
    private readonly DocnetTextLayerProbe _probe = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ImageOnlyPdf_HasNoTextLayer_OnAnyPage()
    {
        // A 100x100 white page, twice — the same construction as the manufactured scan.
        var white = Enumerable.Repeat((byte)255, 100 * 100 * 3).ToArray();
        var pages = new[] { new RasterPage(100, 100, white), new RasterPage(100, 100, white) };
        string path = Path.Combine(_dir, "image-only.pdf");
        File.WriteAllBytes(path, ImageOnlyPdfWriter.Write(pages, dpi: 72));

        IReadOnlyDictionary<int, bool> result = _probe.Probe(path);

        Assert.Equal(2, result.Count);
        Assert.False(result[1]);
        Assert.False(result[2]);
    }

    [Fact]
    public void TextLayerPdf_IsDetected()
    {
        string path = Path.Combine(_dir, "text.pdf");
        File.WriteAllBytes(path, BuildTextPdf(
            "This page carries a genuine PDF text layer with comfortably more than fifty characters of content."));

        IReadOnlyDictionary<int, bool> result = _probe.Probe(path);

        Assert.True(result[1]);
    }

    [Fact]
    public void TextLayerBelowFiftyChars_CountsAsNoTextLayer()
    {
        // The brief's threshold: a page yielding under ~50 chars goes to OCR. A stray header
        // is not a text layer.
        string path = Path.Combine(_dir, "sparse.pdf");
        File.WriteAllBytes(path, BuildTextPdf("Page 3"));

        Assert.False(_probe.Probe(path)[1]);
    }

    /// <summary>Minimal single-page PDF with a real Helvetica text object (valid xref, PDFium-loadable).</summary>
    private static byte[] BuildTextPdf(string text)
    {
        string escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        string content = $"BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET";

        var sb = new StringBuilder();
        var offsets = new long[6];
        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append($"{n} 0 obj\n{body}\nendobj\n");
        }

        sb.Append("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
               "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Obj(5, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");

        long xrefPos = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++)
        {
            sb.Append($"{offsets[n]:0000000000} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
