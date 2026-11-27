using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Reporting;
using Erp.Modules.Documents.Contracts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Stands in for Gotenberg: records the HTML it was asked to convert and returns a minimal PDF.</summary>
public sealed class FakePdfRenderer : IPdfRenderer
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<string> Rendered = [];

    public static readonly byte[] Pdf = "%PDF-1.4\n%fake\n%%EOF"u8.ToArray();

    public bool IsAvailable => true;

    public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
    {
        Rendered.Add(html);
        return Task.FromResult(Pdf);
    }
}

public class DocumentsTests(ErpApiFixture fixture)
{
    private const string Api = "/api/v1/documents/attachments";
    private static readonly byte[] PdfBytes = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid companyId, Guid ownerId, string fileName, byte[] content, string ownerType = "sales.sales-invoice", string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(ownerType), "ownerType");
        form.Add(new StringContent(ownerId.ToString()), "ownerId");
        form.Add(new StringContent("Signed copy"), "description");
        var request = new HttpRequestMessage(HttpMethod.Post, Api) { Content = form }.WithCompany(companyId);
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Attachments_can_be_uploaded_listed_downloaded_and_soft_deleted()
    {
        var t = await fixture.NewTenantAsync();
        var invoiceId = Guid.NewGuid();

        var created = await UploadAsync(t.Client, t.CompanyId, invoiceId, "C:\\scans\\INV-0001.pdf", PdfBytes, contentType: "text/html");
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var attachment = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = attachment.GetProperty("id").GetGuid();
        attachment.GetProperty("fileName").GetString().ShouldBe("INV-0001.pdf");
        attachment.GetProperty("contentType").GetString().ShouldBe("application/pdf"); // from the extension, not the client header
        attachment.GetProperty("sizeBytes").GetInt64().ShouldBe(PdfBytes.Length);
        attachment.GetProperty("uploadedBy").GetGuid().ShouldBe(t.AdminUserId);
        attachment.GetProperty("description").GetString().ShouldBe("Signed copy");

        var list = await (await t.Client.GetAsCompanyAsync($"{Api}?ownerType=sales.sales-invoice&ownerId={invoiceId}", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        list.EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(id);
        (await (await t.Client.GetAsCompanyAsync($"{Api}?ownerType=sales.sales-invoice&ownerId={Guid.NewGuid()}", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetArrayLength().ShouldBe(0);

        var download = await t.Client.GetAsCompanyAsync($"{Api}/{id}/content", t.CompanyId);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        download.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        download.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        (await download.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(PdfBytes);

        // The same bytes on another document share one stored blob but are separate attachments.
        (await UploadAsync(t.Client, t.CompanyId, Guid.NewGuid(), "copy.pdf", PdfBytes)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var deleted = await t.Client.SendAsCompanyAsync(HttpMethod.Delete, $"{Api}/{id}", t.CompanyId);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await t.Client.SendAsCompanyAsync(HttpMethod.Delete, $"{Api}/{id}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await t.Client.GetAsCompanyAsync($"{Api}/{id}/content", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await t.Client.GetAsCompanyAsync($"{Api}?ownerType=sales.sales-invoice&ownerId={invoiceId}", t.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetArrayLength().ShouldBe(0);

        // The soft-deleted row is kept and its upload and deletion are in the audit log.
        await using var connection = await fixture.OpenAsTenantAsync(t.TenantId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsDeleted FROM documents.Attachment WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        ((bool)(await command.ExecuteScalarAsync(Ct))!).ShouldBeTrue();
        command.CommandText = "SELECT COUNT(*) FROM audit.AuditLog WHERE EntityId = @sid AND EntityType LIKE '%Attachment'";
        command.Parameters.AddWithValue("@sid", id.ToString());
        ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Uploads_are_validated()
    {
        var t = await fixture.NewTenantAsync();
        var owner = Guid.NewGuid();

        var mismatch = await UploadAsync(t.Client, t.CompanyId, owner, "invoice.pdf", "<html><script>alert(1)</script></html>"u8.ToArray());
        mismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await mismatch.ErrorCodeAsync()).ShouldBe("documents.file-content-mismatch");

        (await (await UploadAsync(t.Client, t.CompanyId, owner, "page.html", "<html/>"u8.ToArray())).ErrorCodeAsync()).ShouldBe("documents.file-type-not-allowed");
        (await (await UploadAsync(t.Client, t.CompanyId, owner, "a.pdf", PdfBytes, ownerType: "../etc")).ErrorCodeAsync()).ShouldBe("documents.invalid-owner-type");

        using var json = new HttpRequestMessage(HttpMethod.Post, Api) { Content = JsonContent.Create(new { ownerType = "x.y" }) };
        (await (await t.Client.SendAsync(json.WithCompany(t.CompanyId), Ct)).ErrorCodeAsync()).ShouldBe("documents.multipart-required");

        // Without the permission: forbidden.
        var role = await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name = "Viewer", permissions = new[] { DocumentsPermissions.AttachmentRead } }, Ct);
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var email = $"viewer@{t.TenantCode.ToLowerInvariant()}.example";
        (await t.Client.PostAsJsonAsync("/api/v1/identity/users",
            new { email, displayName = "Viewer", password = "Viewer-Passw0rd!", roleIds = new[] { roleId }, companyIds = new[] { t.CompanyId } }, Ct)).EnsureSuccessStatusCode();
        using var viewer = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(viewer, t.TenantCode, email, "Viewer-Passw0rd!");
        (await UploadAsync(viewer, t.CompanyId, owner, "a.pdf", PdfBytes)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.GetAsCompanyAsync($"{Api}?ownerType=sales.sales-invoice&ownerId={owner}", t.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Attachments_are_isolated_between_tenants()
    {
        var a = await fixture.NewTenantAsync();
        var b = await fixture.NewTenantAsync();
        var owner = Guid.NewGuid();
        var created = await UploadAsync(a.Client, a.CompanyId, owner, "secret.pdf", PdfBytes);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        (await b.Client.GetAsCompanyAsync($"{Api}/{id}/content", b.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.SendAsCompanyAsync(HttpMethod.Delete, $"{Api}/{id}", b.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await b.Client.GetAsCompanyAsync($"{Api}?ownerType=sales.sales-invoice&ownerId={owner}", b.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetArrayLength().ShouldBe(0);

        // Row-level security: tenant B's database session cannot see the row at all.
        await using var connection = await fixture.OpenAsTenantAsync(b.TenantId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM documents.Attachment WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(0);
    }

    [Fact]
    public async Task Pdf_output_answers_501_until_a_pdf_engine_is_configured()
    {
        var t = await fixture.NewTenantAsync();
        const string report = "/api/v1/reports/sales/summary?from=2026-01-01&to=2026-12-31";

        var unconfigured = await t.Client.GetAsCompanyAsync($"{report}&format=pdf", t.CompanyId);
        unconfigured.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await unconfigured.ErrorCodeAsync()).ShouldBe("reporting.pdf-not-configured");
        (await t.Client.GetAsCompanyAsync($"/api/v1/reports/documents/sales-invoice/{Guid.NewGuid()}?format=pdf", t.CompanyId))
            .StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await (await t.Client.GetAsCompanyAsync($"/api/v1/reports/documents/sales-invoice/{Guid.NewGuid()}?format=docx", t.CompanyId)).ErrorCodeAsync())
            .ShouldBe("reporting.invalid-format");

        using var withPdf = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPdfRenderer, FakePdfRenderer>())));
        using var client = withPdf.CreateClient();
        await ErpApiFixture.SignInAsync(client, t.TenantCode, t.Email, t.Password);

        var pdf = await client.GetAsCompanyAsync($"{report}&format=pdf&lang=ar", t.CompanyId);
        pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        pdf.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldEndWith(".pdf");
        (await pdf.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(FakePdfRenderer.Pdf);
        FakePdfRenderer.Rendered.ShouldContain(html => html.Contains("dir=\"rtl\""));

        // A document that does not exist is still a 404, not a PDF.
        (await client.GetAsCompanyAsync($"/api/v1/reports/documents/sales-invoice/{Guid.NewGuid()}?format=pdf", t.CompanyId))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Notifications_belong_to_their_user_and_can_be_marked_read()
    {
        var t = await fixture.NewTenantAsync();
        var other = await fixture.NewTenantAsync();
        Guid userId = t.AdminUserId;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<Erp.Application.Abstractions.ITenantContext>();
            tenant.SetTenant(t.TenantId);
            var session = scope.ServiceProvider.GetRequiredService<Erp.Infrastructure.Persistence.ErpDbSession>();
            await session.BeginTransactionAsync(Ct);
            var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
            await publisher.PublishAsync([userId], new InAppNotification("test.first", "First", "Body", "/sales/invoices", t.CompanyId), Ct);
            await publisher.PublishAsync([userId, userId], new InAppNotification("test.second", "Second"), Ct);
            await session.CommitAsync(Ct);
        }

        var count = await (await t.Client.GetAsync("/api/v1/notifications/unread-count", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        count.GetProperty("count").GetInt32().ShouldBe(2);
        var page = await (await t.Client.GetAsync("/api/v1/notifications?pageSize=10", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        page.GetProperty("totalCount").GetInt32().ShouldBe(2);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("title").GetString()).ShouldBe(["Second", "First"]);
        var firstId = items[1].GetProperty("id").GetGuid();
        items[1].GetProperty("link").GetString().ShouldBe("/sales/invoices");

        // Another tenant's user can neither see nor mark it.
        (await (await other.Client.GetAsync("/api/v1/notifications", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await other.Client.PostAsync($"/api/v1/notifications/{firstId}/read", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await t.Client.PostAsync($"/api/v1/notifications/{firstId}/read", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var unread = await (await t.Client.GetAsync("/api/v1/notifications?unreadOnly=true", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        unread.GetProperty("items").EnumerateArray().Single().GetProperty("title").GetString().ShouldBe("Second");

        var all = await (await t.Client.PostAsync("/api/v1/notifications/read-all", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        all.GetProperty("marked").GetInt32().ShouldBe(1);
        (await (await t.Client.GetAsync("/api/v1/notifications/unread-count", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("count").GetInt32().ShouldBe(0);
    }
}
