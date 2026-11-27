using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.Modules.Tax.Domain;
using Erp.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Tax.Application;

public sealed record TemplateResult(int CurrenciesCreated, int TaxCodesCreated, int TaxGroupsCreated, int MappingsCreated);

/// <summary>
/// Seeds a starting tax setup for Egypt (decisions log, MR-1/OQ-TAX-1). Idempotent: anything that already exists by code is
/// left untouched, so running it twice changes nothing. Rates must be confirmed by a tax adviser before go-live.
/// </summary>
public sealed record ApplyEgyptTemplateCommand(DateOnly? EffectiveFrom, decimal? TableTaxRate) : ICommand<TemplateResult>;

public sealed class ApplyEgyptTemplateHandler(ITaxDbContext db, ITenantContext tenant) : ICommandHandler<ApplyEgyptTemplateCommand, TemplateResult>
{
    /// <summary>VAT at 14 % applies from the 2017/18 fiscal year (Law 67/2016 rate step-up).</summary>
    public static readonly DateOnly DefaultEffectiveFrom = new(2017, 7, 1);

    private sealed record CodeSpec(string Code, string NameEn, string NameAr, TaxKind Kind, bool Recoverable, string ReportingBox, decimal Rate);

    public async Task<Result<TemplateResult>> Handle(ApplyEgyptTemplateCommand command, CancellationToken cancellationToken)
    {
        var companyId = tenant.CompanyId!.Value;
        var from = command.EffectiveFrom ?? DefaultEffectiveFrom;
        var tableRate = command.TableTaxRate ?? 0m;
        if (tableRate is < 0 or > 100)
        {
            return Error.Validation("tax.rate-invalid", "Table tax rate must be between 0 and 100.");
        }

        int currencies = 0, codesCreated = 0, groupsCreated = 0, mappingsCreated = 0;

        if (!await db.Currencies.AnyAsync(c => c.Code == "EGP", cancellationToken))
        {
            db.Currencies.Add(Currency.Create("EGP", "Egyptian pound", "جنيه مصري", 2, 0.01m));
            currencies++;
        }

        CodeSpec[] specs =
        [
            new("VAT14-OUT", "VAT 14% (sales)", "ضريبة القيمة المضافة 14% (مبيعات)", TaxKind.Output, false, "VAT-STANDARD", 14m),
            new("VAT14-IN", "VAT 14% (purchases)", "ضريبة القيمة المضافة 14% (مشتريات)", TaxKind.Input, true, "VAT-INPUT", 14m),
            new("VAT0-OUT", "VAT zero-rated (sales)", "ضريبة القيمة المضافة بسعر صفر (مبيعات)", TaxKind.Output, false, "VAT-ZERO", 0m),
            new("VAT0-IN", "VAT zero-rated (purchases)", "ضريبة القيمة المضافة بسعر صفر (مشتريات)", TaxKind.Input, true, "VAT-INPUT-ZERO", 0m),
            new("EXEMPT-OUT", "VAT exempt (sales)", "معفى من ضريبة القيمة المضافة (مبيعات)", TaxKind.Output, false, "VAT-EXEMPT", 0m),
            new("EXEMPT-IN", "VAT exempt (purchases)", "معفى من ضريبة القيمة المضافة (مشتريات)", TaxKind.Input, false, "VAT-INPUT-EXEMPT", 0m),
            // Table tax rates differ by product schedule, so the template seeds 0 % and the company sets the real rate.
            new("TABLE-OUT", "Table tax (sales)", "ضريبة الجدول (مبيعات)", TaxKind.Output, false, "TABLE-TAX", tableRate),
            new("WHT-1", "Withholding tax 1%", "ضريبة الخصم والإضافة 1%", TaxKind.Withholding, false, "WHT", 1m),
            new("WHT-3", "Withholding tax 3%", "ضريبة الخصم والإضافة 3%", TaxKind.Withholding, false, "WHT", 3m),
        ];

        var existingCodes = await db.TaxCodes.ToDictionaryAsync(t => t.Code, cancellationToken);
        foreach (var spec in specs.Where(s => !existingCodes.ContainsKey(s.Code)))
        {
            var code = TaxCode.Create(companyId, spec.Code, spec.NameEn, spec.NameAr, spec.Kind, spec.Recoverable, spec.ReportingBox, "EG", spec.Rate, from);
            db.TaxCodes.Add(code);
            existingCodes[spec.Code] = code;
            codesCreated++;
        }

        (string Code, string NameEn, string NameAr, (string TaxCode, bool Compound)[] Components)[] groups =
        [
            ("VAT14-SALES", "Standard VAT — sales", "ضريبة قيمة مضافة قياسية — مبيعات", [("VAT14-OUT", false)]),
            ("VAT14-PURCH", "Standard VAT — purchases", "ضريبة قيمة مضافة قياسية — مشتريات", [("VAT14-IN", false)]),
            ("ZERO-SALES", "Zero-rated — sales", "سعر صفر — مبيعات", [("VAT0-OUT", false)]),
            ("ZERO-PURCH", "Zero-rated — purchases", "سعر صفر — مشتريات", [("VAT0-IN", false)]),
            ("EXEMPT-SALES", "Exempt — sales", "معفى — مبيعات", [("EXEMPT-OUT", false)]),
            ("EXEMPT-PURCH", "Exempt — purchases", "معفى — مشتريات", [("EXEMPT-IN", false)]),
            // Example of a compound group: VAT is charged on the price plus table tax.
            ("TABLE-VAT-SALES", "Table tax + VAT — sales", "ضريبة الجدول + القيمة المضافة — مبيعات", [("TABLE-OUT", false), ("VAT14-OUT", true)]),
        ];

        var existingGroups = await db.TaxGroups.ToDictionaryAsync(g => g.Code, cancellationToken);
        foreach (var g in groups.Where(g => !existingGroups.ContainsKey(g.Code)))
        {
            var group = TaxGroup.Create(companyId, g.Code, g.NameEn, g.NameAr,
                g.Components.Select((c, i) => (existingCodes[c.TaxCode].Id, (i + 1) * 10, c.Compound)));
            db.TaxGroups.Add(group);
            existingGroups[g.Code] = group;
            groupsCreated++;
        }

        (string Category, TaxDirectionKind Direction, string Group)[] mappings =
        [
            ("STANDARD", TaxDirectionKind.Sales, "VAT14-SALES"),
            ("STANDARD", TaxDirectionKind.Purchase, "VAT14-PURCH"),
            ("ZERO", TaxDirectionKind.Sales, "ZERO-SALES"),
            ("ZERO", TaxDirectionKind.Purchase, "ZERO-PURCH"),
            ("EXEMPT", TaxDirectionKind.Sales, "EXEMPT-SALES"),
            ("EXEMPT", TaxDirectionKind.Purchase, "EXEMPT-PURCH"),
        ];

        var existingMappings = (await db.TaxCategoryMappings.Select(m => new { m.CategoryCode, m.Direction }).ToListAsync(cancellationToken))
            .Select(m => (m.CategoryCode, m.Direction)).ToHashSet();
        foreach (var m in mappings.Where(m => !existingMappings.Contains((m.Category, m.Direction))))
        {
            db.TaxCategoryMappings.Add(TaxCategoryMapping.Create(companyId, m.Category, m.Direction, existingGroups[m.Group].Id));
            mappingsCreated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new TemplateResult(currencies, codesCreated, groupsCreated, mappingsCreated);
    }
}
