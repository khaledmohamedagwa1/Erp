using System.Text.RegularExpressions;
using Erp.Application.Abstractions;
using Erp.Modules.Import.Domain;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Import.Application;

public sealed record ImportRow<T>(int Row, T Data);

/// <summary>Job parameters the definitions need (legal entity and opening date for opening-balance types).</summary>
public sealed record ImportContext(Guid CompanyId, Guid? LegalEntityId, DateOnly? OpeningDate)
{
    public static ImportContext From(ImportJob job) => new(job.CompanyId, job.LegalEntityId, job.OpeningDate);
}

/// <summary>
/// A unit of work executed atomically (one savepoint): one row for master data, one document for grouped types
/// (opening stock per warehouse, the GL opening journal). <see cref="DiagnoseRow"/> re-runs a single row of a failed
/// multi-row unit to find which row is at fault.
/// </summary>
public sealed record ImportUnit(IReadOnlyList<int> Rows, Func<CancellationToken, Task<Result>> Execute, Func<int, CancellationToken, Task<Result>>? DiagnoseRow);

public sealed record PreparedImport(int TotalRows, IReadOnlyList<ImportRowError> Errors, IReadOnlyList<ImportUnit> Units);

public interface IImportDefinition
{
    ImportDefinitionInfo Info { get; }

    Task<PreparedImport> PrepareAsync(ImportTable table, ImportContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Parse → file-level checks → resolve codes (through other modules' contracts) → group into units → execute.
/// Definitions never write to another module's tables: execution always calls that module's import contract.
/// </summary>
public abstract class ImportDefinition<T> : IImportDefinition
{
    public abstract ImportDefinitionInfo Info { get; }

    protected virtual bool DiagnosePerRow => true;

    protected abstract T? Parse(ImportRowValues values, ImportContext context);

    /// <summary>Cross-row checks (duplicates, totals). <paramref name="complete"/> is false when some rows failed to parse.</summary>
    protected virtual IReadOnlyList<ImportRowError> CheckFile(IReadOnlyList<ImportRow<T>> rows, ImportContext context, bool complete) => [];

    /// <summary>Resolves codes; returns the rows to execute (with ids filled in) and the rows that failed.</summary>
    protected virtual Task<(IReadOnlyList<ImportRow<T>> Rows, IReadOnlyList<ImportRowError> Errors)> ResolveAsync(IReadOnlyList<ImportRow<T>> rows, ImportContext context,
        CancellationToken cancellationToken) => Task.FromResult<(IReadOnlyList<ImportRow<T>>, IReadOnlyList<ImportRowError>)>((rows, []));

    protected virtual IEnumerable<IReadOnlyList<ImportRow<T>>> Group(IReadOnlyList<ImportRow<T>> rows) => rows.Select(r => (IReadOnlyList<ImportRow<T>>)[r]);

    protected abstract Task<Result> ExecuteAsync(IReadOnlyList<ImportRow<T>> rows, ImportContext context, CancellationToken cancellationToken);

    public async Task<PreparedImport> PrepareAsync(ImportTable table, ImportContext context, CancellationToken cancellationToken)
    {
        var (map, headerErrors) = ImportColumnMap.Build(Info.Columns, table.Headers);
        if (headerErrors.Count > 0)
        {
            return new PreparedImport(table.Rows.Count, headerErrors, []);
        }

        if (table.Rows.Count == 0)
        {
            return new PreparedImport(0, [new ImportRowError(0, null, ImportErrors.FileEmpty.Code, "The file has no data rows.")], []);
        }

        if (table.Rows.Count > ImportLimits.MaxRows)
        {
            return new PreparedImport(table.Rows.Count, [new ImportRowError(0, null, ImportLimits.TooManyRows.Code, ImportLimits.TooManyRows.Message)], []);
        }

        var errors = new List<ImportRowError>();
        var parsed = new List<ImportRow<T>>();
        foreach (var source in table.Rows)
        {
            var values = new ImportRowValues(source, map, Info.Columns);
            var data = Parse(values, context);
            if (values.HasErrors || data is null)
            {
                errors.AddRange(values.Errors);
            }
            else
            {
                parsed.Add(new ImportRow<T>(source.RowNumber, data));
            }
        }

        var fileErrors = CheckFile(parsed, context, complete: errors.Count == 0);
        errors.AddRange(fileErrors);
        if (fileErrors.Any(e => e.Row == 0))
        {
            return new PreparedImport(table.Rows.Count, errors, []);
        }

        var rejected = fileErrors.Select(e => e.Row).ToHashSet();
        parsed = parsed.Where(r => !rejected.Contains(r.Row)).ToList();

        var (resolved, resolveErrors) = await ResolveAsync(parsed, context, cancellationToken);
        errors.AddRange(resolveErrors);

        var units = Group(resolved).Select(group => new ImportUnit(
            group.Select(r => r.Row).ToList(),
            ct => ExecuteAsync(group, context, ct),
            DiagnosePerRow && group.Count > 1 ? (row, ct) => ExecuteAsync([group.First(r => r.Row == row)], context, ct) : null)).ToList();
        return new PreparedImport(table.Rows.Count, errors, units);
    }
}

/// <summary>Savepoints inside the current transaction, so one failing row can be undone without losing the others.</summary>
public interface IImportSavepoints
{
    Task CreateAsync(string name, CancellationToken cancellationToken);

    Task RollbackAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Executes units inside the caller's transaction, each under its own savepoint. A failed unit is rolled back to its savepoint
/// and every module's change tracker is cleared, so nothing half-done leaks into the next unit. Change trackers are also
/// cleared every <see cref="BatchSize"/> units to keep memory flat on large files (everything before is already saved).
/// With <c>keepChanges: false</c> (dry run) every unit is rolled back too; the caller rolls the transaction back anyway.
/// </summary>
public sealed partial class ImportRunner(IImportSavepoints savepoints, IEnumerable<IChangeTrackerReset> resets)
{
    public const int BatchSize = 100;
    private int _savepoint;

    public async Task<(int SucceededRows, IReadOnlyList<ImportRowError> Errors)> RunAsync(IReadOnlyList<ImportUnit> units, CancellationToken cancellationToken)
    {
        var errors = new List<ImportRowError>();
        var succeeded = 0;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            var result = await TryAsync(unit.Execute, keep: true, cancellationToken);
            if (result.IsSuccess)
            {
                succeeded += unit.Rows.Count;
            }
            else
            {
                errors.AddRange(await AttributeAsync(unit, result.Error, cancellationToken));
            }

            if ((i + 1) % BatchSize == 0)
            {
                ClearTrackers();
            }
        }

        return (succeeded, errors);
    }

    private async Task<IReadOnlyList<ImportRowError>> AttributeAsync(ImportUnit unit, Error error, CancellationToken ct)
    {
        // 1. Errors that name a line ("lines[3]...") point straight at a row of the unit.
        var errors = new List<ImportRowError>();
        foreach (var (key, messages) in error.Details ?? new Dictionary<string, string[]>())
        {
            var match = LineKey().Match(key);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var index) && index < unit.Rows.Count)
            {
                errors.AddRange(messages.Select(m => new ImportRowError(unit.Rows[index], null, error.Code, m)));
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        // 2. Otherwise re-run each row alone (always rolled back) to find the culprits.
        if (unit.DiagnoseRow is { } diagnose)
        {
            foreach (var row in unit.Rows)
            {
                var single = await TryAsync(c => diagnose(row, c), keep: false, ct);
                if (single.IsFailure)
                {
                    errors.Add(new ImportRowError(row, null, single.Error.Code, Describe(single.Error)));
                }
            }

            if (errors.Count > 0)
            {
                return errors;
            }
        }

        // 3. A document-level problem (e.g. period closed): reported once, on the unit's first row.
        return [new ImportRowError(unit.Rows[0], null, error.Code, Describe(error))];
    }

    private async Task<Result> TryAsync(Func<CancellationToken, Task<Result>> action, bool keep, CancellationToken ct)
    {
        var name = $"import_{++_savepoint}";
        await savepoints.CreateAsync(name, ct);
        Result result;
        try
        {
            result = await action(ct);
        }
        catch (DomainException ex)
        {
            result = ex.Error;
        }
        catch (DbUpdateException ex)
        {
            result = Error.Conflict("import.database-rejected", (ex.InnerException ?? ex).Message);
        }

        if (result.IsFailure || !keep)
        {
            // If the server already aborted the transaction (e.g. deadlock), this throws and the whole run fails — as it must.
            await savepoints.RollbackAsync(name, ct);
            ClearTrackers();
        }

        return result;
    }

    private void ClearTrackers()
    {
        foreach (var reset in resets)
        {
            reset.ClearTrackedChanges();
        }
    }

    public static string Describe(Error error)
    {
        if (error.Details is not { Count: > 0 } details)
        {
            return error.Message;
        }

        return $"{error.Message} " + string.Join("; ", details.Select(d => $"{d.Key}: {string.Join(" ", d.Value)}"));
    }

    [GeneratedRegex(@"^lines\[(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LineKey();
}
