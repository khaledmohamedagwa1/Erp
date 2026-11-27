using System.Text.Json;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Reporting.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Reporting.Application;

public interface IReportingDbContext
{
    DbSet<SavedReportView> SavedViews { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record SavedViewDto(Guid Id, string Name, string ReportKey, JsonElement Filters, DateTime CreatedAtUtc);

public sealed record ListSavedViewsQuery(string? ReportKey) : IQuery<IReadOnlyList<SavedViewDto>>;

public sealed record CreateSavedViewCommand(string Name, string ReportKey, JsonElement? Filters) : ICommand<SavedViewDto>;

public sealed record DeleteSavedViewCommand(Guid Id) : ICommand<Unit>;

internal static class SavedViewMapping
{
    public static SavedViewDto ToDto(SavedReportView v)
    {
        using var document = JsonDocument.Parse(v.FiltersJson);
        return new SavedViewDto(v.Id, v.Name, v.ReportKey, document.RootElement.Clone(), v.CreatedAtUtc);
    }
}

/// <summary>Saved views are private: every query is restricted to the current user (the company filter adds the company).</summary>
public sealed class ListSavedViewsHandler(IReportingDbContext db, ICurrentUser user) : IQueryHandler<ListSavedViewsQuery, IReadOnlyList<SavedViewDto>>
{
    public async Task<Result<IReadOnlyList<SavedViewDto>>> Handle(ListSavedViewsQuery query, CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? Guid.Empty;
        var source = db.SavedViews.AsNoTracking().Where(v => v.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.ReportKey))
        {
            var key = query.ReportKey.Trim().ToLowerInvariant();
            source = source.Where(v => v.ReportKey == key);
        }

        var views = await source.OrderBy(v => v.ReportKey).ThenBy(v => v.Name).ToListAsync(cancellationToken);
        return views.Select(SavedViewMapping.ToDto).ToList();
    }
}

public sealed class CreateSavedViewHandler(IReportingDbContext db, ICurrentUser user, ITenantContext tenant, IClock clock, ReportService reports)
    : ICommandHandler<CreateSavedViewCommand, SavedViewDto>
{
    public async Task<Result<SavedViewDto>> Handle(CreateSavedViewCommand command, CancellationToken cancellationToken)
    {
        if (tenant.CompanyId is not { } companyId || user.UserId is not { } userId)
        {
            return ReportingErrors.CompanyRequired;
        }

        var key = (command.ReportKey ?? string.Empty).Trim().ToLowerInvariant();
        if (!reports.AllDefinitions.Any(d => d.FullKey == key))
        {
            return ReportingErrors.UnknownReport;
        }

        var name = (command.Name ?? string.Empty).Trim();
        if (await db.SavedViews.AnyAsync(v => v.UserId == userId && v.ReportKey == key && v.Name == name, cancellationToken))
        {
            return Error.Conflict("reporting.view-name-taken", "You already have a saved view with this name for this report.");
        }

        var view = SavedReportView.Create(companyId, userId, name, key, command.Filters?.GetRawText(), clock.UtcNow);
        db.SavedViews.Add(view);
        await db.SaveChangesAsync(cancellationToken);
        return SavedViewMapping.ToDto(view);
    }
}

public sealed class DeleteSavedViewHandler(IReportingDbContext db, ICurrentUser user) : ICommandHandler<DeleteSavedViewCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeleteSavedViewCommand command, CancellationToken cancellationToken)
    {
        var userId = user.UserId ?? Guid.Empty;
        var view = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == command.Id && v.UserId == userId, cancellationToken);
        if (view is null)
        {
            return ReportingErrors.ViewNotFound;
        }

        db.SavedViews.Remove(view);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
