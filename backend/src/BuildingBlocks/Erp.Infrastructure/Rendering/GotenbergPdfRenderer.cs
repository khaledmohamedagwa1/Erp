using System.Net.Http.Headers;
using System.Text;
using Erp.Application.Reporting;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Rendering;

public sealed class PdfOptions
{
    public const string SectionName = "Pdf";

    /// <summary>Base URL of a Gotenberg service (e.g. <c>http://gotenberg:3000</c>). Empty = PDF output disabled (501).</summary>
    public string? GotenbergUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Largest PDF accepted back from the engine (protects the API from a misbehaving converter).</summary>
    public int MaxPdfBytes { get; set; } = 20 * 1024 * 1024;
}

/// <summary>
/// Converts self-contained HTML to PDF with Gotenberg's Chromium route (<c>POST /forms/chromium/convert/html</c>).
/// The HTML produced by <see cref="HtmlDocumentRenderer"/> embeds its styles and has no external resources,
/// so nothing else needs to be uploaded.
/// </summary>
public sealed class GotenbergPdfRenderer(HttpClient http, IOptions<PdfOptions> options) : IPdfRenderer
{
    public bool IsAvailable => true;

    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        form.Add(file, "files", "index.html");
        form.Add(new StringContent("true"), "printBackground");
        form.Add(new StringContent("true"), "preferCssPageSize");

        using var response = await http.PostAsync("forms/chromium/convert/html", form, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"PDF conversion failed with status {(int)response.StatusCode}.");
        }

        var limit = options.Value.MaxPdfBytes;
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new HttpRequestException("PDF conversion returned a document larger than Pdf:MaxPdfBytes.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length > limit ? throw new HttpRequestException("PDF conversion returned a document larger than Pdf:MaxPdfBytes.") : bytes;
    }

    public static Uri BaseAddress(string url) => new(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
}
