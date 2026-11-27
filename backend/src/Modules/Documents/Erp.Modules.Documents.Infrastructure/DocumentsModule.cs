using Erp.Application;
using Erp.Application.Messaging;
using Erp.Application.Paging;
using Erp.Infrastructure;
using Erp.Modules.Documents.Application;
using Erp.Modules.Documents.Contracts;
using Erp.SharedKernel.Results;
using Erp.Web.Http;
using Erp.Web.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Erp.Modules.Documents.Infrastructure;

public sealed class DocumentsModule : IErpModule
{
    public const string Code = "documents";

    public ModuleManifest Manifest { get; } = new(
        Code,
        "Documents and notifications",
        "المرفقات والإشعارات",
        IsCore: true,
        DependsOn: [],
        Permissions:
        [
            new(DocumentsPermissions.AttachmentRead, Code, "View and download attachments of documents", "عرض وتنزيل مرفقات المستندات"),
            new(DocumentsPermissions.AttachmentWrite, Code, "Upload and delete attachments", "رفع وحذف المرفقات"),
        ],
        Menu: []);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddErpDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, order: 170);
        services.AddScoped<IDocumentsDbContext>(sp => sp.GetRequiredService<DocumentsDbContext>());

        var options = new AttachmentOptions();
        configuration.GetSection(AttachmentOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        if (string.Equals(options.Storage, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(_ => S3FileStorage.CreateClient(options.S3));
            services.AddSingleton<IFileStorage>(sp => new S3FileStorage(sp.GetRequiredService<Amazon.S3.IAmazonS3>(), options.S3));
        }
        else
        {
            services.AddSingleton<IFileStorage>(sp =>
            {
                var root = Path.IsPathRooted(options.LocalRoot)
                    ? options.LocalRoot
                    : Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, options.LocalRoot);
                return new LocalFileStorage(root);
            });
        }

        services.AddScoped<AttachmentIntake>();

        // AddScoped (not TryAdd): wins over modules' TryAddScoped<INotificationPublisher, NoInAppNotifications>.
        services.AddScoped<INotificationPublisher, NotificationPublisher>();
        services.AddHandlersFromAssembly(typeof(IDocumentsDbContext).Assembly);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var attachments = endpoints.MapGroup("/api/v1/documents/attachments").WithTags("Attachments").RequireAuthorization().RequireCompany();

        attachments.MapGet(string.Empty, async (string? ownerType, Guid? ownerId, IDispatcher d, CancellationToken ct) =>
                ownerId is not { } id || string.IsNullOrWhiteSpace(ownerType)
                    ? Error.Validation("documents.owner-required", "ownerType and ownerId are required.").ToProblem()
                    : (await d.Send(new ListAttachmentsQuery(ownerType, id), ct)).ToHttpResult())
            .RequirePermission(DocumentsPermissions.AttachmentRead);

        // multipart/form-data: file, ownerType, ownerId, description (optional). The form is read by hand (no antiforgery
        // binding): the API authenticates with bearer tokens, not cookies.
        attachments.MapPost(string.Empty, UploadAsync).RequirePermission(DocumentsPermissions.AttachmentWrite);

        attachments.MapGet("/{id:guid}/content", async (Guid id, HttpContext http, IDispatcher d, IFileStorage storage, CancellationToken ct) =>
            {
                var found = await d.Send(new GetAttachmentFileQuery(id), ct);
                if (found.IsFailure)
                {
                    return found.Error.ToProblem();
                }

                var stream = await storage.OpenReadAsync(found.Value.StorageKey, ct);
                if (stream is null)
                {
                    return DocumentsAppErrors.FileMissing.ToProblem();
                }

                var a = found.Value.Attachment;
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.ETag = $"\"{a.Sha256}\"";

                // Always a download (Content-Disposition: attachment), never rendered inline by the browser.
                return Results.Stream(stream, a.ContentType, a.FileName);
            })
            .RequirePermission(DocumentsPermissions.AttachmentRead);

        attachments.MapDelete("/{id:guid}", async (Guid id, IDispatcher d, CancellationToken ct) =>
                (await d.Send(new DeleteAttachmentCommand(id), ct)).ToHttpResult(_ => Results.NoContent()))
            .RequirePermission(DocumentsPermissions.AttachmentWrite);

        // ---- Notifications: always the signed-in user's own; no permission and no company needed ----
        var notifications = endpoints.MapGroup("/api/v1/notifications").WithTags("Notifications").RequireAuthorization();
        notifications.MapGet(string.Empty, async (bool? unreadOnly, int? page, int? pageSize, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new ListNotificationsQuery(unreadOnly ?? false, new PageRequest(page ?? 1, pageSize ?? 50)), ct)).ToHttpResult());
        notifications.MapGet("/unread-count", async (IDispatcher d, CancellationToken ct) =>
            (await d.Send(new UnreadNotificationCountQuery(), ct)).ToHttpResult(count => Results.Ok(new { count })));
        notifications.MapPost("/{id:guid}/read", async (Guid id, IDispatcher d, CancellationToken ct) =>
            (await d.Send(new MarkNotificationReadCommand(id), ct)).ToHttpResult(_ => Results.NoContent()));
        notifications.MapPost("/read-all", async (IDispatcher d, CancellationToken ct) =>
            (await d.Send(new MarkAllNotificationsReadCommand(), ct)).ToHttpResult(count => Results.Ok(new { marked = count })));
    }

    private static async Task<IResult> UploadAsync(HttpContext http, AttachmentIntake intake, AttachmentOptions options, IDispatcher d, CancellationToken ct)
    {
        // Let Kestrel accept bodies up to the attachment limit (plus room for the multipart envelope) on this endpoint only.
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = options.MaxUploadBytes + (1024 * 1024);
        }

        if (!http.Request.HasFormContentType)
        {
            return Error.Validation("documents.multipart-required", "Upload the file as multipart/form-data.").ToProblem();
        }

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null)
        {
            return Error.Validation("documents.file-required", "A 'file' part is required.").ToProblem();
        }

        if (!Guid.TryParse(form["ownerId"], out var ownerId) || ownerId == Guid.Empty)
        {
            return Error.Validation("documents.owner-required", "ownerId is required.").ToProblem();
        }

        var ownerType = form["ownerType"].ToString();
        if (!Domain.DocumentsErrors.OwnerTypePattern().IsMatch(ownerType.Trim().ToLowerInvariant()))
        {
            return Domain.DocumentsErrors.InvalidOwnerType.ToProblem();
        }

        await using var content = file.OpenReadStream();
        var blob = await intake.PrepareAsync(content, file.FileName, file.Length, ct);
        if (blob.IsFailure)
        {
            return blob.Error.ToProblem();
        }

        var description = form["description"].ToString();
        return (await d.Send(new CreateAttachmentCommand(ownerType, ownerId, string.IsNullOrWhiteSpace(description) ? null : description, blob.Value), ct))
            .ToHttpResult(a => Results.Created($"/api/v1/documents/attachments/{a.Id}/content", a));
    }
}
