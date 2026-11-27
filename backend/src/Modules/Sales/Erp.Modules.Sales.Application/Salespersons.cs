using Erp.Application.Messaging;
using Erp.Modules.Parties.Contracts;
using Erp.Modules.People.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Sales.Application;

public static class SalespersonErrors
{
    public static readonly Error Invalid = Error.Validation("sales.salesperson-invalid", "The salesperson must be an active employee flagged as salesperson.");
}

/// <summary>
/// Chooses a document's salesperson: the one requested (must be an active salesperson), otherwise the source document's, otherwise the
/// customer's default salesperson while that employee can still sell, otherwise none.
/// </summary>
public sealed class SalespersonResolver(IServiceProvider services)
{
    public async Task<Result<Guid?>> ResolveAsync(Guid? requested, Guid? inherited, CustomerInfo? customer, CancellationToken ct)
    {
        var employees = services.GetService<IEmployeeQueries>();
        if (requested is { } id)
        {
            return employees is not null && await employees.GetEmployeeAsync(id, ct) is { CanSell: true } ? id : SalespersonErrors.Invalid;
        }

        if (inherited is not null)
        {
            return inherited;
        }

        if (customer?.SalespersonId is { } fallback && employees is not null && await employees.GetEmployeeAsync(fallback, ct) is { CanSell: true })
        {
            return fallback;
        }

        return (Guid?)null;
    }
}

public enum SalespersonDocument
{
    Quotation,
    Order,
    Invoice,
}

/// <summary>Changes the salesperson of a quotation or order in Draft, or of an invoice not yet posted (null clears it).</summary>
public sealed record SetSalespersonCommand(SalespersonDocument Document, Guid Id, Guid? SalespersonId) : ICommand<SalesDocumentDto>;

public sealed class SetSalespersonHandler(ISalesDbContext db, SalespersonResolver resolver) : ICommandHandler<SetSalespersonCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(SetSalespersonCommand c, CancellationToken ct)
    {
        if (c.SalespersonId is not null)
        {
            var valid = await resolver.ResolveAsync(c.SalespersonId, null, null, ct);
            if (valid.IsFailure)
            {
                return valid.Error;
            }
        }

        switch (c.Document)
        {
            case SalespersonDocument.Quotation:
            {
                var q = await db.Quotations.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
                if (q is null)
                {
                    return SalesAppErrors.NotFound;
                }

                if (q.Status != QuotationStatus.Draft)
                {
                    return SalesErrors.InvalidTransition("quotation", q.Status.ToString(), "edited");
                }

                q.AssignSalesperson(c.SalespersonId);
                await db.SaveChangesAsync(ct);
                return SalesMapping.ToDto(q);
            }

            case SalespersonDocument.Order:
            {
                var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
                if (o is null)
                {
                    return SalesAppErrors.NotFound;
                }

                if (o.Status != SalesOrderStatus.Draft)
                {
                    return SalesErrors.InvalidTransition("sales order", o.Status.ToString(), "edited");
                }

                o.AssignSalesperson(c.SalespersonId);
                await db.SaveChangesAsync(ct);
                return SalesMapping.ToDto(o);
            }

            default:
            {
                var i = await db.Invoices.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
                if (i is null)
                {
                    return SalesAppErrors.NotFound;
                }

                if (i.IsPosted || i.Status == SalesInvoiceStatus.Cancelled)
                {
                    return SalesErrors.InvalidTransition("sales invoice", i.Status.ToString(), "edited");
                }

                i.AssignSalesperson(c.SalespersonId);
                await db.SaveChangesAsync(ct);
                return SalesMapping.ToDto(i);
            }
        }
    }
}

/// <summary>Closes a fully paid invoice on request (no further credit notes); Finance reopens it if a receipt is reversed.</summary>
public sealed record CloseInvoiceCommand(Guid Id) : ICommand<SalesDocumentDto>;

public sealed class CloseInvoiceHandler(ISalesDbContext db) : ICommandHandler<CloseInvoiceCommand, SalesDocumentDto>
{
    public async Task<Result<SalesDocumentDto>> Handle(CloseInvoiceCommand c, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
        if (invoice is null)
        {
            return SalesAppErrors.NotFound;
        }

        if (invoice.Status == SalesInvoiceStatus.Closed)
        {
            return SalesMapping.ToDto(invoice);
        }

        if (await db.Returns.AnyAsync(r => r.SalesInvoiceId == invoice.Id && r.Status == SalesReturnStatus.Draft, ct))
        {
            return Error.Conflict("sales.invoice-has-draft-returns", "Post or cancel the draft credit notes of this invoice before closing it.");
        }

        try
        {
            invoice.Close();
        }
        catch (Erp.SharedKernel.Domain.DomainException ex)
        {
            return ex.Error;
        }

        await db.SaveChangesAsync(ct);
        return SalesMapping.ToDto(invoice);
    }
}
