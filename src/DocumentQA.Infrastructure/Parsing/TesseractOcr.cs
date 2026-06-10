using System.Diagnostics;

namespace DocumentQA.Infrastructure.Parsing;

/// <summary>
/// Wraps pdftoppm + tesseract CLI tools (standard on the Linux Docker runtime image)
/// to OCR a single page from a PDF.  Only invoked when PdfPig extracts fewer than
/// OcrMinWords words from a page (i.e. scanned/image-only pages).
/// </summary>
public sealed class TesseractOcr
{
    public async Task<string> OcrPageAsync(string pdfPath, int pageNumber, CancellationToken ct)
    {
        var tmpDir  = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Step 1: rasterise the single page to a PNG
            var pngBase = Path.Combine(tmpDir, "page");
            var ppmResult = await RunAsync(
                "pdftoppm",
                $"-png -r 200 -f {pageNumber} -l {pageNumber} \"{pdfPath}\" \"{pngBase}\"",
                ct);

            if (!string.IsNullOrEmpty(ppmResult.Error))
                return string.Empty;

            // pdftoppm names output as <base>-<page>.png (e.g. page-1.png)
            var pngFiles = Directory.GetFiles(tmpDir, "*.png");
            if (pngFiles.Length == 0)
                return string.Empty;

            // Step 2: OCR with Tesseract
            var ocrResult = await RunAsync(
                "tesseract",
                $"\"{pngFiles[0]}\" stdout -l eng quiet",
                ct);

            return ocrResult.Output.Trim();
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<(string Output, string Error)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true
            }
        };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        var error  = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (output, error);
    }
}
