using System.Net;
using System.Text;
using Erp.Application.Abstractions;
using Erp.Infrastructure.Rendering;
using Erp.Modules.Documents.Application;
using Erp.Modules.Documents.Domain;
using Erp.Modules.Documents.Infrastructure;
using Erp.SharedKernel.Domain;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public sealed class DocumentsTests : IDisposable
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF"u8.ToArray();
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "erp-unit-attachments-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------- Allowlist and content sniffing ----------

    [Theory]
    [InlineData("invoice.pdf", "application/pdf")]
    [InlineData("SCAN.PDF", "application/pdf")]
    public void A_pdf_is_accepted_and_its_type_comes_from_the_extension(string name, string expected)
    {
        var detected = FileTypes.Detect(name, PdfBytes);
        detected.IsSuccess.ShouldBeTrue();
        detected.Value.ContentType.ShouldBe(expected);
    }

    [Fact]
    public void Content_that_does_not_match_the_extension_is_rejected()
    {
        // An HTML page renamed to .pdf, and a PNG renamed to .jpg.
        FileTypes.Detect("invoice.pdf", "<html><script>alert(1)</script></html>"u8.ToArray()).Error.Code.ShouldBe("documents.file-content-mismatch");
        FileTypes.Detect("photo.jpg", PngBytes).Error.Code.ShouldBe("documents.file-content-mismatch");
        FileTypes.Detect("photo.png", PngBytes).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("page.html")]
    [InlineData("logo.svg")]
    [InlineData("setup.exe")]
    [InlineData("macro.xlsm")]
    [InlineData("noextension")]
    public void Active_or_unknown_file_types_are_not_allowed(string name) =>
        FileTypes.Detect(name, PdfBytes).Error.Code.ShouldBe("documents.file-type-not-allowed");

    [Fact]
    public void Text_files_must_not_contain_binary_content_and_empty_files_are_rejected()
    {
        FileTypes.Detect("notes.txt", Encoding.UTF8.GetBytes("سطر عربي\nline 2")).IsSuccess.ShouldBeTrue();
        FileTypes.Detect("data.csv", [0x41, 0x00, 0x42]).Error.Code.ShouldBe("documents.file-content-mismatch");
        FileTypes.Detect("notes.txt", []).Error.Code.ShouldBe("documents.file-empty");
    }

    [Fact]
    public void Office_documents_need_a_zip_container_and_the_company_allowlist_can_narrow_types()
    {
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];
        FileTypes.Detect("book.xlsx", zip).IsSuccess.ShouldBeTrue();
        FileTypes.Detect("book.xlsx", PdfBytes).IsFailure.ShouldBeTrue();
        FileTypes.Detect("book.xlsx", zip, [".pdf"]).Error.Code.ShouldBe("documents.file-type-not-allowed");
    }

    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("C:\\Users\\me\\Desktop\\invoice.pdf", "invoice.pdf")]
    [InlineData("..hidden.pdf", "hidden.pdf")]
    [InlineData("bad\"name<>.pdf", "bad_name__.pdf")]
    [InlineData("tab\tname.pdf", "tab_name.pdf")]
    public void Client_file_names_are_reduced_to_a_plain_name(string input, string expected) =>
        FileTypes.SanitizeFileName(input).ShouldBe(expected);

    // ---------- Storage keys and the local store ----------

    [Fact]
    public void Storage_keys_are_content_addressed_per_tenant()
    {
        var tenant = Guid.NewGuid();
        var sha = StorageKeys.Sha256Hex(PdfBytes);
        var key = StorageKeys.For(tenant, sha);
        key.ShouldBe($"{tenant:N}/{sha[..2]}/{sha}");
        StorageKeys.IsSafe(key).ShouldBeTrue();
        StorageKeys.For(Guid.NewGuid(), sha).ShouldNotBe(key);
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("0123456789abcdef0123456789abcdef/../x")]
    [InlineData("0123456789abcdef0123456789abcdef/ab/..%2f..%2fsecret")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF/ab/ab00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("")]
    public void Local_storage_rejects_keys_that_could_escape_the_root(string key)
    {
        var storage = new LocalFileStorage(_root);
        StorageKeys.IsSafe(key).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => storage.PathFor(key));
        Should.Throw<ArgumentException>(() => storage.ExistsAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Local_storage_round_trips_content_and_writes_each_key_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new LocalFileStorage(_root);
        var key = StorageKeys.For(Guid.NewGuid(), StorageKeys.Sha256Hex(PdfBytes));

        (await storage.ExistsAsync(key, ct)).ShouldBeFalse();
        (await storage.OpenReadAsync(key, ct)).ShouldBeNull();

        await storage.SaveAsync(key, new MemoryStream(PdfBytes), "application/pdf", ct);
        await storage.SaveAsync(key, new MemoryStream(PdfBytes), "application/pdf", ct);
        (await storage.ExistsAsync(key, ct)).ShouldBeTrue();

        await using var read = await storage.OpenReadAsync(key, ct);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy, ct);
        copy.ToArray().ShouldBe(PdfBytes);
        storage.PathFor(key).ShouldStartWith(storage.Root + Path.DirectorySeparatorChar);
        Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task Intake_rejects_oversized_uploads_and_stores_valid_ones_under_the_tenant_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new FixedTenant(Guid.NewGuid());
        var storage = new LocalFileStorage(_root);
        var intake = new AttachmentIntake(storage, tenant, new AttachmentOptions { MaxUploadBytes = 16 });

        (await intake.PrepareAsync(new MemoryStream(PdfBytes), "big.pdf", null, ct)).Error.Code.ShouldBe("documents.file-too-large");
        (await intake.PrepareAsync(new MemoryStream(PdfBytes), "big.pdf", 1_000_000, ct)).Error.Code.ShouldBe("documents.file-too-large");

        var ok = await new AttachmentIntake(storage, tenant, new AttachmentOptions()).PrepareAsync(new MemoryStream(PdfBytes), "../x/Invoice 1.pdf", null, ct);
        ok.IsSuccess.ShouldBeTrue();
        ok.Value.FileName.ShouldBe("Invoice 1.pdf");
        ok.Value.ContentType.ShouldBe("application/pdf");
        ok.Value.SizeBytes.ShouldBe(PdfBytes.Length);
        ok.Value.StorageKey.ShouldStartWith(tenant.TenantId!.Value.ToString("N") + "/");
        (await storage.ExistsAsync(ok.Value.StorageKey, ct)).ShouldBeTrue();
    }

    // ---------- S3 adapter (configuration only; no network) ----------

    [Fact]
    public void S3_adapter_prefixes_keys_validates_them_and_configures_compatible_endpoints()
    {
        var options = new S3StorageOptions { Bucket = "erp", ServiceUrl = "http://minio:9000", Prefix = "/tenant-files/", AccessKey = "k", SecretKey = "s" };
        var config = S3FileStorage.CreateConfig(options);
        config.ForcePathStyle.ShouldBeTrue();
        config.ServiceURL.ShouldStartWith("http://minio:9000");

        using var client = S3FileStorage.CreateClient(options);
        var storage = new S3FileStorage(client, options);
        var key = StorageKeys.For(Guid.NewGuid(), StorageKeys.Sha256Hex(PdfBytes));
        storage.ObjectKey(key).ShouldBe("tenant-files/" + key);
        Should.Throw<ArgumentException>(() => storage.ObjectKey("../escape"));
        Should.Throw<InvalidOperationException>(() => S3FileStorage.CreateClient(new S3StorageOptions()));
    }

    // ---------- Domain ----------

    [Fact]
    public void Attachments_need_a_well_formed_owner_and_cannot_be_deleted_twice()
    {
        var now = DateTime.UtcNow;
        Should.Throw<DomainException>(() => Attachment.Create(Guid.NewGuid(), "Sales Invoice", Guid.NewGuid(), "a.pdf", "application/pdf", 1, "x", "k", null, Guid.NewGuid(), now))
            .Error.Code.ShouldBe("documents.invalid-owner-type");
        Should.Throw<DomainException>(() => Attachment.Create(Guid.NewGuid(), "sales.sales-invoice", Guid.Empty, "a.pdf", "application/pdf", 1, "x", "k", null, Guid.NewGuid(), now))
            .Error.Code.ShouldBe("documents.owner-required");

        var attachment = Attachment.Create(Guid.NewGuid(), "Sales.Sales-Invoice", Guid.NewGuid(), "a.pdf", "application/pdf", 1, "x", "k", " note ", Guid.NewGuid(), now);
        attachment.OwnerType.ShouldBe("sales.sales-invoice");
        attachment.Description.ShouldBe("note");
        attachment.Delete(Guid.NewGuid(), now);
        attachment.IsDeleted.ShouldBeTrue();
        Should.Throw<DomainException>(() => attachment.Delete(Guid.NewGuid(), now)).Error.Code.ShouldBe("documents.attachment-deleted");
    }

    [Theory]
    [InlineData("https://evil.example/login")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    public void Notification_links_must_be_relative_routes(string link) =>
        Should.Throw<DomainException>(() => Notification.Create(Guid.NewGuid(), null, "x", "Title", null, link, DateTime.UtcNow))
            .Error.Code.ShouldBe("documents.notification-link-invalid");

    [Fact]
    public void Reading_a_notification_keeps_the_first_read_time()
    {
        var first = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var notification = Notification.Create(Guid.NewGuid(), null, "workflow.approval-requested", new string('x', 500), null, "/approvals", first);
        notification.Title.Length.ShouldBe(200);
        notification.MarkRead(first);
        notification.MarkRead(first.AddHours(1));
        notification.ReadAtUtc.ShouldBe(first);
    }

    // ---------- PDF adapter ----------

    [Fact]
    public async Task Gotenberg_adapter_posts_the_html_and_returns_the_pdf()
    {
        var handler = new CapturingHandler(PdfBytes);
        using var http = new HttpClient(handler) { BaseAddress = GotenbergPdfRenderer.BaseAddress("http://gotenberg:3000") };
        var renderer = new GotenbergPdfRenderer(http, Options.Create(new PdfOptions()));

        var pdf = await renderer.RenderAsync("<html><body>مرحبا</body></html>", TestContext.Current.CancellationToken);

        pdf.ShouldBe(PdfBytes);
        handler.Uri!.ToString().ShouldBe("http://gotenberg:3000/forms/chromium/convert/html");
        handler.Body!.ShouldContain("filename=index.html");
        handler.Body!.ShouldContain("مرحبا");

        var failing = new GotenbergPdfRenderer(new HttpClient(new CapturingHandler([], HttpStatusCode.ServiceUnavailable)) { BaseAddress = new Uri("http://g/") },
            Options.Create(new PdfOptions()));
        await Should.ThrowAsync<HttpRequestException>(() => failing.RenderAsync("<p/>", TestContext.Current.CancellationToken));
    }

    private sealed class FixedTenant(Guid tenantId) : ITenantContext
    {
        public Guid? TenantId { get; } = tenantId;

        public Guid? CompanyId => null;

        public bool HasTenant => true;

        public void SetTenant(Guid tenantId) => throw new NotSupportedException();

        public void SetCompany(Guid companyId) => throw new NotSupportedException();
    }

    private sealed class CapturingHandler(byte[] response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(response) };
        }
    }
}
