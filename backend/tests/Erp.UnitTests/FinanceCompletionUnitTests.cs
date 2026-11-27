using Erp.Modules.Accounting.Domain;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Tax.Domain;
using Erp.SharedKernel.Domain;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class FinanceCompletionUnitTests
{
    private static OpenItem Item(decimal amount = 1000m, decimal baseAmount = 50_000m) =>
        OpenItem.Create(Guid.NewGuid(), Guid.NewGuid(), PartyKind.Customer, Guid.NewGuid(), "DEFAULT", "sales", "SalesInvoice", Guid.NewGuid(), "INV-1",
            new DateOnly(2026, 3, 1), 1, new DateOnly(2026, 3, 31), "USD", 50m, 1, amount, baseAmount);

    [Fact]
    public void Unsettle_restores_amount_and_base_and_status()
    {
        var item = Item();
        var relieved = item.Settle(400m);
        item.Status.ShouldBe(OpenItemStatus.PartiallySettled);
        item.Unsettle(400m, relieved);
        item.OpenAmount.ShouldBe(1000m);
        item.OpenBase.ShouldBe(50_000m);
        item.Status.ShouldBe(OpenItemStatus.Open);
        Should.Throw<DomainException>(() => item.Unsettle(1m, 50m));
    }

    [Fact]
    public void Allocation_reversal_negates_every_amount_and_points_to_the_original()
    {
        var item = Item();
        var relieved = item.Settle(1000m);
        var allocation = Allocation.Create(item, SettlementKind.Receipt, Guid.NewGuid(), "RCT-1", new DateOnly(2026, 4, 5), 1000m, relieved, 51_000m, 51_000m);
        allocation.RealizedFxBase.ShouldBe(1_000m);
        var reversal = allocation.CreateReversal(new DateOnly(2026, 4, 9), "REV RCT-1");
        reversal.Amount.ShouldBe(-1000m);
        reversal.BaseRelieved.ShouldBe(-50_000m);
        reversal.SettlementBase.ShouldBe(-51_000m);
        reversal.RealizedFxBase.ShouldBe(-1_000m);
        reversal.SettlementAmount.ShouldBe(-51_000m);
        reversal.ReversalOfId.ShouldBe(allocation.Id);
        reversal.SettlementId.ShouldBe(allocation.SettlementId);
    }

    [Fact]
    public void Settlement_reversal_needs_a_reason_and_a_date_on_or_after_the_document()
    {
        var settlement = Settlement.Create(Guid.NewGuid(), Guid.NewGuid(), SettlementDirection.Receipt, Guid.NewGuid(), new DateOnly(2026, 4, 5), PaymentMethod.Cash,
            Guid.NewGuid(), "EGP", 1m, 500m, null, null);
        Should.Throw<DomainException>(() => settlement.MarkReversed(new DateOnly(2026, 4, 6), "x")).Error.Code.ShouldBe("finance.not-posted");
        settlement.MarkPosted("RCT-1", Guid.NewGuid(), 500m);
        Should.Throw<DomainException>(() => settlement.MarkReversed(new DateOnly(2026, 4, 6), " ")).Error.Code.ShouldBe("finance.reversal-reason-required");
        Should.Throw<DomainException>(() => settlement.MarkReversed(new DateOnly(2026, 4, 1), "x")).Error.Code.ShouldBe("finance.reversal-before-document");
        settlement.MarkReversed(new DateOnly(2026, 4, 6), "Bounced");
        settlement.Status.ShouldBe(DocumentStatus.Reversed);
        settlement.UnallocatedAmount.ShouldBe(0m);
    }

    [Fact]
    public void Cross_currency_requested_allocations_count_in_the_settlement_currency()
    {
        var settlement = Settlement.Create(Guid.NewGuid(), Guid.NewGuid(), SettlementDirection.Receipt, Guid.NewGuid(), new DateOnly(2026, 4, 5), PaymentMethod.Cash,
            Guid.NewGuid(), "EGP", 1m, 5000m, null, null);
        settlement.SetAllocations([new RequestedAllocation(Guid.NewGuid(), 100m, 5000m)]);
        Should.Throw<DomainException>(() => settlement.SetAllocations([new RequestedAllocation(Guid.NewGuid(), 100m, 5000.01m)]));
    }

    [Fact]
    public void Withholding_certificate_totals_lines_in_functional_currency()
    {
        var lines = new[]
        {
            new WithholdingCertificateLine(Guid.NewGuid(), "PAY-2", new DateOnly(2026, 3, 20), null, "USD", 50m, 100m, 1m),
            new WithholdingCertificateLine(Guid.NewGuid(), "PAY-1", new DateOnly(2026, 3, 2), null, "EGP", 1m, 6100m, 61m),
        };
        var certificate = WithholdingCertificate.Create(Guid.NewGuid(), Guid.NewGuid(), WithholdingCertificateKind.Issued, Guid.NewGuid(), "WHT-1",
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 5), null, null, lines);
        certificate.TotalTaxableBase.ShouldBe(11_100m);
        certificate.TotalWithheldBase.ShouldBe(111m);
        certificate.Lines.First().SettlementNumber.ShouldBe("PAY-1");
        certificate.PartyType.ShouldBe(PartyKind.Supplier);
        Should.Throw<DomainException>(() => WithholdingCertificate.Create(Guid.NewGuid(), Guid.NewGuid(), WithholdingCertificateKind.Issued, Guid.NewGuid(), "WHT-2",
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 5), null, null, []));
    }

    [Fact]
    public void Income_statement_group_must_fit_the_account_type()
    {
        var revenue = Account.Create(Guid.NewGuid(), "4999", "Other", "أخرى", AccountType.Revenue, null, true);
        revenue.ReportGroup.ShouldBe(IncomeStatementGroup.Revenue);
        revenue.SetReportGroup(IncomeStatementGroup.OtherIncome);
        Should.Throw<DomainException>(() => revenue.SetReportGroup(IncomeStatementGroup.CostOfSales));
        var asset = Account.Create(Guid.NewGuid(), "1999", "Asset", "أصل", AccountType.Asset, null, true);
        asset.ReportGroup.ShouldBe(IncomeStatementGroup.None);
        Should.Throw<DomainException>(() => asset.SetReportGroup(IncomeStatementGroup.Revenue));
    }

    [Fact]
    public void Base_only_lines_balance_foreign_currency_journals()
    {
        var journal = JournalEntry.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 4, 5), "finance", "Receipt", Guid.NewGuid(), "post", "Receipt", "USD", 51m);
        var cash = Guid.NewGuid();
        var receivable = Guid.NewGuid();
        var gain = Guid.NewGuid();
        journal.AddLine(cash, 1000m, 0m);
        journal.AddLine(receivable, 0m, 1000m, baseAmount: 50_000m);
        journal.AddBaseOnlyLine(gain, 0m, 1_000m, postingRole: "RealizedFxGain");
        journal.CheckBalance().Value.ShouldBeFalse();
        journal.TotalBaseDebit.ShouldBe(51_000m);
        journal.TotalBaseCredit.ShouldBe(51_000m);
        journal.Lines.Last().PostingRole.ShouldBe("RealizedFxGain");
        Should.Throw<DomainException>(() => journal.AddBaseOnlyLine(gain, 1m, 1m));
    }

    [Fact]
    public void E_invoice_chain_links_each_document_to_the_previous_hash()
    {
        var settings = EInvoicingSettings.Create(Guid.NewGuid(), true, "mock", "sandbox");
        var (first, previous1) = settings.Advance((counter, prev) => $"H{counter}:{prev}");
        var (second, previous2) = settings.Advance((counter, prev) => $"H{counter}:{prev}");
        (first, previous1).ShouldBe((1L, (string?)null));
        (second, previous2).ShouldBe((2L, "H1:"));
        settings.LastHash.ShouldBe("H2:H1:");

        var submission = EInvoiceSubmission.Create(Guid.NewGuid(), Guid.NewGuid(), "sales", "SalesInvoice", Guid.NewGuid(), "INV-1", DateTime.UtcNow);
        submission.DomainEvents.OfType<EInvoiceSubmissionQueued>().ShouldHaveSingleItem();
        submission.SetBuilt("{}", 2, "H2", "H1", "mock");
        submission.Reject(["bad"], DateTime.UtcNow);
        var uuid = submission.Uuid;
        submission.Retry();
        submission.Status.ShouldBe(EInvoiceStatus.Pending);
        submission.IsBuilt.ShouldBeFalse();
        submission.Uuid.ShouldNotBe(uuid);
        submission.Accept("R", null, DateTime.UtcNow);
        Should.Throw<DomainException>(submission.Retry);
    }
}
