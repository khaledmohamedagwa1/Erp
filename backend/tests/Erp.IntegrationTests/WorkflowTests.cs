using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Workflow.Application;
using Erp.Modules.Workflow.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.IntegrationTests;

/// <summary>Records ApprovalCompleted events delivered from the outbox.</summary>
public sealed class ApprovalCompletedRecorder(ITenantContext tenant) : IIntegrationEventHandler<ApprovalCompleted>
{
    public static readonly ConcurrentBag<(Guid TenantId, ApprovalCompleted Event)> Received = [];

    public Task HandleAsync(ApprovalCompleted integrationEvent, CancellationToken cancellationToken)
    {
        Received.Add((tenant.TenantId!.Value, integrationEvent));
        return Task.CompletedTask;
    }
}

public class WorkflowTests(ErpApiFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record User(Guid Id, string Email, HttpClient Client);

    private sealed record Setup(ErpApiFixture.TenantSession T, Guid FinanceRoleId, User Fm1, User Fm2, User Deputy);

    private async Task<User> CreateUserAsync(ErpApiFixture.TenantSession t, string name, Guid roleId)
    {
        var email = $"{name}@{t.TenantCode.ToLowerInvariant()}.example";
        const string password = "Approver-Passw0rd!";
        var created = await t.Client.PostAsJsonAsync("/api/v1/identity/users",
            new { email, displayName = name, password, roleIds = new[] { roleId }, companyIds = new[] { t.CompanyId } }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var client = fixture.CreateClient();
        await ErpApiFixture.SignInAsync(client, t.TenantCode, email, password);
        return new User(id, email, client);
    }

    private async Task<Guid> CreateRoleAsync(ErpApiFixture.TenantSession t, string name, params string[] permissions)
    {
        var role = await t.Client.PostAsJsonAsync("/api/v1/identity/roles", new { name, permissions }, Ct);
        role.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await role.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<Setup> NewSetupAsync(int? escalateAfterHours = null)
    {
        var t = await fixture.NewTenantAsync();
        var financeRole = await CreateRoleAsync(t, "Finance Manager", WorkflowPermissions.Approve, WorkflowPermissions.DefinitionRead);
        var approverRole = await CreateRoleAsync(t, "Approver", WorkflowPermissions.Approve);
        var fm1 = await CreateUserAsync(t, "fm1", financeRole);
        var fm2 = await CreateUserAsync(t, "fm2", financeRole);
        var deputy = await CreateUserAsync(t, "deputy", approverRole);

        var definition = await t.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/workflow/definitions", t.CompanyId, new
        {
            documentType = ApprovalDocumentTypes.SalesInvoice,
            name = "Sales invoice above 50,000",
            priority = 10,
            conditions = new[] { new { fact = "amount", @operator = "GreaterThan", value = "50000" } },
            steps = new[]
            {
                new
                {
                    sequence = 1, approverKind = "Role", approverId = financeRole, requiredApprovals = 1,
                    escalateAfterHours, escalateToKind = escalateAfterHours is null ? null : "User", escalateToId = escalateAfterHours is null ? (Guid?)null : deputy.Id,
                },
            },
        });
        definition.StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Setup(t, financeRole, fm1, fm2, deputy);
    }

    /// <summary>Submits through the contract, inside a transaction, exactly as a module would when a document is submitted.</summary>
    private async Task<(ApprovalDecision Decision, Guid DocumentId)> SubmitAsync(Setup s, decimal amount, Guid requestedBy, string currency = "EGP")
    {
        var documentId = Guid.NewGuid();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.SetTenant(s.T.TenantId);
        tenant.SetCompany(s.T.CompanyId);
        var session = scope.ServiceProvider.GetRequiredService<ErpDbSession>();
        await session.BeginTransactionAsync(Ct);
        var decision = await scope.ServiceProvider.GetRequiredService<IApprovalService>().SubmitAsync(
            new ApprovalRequest(ApprovalDocumentTypes.SalesInvoice, documentId, $"DRAFT-{documentId.ToString()[..4]}", s.T.CompanyId, amount, currency, requestedBy), Ct);
        await session.CommitAsync(Ct);
        return (decision, documentId);
    }

    private async Task<JsonElement> InboxAsync(Setup s, User user) =>
        await (await user.Client.GetAsCompanyAsync("/api/v1/workflow/inbox", s.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task DrainOutboxAsync()
    {
        var processor = fixture.Factory.Services.GetRequiredService<IOutboxProcessor>();
        for (var i = 0; i < 10 && await processor.ProcessPendingAsync(Ct) > 0; i++)
        {
        }
    }

    [Fact]
    public async Task Invoices_above_the_threshold_need_a_finance_manager_and_approval_is_published()
    {
        var s = await NewSetupAsync();
        (await SubmitAsync(s, 40_000m, s.Fm2.Id)).Decision.Required.ShouldBeFalse();

        var (decision, documentId) = await SubmitAsync(s, 60_000m, s.Fm2.Id);
        decision.Required.ShouldBeTrue();
        var instanceId = decision.InstanceId!.Value;

        // Approvers of the step are emailed (outbox → notifier); the requester is not, even though they hold the role.
        await DrainOutboxAsync();
        var documentNumber = $"DRAFT-{documentId.ToString()[..4]}";
        RecordingNotificationSender.Sent.ShouldContain(m => m.To == s.Fm1.Email && m.Subject.Contains(documentNumber));
        RecordingNotificationSender.Sent.ShouldNotContain(m => m.To == s.Fm2.Email && m.Subject.Contains(documentNumber));

        // ...and get an in-app notification linking to the approvals inbox (only the approver, not the requester).
        var bell = await (await s.Fm1.Client.GetAsync("/api/v1/notifications?unreadOnly=true", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var notification = bell.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("title").GetString()!.Contains(documentNumber));
        notification.GetProperty("category").GetString().ShouldBe("workflow.approval-requested");
        notification.GetProperty("link").GetString().ShouldBe("/approvals");
        notification.GetProperty("companyId").GetGuid().ShouldBe(s.T.CompanyId);
        (await (await s.Fm2.Client.GetAsync("/api/v1/notifications", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // The approver sees it; the requester (also a Finance Manager) does not, and cannot approve it.
        var inbox = await InboxAsync(s, s.Fm1);
        inbox.EnumerateArray().Single().GetProperty("instanceId").GetGuid().ShouldBe(instanceId);
        (await InboxAsync(s, s.Fm2)).GetArrayLength().ShouldBe(0);
        var own = await s.Fm2.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/instances/{instanceId}/approve", s.T.CompanyId, new { });
        own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await own.ErrorCodeAsync()).ShouldBe("workflow.own-document");

        // Someone outside the step cannot decide.
        var outsider = await s.Deputy.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/instances/{instanceId}/approve", s.T.CompanyId, new { });
        (await outsider.ErrorCodeAsync()).ShouldBe("workflow.not-an-approver");

        var approved = await s.Fm1.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/instances/{instanceId}/approve", s.T.CompanyId, new { comment = "OK" });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Approved");

        await DrainOutboxAsync();
        ApprovalCompletedRecorder.Received.ShouldContain(r => r.TenantId == s.T.TenantId && r.Event.DocumentId == documentId && r.Event.Approved && r.Event.DecidedBy == s.Fm1.Id);
    }

    [Fact]
    public async Task Rejection_requires_a_reason_and_is_published()
    {
        var s = await NewSetupAsync();
        var (decision, documentId) = await SubmitAsync(s, 75_000m, s.Fm2.Id);
        var url = $"/api/v1/workflow/instances/{decision.InstanceId}/reject";

        var noReason = await s.Fm1.Client.SendAsCompanyAsync(HttpMethod.Post, url, s.T.CompanyId, new { reason = "" });
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await noReason.ErrorCodeAsync()).ShouldBe("workflow.reason-required");

        var rejected = await s.Fm1.Client.SendAsCompanyAsync(HttpMethod.Post, url, s.T.CompanyId, new { reason = "Customer over credit limit" });
        (await rejected.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Rejected");

        await DrainOutboxAsync();
        ApprovalCompletedRecorder.Received.ShouldContain(r => r.Event.DocumentId == documentId && !r.Event.Approved && r.Event.Reason == "Customer over credit limit");
    }

    [Fact]
    public async Task A_standing_delegation_lets_a_deputy_approve_on_behalf_of_the_approver()
    {
        var s = await NewSetupAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var delegation = await s.Fm1.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/workflow/delegations", s.T.CompanyId,
            new { toUserId = s.Deputy.Id, validFrom = today.AddDays(-1), validTo = today.AddDays(7) });
        delegation.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Delegating on behalf of someone else needs the manage permission.
        var forOther = await s.Deputy.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/workflow/delegations", s.T.CompanyId,
            new { fromUserId = s.Fm1.Id, toUserId = s.Deputy.Id, validFrom = today, validTo = today });
        forOther.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var (decision, _) = await SubmitAsync(s, 90_000m, s.Fm2.Id);
        var item = (await InboxAsync(s, s.Deputy)).EnumerateArray().Single();
        item.GetProperty("onBehalfOf").GetGuid().ShouldBe(s.Fm1.Id);

        var approved = await s.Deputy.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/instances/{decision.InstanceId}/approve", s.T.CompanyId, new { });
        var body = await approved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("Approved");
        var approval = body.GetProperty("history").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "Approved");
        approval.GetProperty("userId").GetGuid().ShouldBe(s.Deputy.Id);
        approval.GetProperty("onBehalfOf").GetGuid().ShouldBe(s.Fm1.Id);
    }

    [Fact]
    public async Task An_overdue_step_escalates_to_the_configured_user()
    {
        var s = await NewSetupAsync(escalateAfterHours: 4);
        var (decision, _) = await SubmitAsync(s, 120_000m, s.Fm2.Id);
        (await InboxAsync(s, s.Deputy)).GetArrayLength().ShouldBe(0);

        async Task<int> EscalateAsync(DateTime asOf)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(s.T.TenantId);
            return (await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new EscalateOverdueCommand(asOf), Ct)).Value;
        }

        (await EscalateAsync(DateTime.UtcNow.AddHours(3))).ShouldBe(0);
        (await EscalateAsync(DateTime.UtcNow.AddHours(5))).ShouldBe(1);
        (await EscalateAsync(DateTime.UtcNow.AddHours(9))).ShouldBe(0);

        (await InboxAsync(s, s.Deputy)).EnumerateArray().Single().GetProperty("instanceId").GetGuid().ShouldBe(decision.InstanceId!.Value);
        var approved = await s.Deputy.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/instances/{decision.InstanceId}/approve", s.T.CompanyId, new { });
        (await approved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Approved");

        // The admin endpoint runs the same job for the current tenant (nothing is due right now).
        var run = await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/workflow/escalations/run", s.T.CompanyId, new { });
        (await run.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("escalated").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Foreign_currency_amounts_are_converted_before_rules_are_evaluated()
    {
        var s = await NewSetupAsync();
        foreach (var (code, name) in new[] { ("EGP", "Egyptian pound"), ("USD", "US dollar") })
        {
            await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/currencies", s.T.CompanyId, new { code, nameEn = name, nameAr = name, minorUnits = 2 });
        }

        // No rate yet: the submission fails rather than silently skipping approval.
        await Should.ThrowAsync<Erp.SharedKernel.Domain.DomainException>(() => SubmitAsync(s, 1_000m, s.Fm2.Id, "USD"));

        var rate = await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, "/api/v1/tax/exchange-rates", s.T.CompanyId,
            new { fromCurrency = "USD", toCurrency = "EGP", effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), rate = 60m });
        rate.IsSuccessStatusCode.ShouldBeTrue(await rate.Content.ReadAsStringAsync(Ct));

        (await SubmitAsync(s, 800m, s.Fm2.Id, "USD")).Decision.Required.ShouldBeFalse(); // 48,000 EGP
        var (decision, _) = await SubmitAsync(s, 1_000m, s.Fm2.Id, "USD");              // 60,000 EGP
        decision.Required.ShouldBeTrue();
        var instance = await (await s.Fm1.Client.GetAsCompanyAsync($"/api/v1/workflow/instances/{decision.InstanceId}", s.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        instance.GetProperty("amountBase").GetDecimal().ShouldBe(60_000m);
        instance.GetProperty("currency").GetString().ShouldBe("USD");
    }

    [Fact]
    public async Task Resubmitting_replaces_the_open_approval_and_definitions_need_the_current_version()
    {
        var s = await NewSetupAsync();
        var documentId = Guid.NewGuid();
        async Task<ApprovalDecision> Submit(decimal amount)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenant.SetTenant(s.T.TenantId);
            var session = scope.ServiceProvider.GetRequiredService<ErpDbSession>();
            await session.BeginTransactionAsync(Ct);
            var result = await scope.ServiceProvider.GetRequiredService<IApprovalService>().SubmitAsync(
                new ApprovalRequest(ApprovalDocumentTypes.SalesInvoice, documentId, "DRAFT-R", s.T.CompanyId, amount, "EGP", s.Fm2.Id), Ct);
            await session.CommitAsync(Ct);
            return result;
        }

        var first = await Submit(60_000m);
        var second = await Submit(70_000m);
        second.InstanceId.ShouldNotBe(first.InstanceId);
        var history = await (await s.T.Client.GetAsCompanyAsync($"/api/v1/workflow/instances?documentId={documentId}", s.T.CompanyId))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        history.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Order().ShouldBe(["Pending", "Withdrawn"]);

        var definitions = await (await s.T.Client.GetAsCompanyAsync("/api/v1/workflow/definitions", s.T.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var definitionId = definitions.EnumerateArray().Single().GetProperty("id").GetGuid();
        var get = await s.T.Client.GetAsCompanyAsync($"/api/v1/workflow/definitions/{definitionId}", s.T.CompanyId);
        var etag = get.Headers.ETag!.Tag;
        var update = new
        {
            name = "Sales invoice above 80,000", priority = 10,
            conditions = new[] { new { fact = "amount", @operator = "GreaterThan", value = "80000" } },
            steps = new[] { new { sequence = 1, approverKind = "Role", approverId = s.FinanceRoleId, requiredApprovals = 1 } },
        };
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/workflow/definitions/{definitionId}", s.T.CompanyId, update)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionRequired);
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/workflow/definitions/{definitionId}", s.T.CompanyId, update, etag)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Put, $"/api/v1/workflow/definitions/{definitionId}", s.T.CompanyId, update, etag)).StatusCode
            .ShouldBe(HttpStatusCode.PreconditionFailed);

        (await SubmitAsync(s, 70_000m, s.Fm2.Id)).Decision.Required.ShouldBeFalse();
        (await s.T.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/definitions/{definitionId}/deactivate", s.T.CompanyId, new { })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await SubmitAsync(s, 500_000m, s.Fm2.Id)).Decision.Required.ShouldBeFalse();
    }

    [Fact]
    public async Task Approvals_are_isolated_between_tenants()
    {
        var a = await NewSetupAsync();
        var b = await fixture.NewTenantAsync();
        var (decision, _) = await SubmitAsync(a, 60_000m, a.Fm2.Id);

        (await b.Client.GetAsCompanyAsync($"/api/v1/workflow/instances/{decision.InstanceId}", b.CompanyId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.SendAsCompanyAsync(HttpMethod.Post, $"/api/v1/workflow/instances/{decision.InstanceId}/approve", b.CompanyId, new { })).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await (await b.Client.GetAsCompanyAsync("/api/v1/workflow/definitions", b.CompanyId)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetArrayLength().ShouldBe(0);
    }
}
