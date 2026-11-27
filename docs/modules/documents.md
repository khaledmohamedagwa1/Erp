# Documents module — attachments and in-app notifications

Schema `documents` · migration order 170 · module code `documents` (core, always enabled) ·
projects `Erp.Modules.Documents.{Domain,Application,Infrastructure,Contracts}`.

PDF output for printable documents and reports is a platform service (`IPdfRenderer`) — see
[platform.md](platform.md#pdf-output).

## Attachments

A file attached to any business document, identified by an **owner type** and **owner id**. Owner types use the same
strings as approval document types (`sales.sales-invoice`, `procurement.purchase-order`, …): lower-case
`module.document-type`. Attachments are company-scoped (tenant + company query filters, row-level security) and
**audited** (upload and deletion appear in the hash-chained audit log).

| Method | Route | Permission | Notes |
|---|---|---|---|
| `POST` | `/api/v1/documents/attachments` | `documents.attachments.write` | `multipart/form-data`: `file`, `ownerType`, `ownerId`, optional `description`. 201 with the attachment. |
| `GET` | `/api/v1/documents/attachments?ownerType=&ownerId=` | `documents.attachments.read` | Non-deleted attachments of one document, oldest first. |
| `GET` | `/api/v1/documents/attachments/{id}/content` | `documents.attachments.read` | Download. Always `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, `ETag` = SHA-256. |
| `DELETE` | `/api/v1/documents/attachments/{id}` | `documents.attachments.write` | Soft delete (204). The row and the stored file are kept. |

All attachment endpoints require `X-Company-Id`.

### Upload validation

1. Size: `Documents:Attachments:MaxUploadBytes` (default 20 MB). The upload endpoint raises Kestrel's body limit for
   itself only; all other endpoints keep `Hosting:MaxRequestBodyBytes`.
2. File name: reduced to a plain name (directories, control characters and header-unsafe characters removed).
3. **Allowlist by extension**: PDF, PNG, JPEG, GIF, WebP, TXT, CSV, DOCX, XLSX, PPTX
   (`Documents:Attachments:AllowedExtensions` can narrow it). HTML, SVG, scripts, executables and macro-enabled Office
   files are never accepted.
4. **Content sniffing**: the first bytes must match the extension (`%PDF-`, PNG/JPEG/GIF/WebP signatures, ZIP container
   for Office files, no NUL bytes for text). The stored content type comes from the extension, never from the client.

Errors: `documents.file-too-large`, `documents.file-empty`, `documents.file-type-not-allowed`,
`documents.file-content-mismatch`, `documents.invalid-owner-type`, `documents.owner-required`,
`documents.multipart-required`, `documents.file-required`, `documents.attachment-not-found`, `documents.file-missing`.

### Storage

`IFileStorage` port, chosen by `Documents:Attachments:Storage`:

| Storage | Settings | Notes |
|---|---|---|
| `Local` (default) | `LocalRoot` (default `data/attachments` under the content root; containers use `/var/lib/erp/files/attachments`) | Temp file + atomic move. Keys are validated and resolved paths must stay under the root. |
| `S3` | `S3:Bucket`, `S3:ServiceUrl` (S3-compatible servers), `S3:Region`, `S3:ForcePathStyle`, `S3:Prefix`, optional `S3:AccessKey`/`S3:SecretKey` | AWS SDK; without keys the SDK default credential chain (environment, instance/workload identity) is used. |

Keys are content-addressed per tenant: `{tenantId}/{sha[0..2]}/{sha256}`. Identical content uploaded twice in a tenant is
stored once. The blob is written **before** the database command (outside the transaction, so no transaction is held
open while bytes travel to S3); the write is idempotent, so a failed or retried command leaves at most an
unreferenced blob.

## In-app notifications

Per-user notifications in the tenant (not company-filtered: a user sees all of theirs; each may carry the `companyId`
it is about). Links are relative client routes only.

| Method | Route | Notes |
|---|---|---|
| `GET` | `/api/v1/notifications?unreadOnly=&page=&pageSize=` | The signed-in user's notifications, newest first (paged). |
| `GET` | `/api/v1/notifications/unread-count` | `{ "count": n }` for the bell badge. |
| `POST` | `/api/v1/notifications/{id}/read` | 204; 404 for another user's notification. |
| `POST` | `/api/v1/notifications/read-all` | `{ "marked": n }` (up to 1,000 per call). |

No permission is needed (users only ever see their own); no `X-Company-Id` is needed.

Other modules publish through `INotificationPublisher` (Documents.Contracts), inside their own transaction — typically
an outbox/inbox handler, so the notification is written exactly once after the business transaction committed.
Modules register `NoInAppNotifications` with `TryAdd` so they work without this module.

Published today:

| Category | Source | Recipients | Link |
|---|---|---|---|
| `workflow.approval-requested` | Workflow, when a step is assigned (submission, next step, delegation, escalation) | Approvers of the step (users, or active members of the approver role), except the requester | `/approvals` |

## Limitations

- **Owner documents are not verified**: the owner id is not checked against the owning module, and read access to
  attachments is granted by `documents.attachments.read` alone — not by permission on the owner document. Give the
  permission only to roles that may see documents of every type in the company.
- No virus/malware scanning hook; no image thumbnails or previews; no versioning (upload a new attachment instead).
- Blobs are never garbage-collected (soft delete; content may be shared). Unreferenced blobs from failed uploads stay
  in the store.
- Notifications have no real-time push (the client polls `unread-count`) and no per-user preferences; they are not
  pruned automatically.
- PDF output depends on an external Gotenberg service; there is no embedded PDF engine.
