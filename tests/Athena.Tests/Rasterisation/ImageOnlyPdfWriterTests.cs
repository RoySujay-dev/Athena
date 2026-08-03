using System.Text;
using Athena.Ingestion.Rasterisation;
using Docnet.Core;
using Docnet.Core.Models;

namespace Athena.Tests.Rasterisation;

public class ImageOnlyPdfWriterTests
{
    private static RasterPage SolidPage(int w, int h, byte r, byte g, byte b)
    {
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }
        return new RasterPage(w, h, rgb);
    }

    [Fact]
    public void Write_ProducesStructurallyValidPdf()
    {
        var pdf = ImageOnlyPdfWriter.Write(new[] { SolidPage(4, 6, 200, 200, 200) }, dpi: 200);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        var tail = Encoding.ASCII.GetString(pdf, pdf.Length - 32, 32);
        Assert.Contains("%%EOF", tail);
    }

    [Fact]
    public void Write_RoundTripsThroughDocnet_WithNoTextLayer()
    {
        // Two distinguishable pages at 200 DPI; physical size must survive the round trip:
        // 100 px at 200 DPI = 0.5 in = 36 pt, which Docnet re-rasterises at scale 1 (72 DPI)
        // back to 36 px.
        var pdf = ImageOnlyPdfWriter.Write(
            new[] { SolidPage(100, 200, 255, 0, 0), SolidPage(100, 200, 0, 0, 255) },
            dpi: 200);

        using var reader = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));

        Assert.Equal(2, reader.GetPageCount());

        using var page = reader.GetPageReader(0);
        Assert.Equal(36, page.GetPageWidth());
        Assert.Equal(72, page.GetPageHeight());

        // The whole point of the artifact: an image-only page has no text layer to extract.
        Assert.True(string.IsNullOrWhiteSpace(page.GetText()));

        // And the image content itself survived: page 1 renders red, not white/black.
        var bgra = page.GetImage();
        var midPixel = (36 / 2 * 36 + 36 / 2) * 4;
        Assert.True(bgra[midPixel + 2] > 200, "expected a red pixel at page-1 centre");
        Assert.True(bgra[midPixel] < 60, "expected low blue at page-1 centre");
    }

    [Fact]
    public void Write_EmptyPageList_Throws()
    {
        Assert.Throws<ArgumentException>(() => ImageOnlyPdfWriter.Write(Array.Empty<RasterPage>(), 200));
    }

    [Fact]
    public void Write_MismatchedBufferLength_Throws()
    {
        var bad = new RasterPage(10, 10, new byte[5]);
        Assert.Throws<ArgumentException>(() => ImageOnlyPdfWriter.Write(new[] { bad }, 200));
    }
}
