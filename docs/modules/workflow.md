# Workflow (approvals) module

Schema `workflow` · migration order 140 · module code `workflow` (core) · spec Part 16.

A reusable approval engine. Modules ask it whether a document needs approval; it never posts anything. When an approval
completes, the engine publishes `ApprovalCompleted` through the outbox and the owning module moves its document to
Approved (or back to Draft on rejection).

## Contract (`Erp.Modules.Workflow.Contracts`)

| Member | Use |
|---|---|
| `IApprovalService.SubmitAsync(ApprovalRequest)` | Called by a module inside its own transaction when a document is submitted. Returns `ApprovalDecision(Required, InstanceId)`. `Required = false` → no rule matched; the module approves the document itself. |
| `IApprovalService.WithdrawAsync(documentType, documentId)` | Withdraws an open approval (document edited/cancelled). No-op when none. |
| `ApprovalCompleted(DocumentType, DocumentId, Approved, DecidedBy, Reason)` | Outbox event. Handle it (ideally with `InboxIntegrationEventHandler<T>`) to move the document. |
| `ApprovalDocumentTypes` | Exact document-type strings. |
| `WorkflowPermissions` | `workflow.definition.read`, `workflow.definition.manage` (sensitive), `workflow.approve`. |
| `NoApprovalRequired` | Default modules register with `TryAddScoped`; the Workflow module registers the real engine with `AddScoped`, so it always wins. |

`ApprovalRequest` carries the facts rules are evaluated against: `Amount` + `Currency`, optional `BranchId`,
`NumericFacts` (e.g. `discountPercent`) and `TextFacts` (e.g. `customerGroup`). The facts are snapshotted on the instance.

## Rules

- **Definitions** (per company and document type): name, priority (lower first), active flag, **conditions** (all must hold)
  and ordered **steps**.
- **Conditions**: fact `amount` (always compared in the company's **base currency** — other currencies are converted at
  today's spot rate via `IExchangeRateProvider`; a missing rate fails the submission with `workflow.exchange-rate-missing`
  rather than skipping approval), `branch`, or any numeric/text fact. Operators `GreaterThan`, `GreaterOrEqual`, `LessThan`,
  `LessOrEqual`, `Equal`, `In` (comma-separated list). A fact the request does not supply makes the condition false.
- **Selection**: active definitions for the document type, ordered by priority, then name; the **first** whose conditions all
  hold creates the instance.
- **Steps**: approver = `Role` (Identity role id) or `User`; `requiredApprovals` = quorum of **distinct** approvers;
  optional escalation after N hours to a role or user.
- **Segregation of duties**: the requester can never approve or reject their own document — not directly, not under a
  delegation, and nobody can act on the requester's behalf. Requesters also never receive the notification and never see it in their inbox.
- **Resubmission**: submitting a document that already has a pending approval withdraws the old instance first
  (one pending instance per document is enforced by a filtered unique index).
- Instances keep a **snapshot** of the definition's steps, so editing or deleting a definition never changes a running approval.

## Delegation and escalation

- **Ad-hoc delegation** of one instance (`POST instances/{id}/delegate`): an eligible approver adds another user as an approver
  of the current step (never the requester).
- **Standing delegation** (`delegations`): from user → to user for an inclusive date range, optionally per document type.
  While active, the delegate may decide whatever the delegator may decide; the action is recorded with `onBehalfOf`, and
  for the quorum it counts as the delegator's approval. Users delegate their own approvals; delegating for someone else needs
  `workflow.definition.manage`.
- **Escalation**: a step that waits longer than its escalation time gets the escalation target added as an approver
  (once per step). The worker (`Erp:Role` = `worker`/`all`) runs it every `Workflow:EscalationIntervalMinutes` (default 15)
  for every active tenant; administrators can trigger it for their tenant with `POST escalations/run`.

## Notifications

`ApprovalStepAssigned` (submission, next step, delegation, escalation) is delivered from the outbox to a notifier that emails
the current step's approvers through `INotificationSender`, at most once per event (inbox). Steps already decided are skipped.

## Endpoints (`/api/v1/workflow`, company required)

| Method & path | Permission | Notes |
|---|---|---|
| `GET /definitions?documentType=` | `workflow.definition.read` | |
| `GET /definitions/{id}` | `workflow.definition.read` | Returns `ETag`. |
| `POST /definitions` | `workflow.definition.manage` | `{documentType, name, priority, conditions[{fact, operator, value}], steps[{sequence, approverKind, approverId, requiredApprovals, escalateAfterHours?, escalateToKind?, escalateToId?}]}` |
| `PUT /definitions/{id}` | `workflow.definition.manage` | Same body (document type cannot change); `If-Match` required (428/412). |
| `POST /definitions/{id}/activate`, `/deactivate` | `workflow.definition.manage` | |
| `DELETE /definitions/{id}` | `workflow.definition.manage` | Running instances are unaffected. |
| `GET /inbox` | `workflow.approve` | Pending approvals the user may decide now (directly, via role, as assigned/escalated approver, or `onBehalfOf` a delegator), with facts. |
| `GET /instances?status&documentType&documentId&page&pageSize` | `workflow.definition.read` | Paged history. |
| `GET /instances/{id}` | `workflow.approve` | Detail with steps, assigned approvers and full action history. |
| `POST /instances/{id}/approve` | `workflow.approve` | `{comment?}`. Errors: `workflow.own-document` (403), `workflow.not-an-approver` (403), `workflow.already-approved`, `workflow.not-pending` (409). |
| `POST /instances/{id}/reject` | `workflow.approve` | `{reason}` — required (`workflow.reason-required`, 422). |
| `POST /instances/{id}/delegate` | `workflow.approve` | `{toUserId, comment?}`. |
| `GET /delegations?all=` | `workflow.approve` | Own (given or received); `all=true` lists the company's with manage permission. |
| `POST /delegations` | `workflow.approve` | `{fromUserId?, toUserId, validFrom, validTo, documentType?}`. |
| `DELETE /delegations/{id}` | `workflow.approve` | Revokes own (any with manage permission). |
| `POST /escalations/run` | `workflow.definition.manage` | `{escalated: n}` for the current tenant. |

## Contracts added elsewhere (additive)

- `Identity.Contracts.IUserDirectory` (role ids of a user, users in a role, users by id) — implemented in Identity.Application.
- `ITenantDirectory.ListActiveAsync` (default interface method) — implemented by Organization's `TenantDirectory`; used by the escalation job.

## Limitations

- Conditions are AND-only; OR is expressed with several definitions.
- Amount conversion uses the spot rate of the submission date.
- Escalation adds an approver; it does not reassign or auto-approve.
