using System.Text.RegularExpressions;
using Erp.Application.Messaging;
using Erp.Modules.Catalog.Contracts;
using Erp.Modules.Catalog.Domain;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Catalog.Application;

/// <summary>Creates the missing variants of a product from attribute value lists (e.g. Size: S, M, L × Colour: Red, Blue).</summary>
public sealed record GenerateProductVariantsCommand(Guid ProductId, IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes) : ICommand<IReadOnlyList<VariantInfo>>;

public sealed class GenerateProductVariantsValidator : AbstractValidator<GenerateProductVariantsCommand>
{
    public GenerateProductVariantsValidator()
    {
        RuleFor(x => x.Attributes).NotEmpty().WithMessage("Give at least one attribute with values.");
        RuleFor(x => x.Attributes).Must(a => a.All(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value.Count > 0 && kv.Value.All(v => !string.IsNullOrWhiteSpace(v))))
            .WithMessage("Every attribute needs a name and at least one non-empty value.");
        RuleFor(x => x.Attributes).Must(a => a.Values.Aggregate(1L, (n, v) => n * Math.Max(1, v.Count)) <= 500)
            .WithMessage("At most 500 combinations can be generated at once.");
    }
}

public sealed partial class GenerateProductVariantsHandler(ICatalogDbContext db) : ICommandHandler<GenerateProductVariantsCommand, IReadOnlyList<VariantInfo>>
{
    public async Task<Result<IReadOnlyList<VariantInfo>>> Handle(GenerateProductVariantsCommand command, CancellationToken cancellationToken)
    {
        var product = await CatalogChecks.LoadProductAsync(db, command.ProductId, cancellationToken);
        if (product is null)
        {
            return CatalogAppErrors.ProductNotFound;
        }

        var existing = product.Variants.Select(v => Key(v.AttributeValues())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var combination in Combinations(command.Attributes))
        {
            if (!existing.Add(Key(combination)))
            {
                continue;
            }

            var sku = $"{product.Sku}-{string.Join('-', combination.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => SkuPart(kv.Value)))}";
            if (sku.Length > 40)
            {
                return Error.Validation("catalog.variant-sku-too-long", $"The generated SKU {sku} is longer than 40 characters; use shorter attribute values.");
            }

            if (await CatalogChecks.SkuTakenAsync(db, sku.ToUpperInvariant(), cancellationToken))
            {
                return CatalogErrors.SkuTaken;
            }

            product.AddVariant(sku, combination);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(CatalogVariantService.ToInfo(product));
    }

    private static string Key(IReadOnlyDictionary<string, string> attributes) =>
        string.Join('|', attributes.OrderBy(kv => kv.Key.Trim(), StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key.Trim()}={kv.Value.Trim()}"));

    private static string SkuPart(string value) => NonSkuCharacters().Replace(value.Trim().ToUpperInvariant(), string.Empty);

    private static IEnumerable<IReadOnlyDictionary<string, string>> Combinations(IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
    {
        IEnumerable<Dictionary<string, string>> result = [new Dictionary<string, string>(StringComparer.Ordinal)];
        foreach (var (name, values) in attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var key = name.Trim();
            result = result.SelectMany(partial => values.Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(v => new Dictionary<string, string>(partial, StringComparer.Ordinal) { [key] = v })).ToList();
        }

        return result;
    }

    [GeneratedRegex("[^A-Z0-9]")]
    private static partial Regex NonSkuCharacters();
}

/// <summary>Contract implementation of <see cref="ICatalogVariantService"/>.</summary>
public sealed class CatalogVariantService(ICatalogDbContext db, IDispatcher dispatcher) : ICatalogVariantService
{
    public async Task<IReadOnlyList<VariantInfo>> GetVariantsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
    {
        if (productIds.Count == 0)
        {
            return [];
        }

        var products = await db.Products.AsNoTracking().Include(p => p.Variants).Where(p => productIds.Contains(p.Id)).ToListAsync(cancellationToken);
        return products.SelectMany(ToInfo).ToList();
    }

    public Task<Result<IReadOnlyList<VariantInfo>>> GenerateVariantsAsync(Guid productId, IReadOnlyDictionary<string, IReadOnlyList<string>> attributeValues,
        CancellationToken cancellationToken = default) =>
        dispatcher.Send(new GenerateProductVariantsCommand(productId, attributeValues), cancellationToken);

    internal static IReadOnlyList<VariantInfo> ToInfo(Product product) =>
        product.Variants.OrderBy(v => v.Sku, StringComparer.Ordinal).Select(v => new VariantInfo(v.Id, product.Id, v.Sku, v.AttributeValues())).ToList();
}
