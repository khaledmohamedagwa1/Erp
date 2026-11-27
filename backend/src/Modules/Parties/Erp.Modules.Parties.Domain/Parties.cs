using System.Text.RegularExpressions;
using Erp.SharedKernel.Domain;
using Erp.SharedKernel.Results;

namespace Erp.Modules.Parties.Domain;

public static partial class PartyRules
{
    public const string DefaultPostingGroup = "TRADE";

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,29}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public static string Code(string? value, string field)
    {
        var code = Guard.NotEmpty(value, "parties.code-required", field).ToUpperInvariant();
        if (!CodePattern().IsMatch(code))
        {
            throw new DomainException(Error.Validation("parties.code-invalid", $"{field} must be 2–30 characters: letters, digits, '-' or '_'."));
        }

        return code;
    }

    public static string Name(string? value, string field) =>
        Guard.MaxLength(Guard.NotEmpty(value, "parties.name-required", field), 200, "parties.name-too-long", field);

    public static string Currency(string? value)
    {
        var currency = Guard.NotEmpty(value, "parties.currency-required", "Currency").ToUpperInvariant();
        if (!CurrencyPattern().IsMatch(currency))
        {
            throw new DomainException(Error.Validation("parties.currency-invalid", "Currency must be a 3-letter ISO 4217 code."));
        }

        return currency;
    }

    public static string PostingGroup(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultPostingGroup : Code(value, "Posting group");

    public static string? Optional(string? value, int max, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : Guard.MaxLength(value.Trim(), max, "parties.value-too-long", field);
}

// ---------------- Payment terms ----------------

public enum DueDateBasis
{
    /// <summary>Days are counted from the invoice date.</summary>
    InvoiceDate = 1,

    /// <summary>Days are counted from the last day of the invoice month.</summary>
    EndOfMonth = 2,
}

/// <summary>One slice of a payment term: a percentage of the amount due a number of days after a base date.</summary>
public sealed class PaymentInstallment
{
    private PaymentInstallment()
    {
    }

    public PaymentInstallment(decimal percent, int days, DueDateBasis basis)
    {
        if (percent <= 0 || percent > 100)
        {
            throw new DomainException(Error.Validation("parties.installment-percent-invalid", "Each installment percent must be greater than 0 and at most 100."));
        }

        if (days is < 0 or > 3650)
        {
            throw new DomainException(Error.Validation("parties.installment-days-invalid", "Installment days must be between 0 and 3650."));
        }

        Percent = percent;
        Days = days;
        Basis = basis;
    }

    public decimal Percent { get; private set; }

    public int Days { get; private set; }

    public DueDateBasis Basis { get; private set; }

    public DateOnly DueDate(DateOnly invoiceDate)
    {
        var baseDate = Basis == DueDateBasis.EndOfMonth
            ? new DateOnly(invoiceDate.Year, invoiceDate.Month, DateTime.DaysInMonth(invoiceDate.Year, invoiceDate.Month))
            : invoiceDate;
        return baseDate.AddDays(Days);
    }
}

[Audited]
public sealed class PaymentTerms : AggregateRoot, ICompanyScoped
{
    private readonly List<PaymentInstallment> _installments = [];

    private PaymentTerms()
    {
    }

    private PaymentTerms(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public IReadOnlyCollection<PaymentInstallment> Installments => _installments.AsReadOnly();

    public static PaymentTerms Create(Guid companyId, string code, string nameEn, string nameAr, IEnumerable<PaymentInstallment> installments)
    {
        var terms = new PaymentTerms(SequentialGuid.New(), companyId, PartyRules.Code(code, "Payment terms code"));
        terms.Update(nameEn, nameAr, installments);
        return terms;
    }

    public void Update(string nameEn, string nameAr, IEnumerable<PaymentInstallment> installments)
    {
        var list = installments.ToList();
        if (list.Count == 0)
        {
            throw new DomainException(Error.Validation("parties.installments-required", "Payment terms need at least one installment."));
        }

        if (list.Sum(i => i.Percent) != 100m)
        {
            throw new DomainException(Error.Validation("parties.installments-not-100", "Installment percentages must add up to exactly 100."));
        }

        NameEn = PartyRules.Name(nameEn, "English name");
        NameAr = PartyRules.Name(nameAr, "Arabic name");
        _installments.Clear();
        _installments.AddRange(list);
    }

    public void Deactivate() => IsActive = false;

    /// <summary>
    /// Due schedule for an amount. Installments are rounded half away from zero to <paramref name="decimals"/>;
    /// the last installment takes the remainder so the schedule always sums exactly to <paramref name="amount"/>.
    /// Installments falling on the same date are merged.
    /// </summary>
    public IReadOnlyList<(DateOnly DueDate, decimal Amount)> Schedule(DateOnly invoiceDate, decimal amount, int decimals = 2) =>
        BuildSchedule(_installments, invoiceDate, amount, decimals);

    public static IReadOnlyList<(DateOnly DueDate, decimal Amount)> BuildSchedule(
        IReadOnlyList<PaymentInstallment> installments, DateOnly invoiceDate, decimal amount, int decimals)
    {
        if (installments.Count == 0)
        {
            return [(invoiceDate, amount)];
        }

        var result = new List<(DateOnly DueDate, decimal Amount)>();
        var allocated = 0m;
        for (var i = 0; i < installments.Count; i++)
        {
            var installment = installments[i];
            var part = i == installments.Count - 1
                ? amount - allocated
                : Math.Round(amount * installment.Percent / 100m, decimals, MidpointRounding.AwayFromZero);
            allocated += part;
            result.Add((installment.DueDate(invoiceDate), part));
        }

        return result
            .GroupBy(r => r.DueDate)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.Sum(x => x.Amount)))
            .ToList();
    }
}

// ---------------- Contacts and addresses ----------------

public enum AddressType
{
    Billing = 1,
    Shipping = 2,
}

[Audited]
public sealed class PartyContact
{
    private PartyContact()
    {
    }

    public PartyContact(string name, string? phone, string? email, string? role)
    {
        Uid = SequentialGuid.New();
        Name = PartyRules.Name(name, "Contact name");
        Phone = PartyRules.Optional(phone, 50, "Phone");
        Email = PartyRules.Optional(email, 256, "Email");
        Role = PartyRules.Optional(role, 100, "Role");
    }

    /// <summary>Stable identifier of the contact inside its party (used by the API to remove it).</summary>
    public Guid Uid { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Role { get; private set; }
}

[Audited]
public sealed class PartyAddress
{
    private PartyAddress()
    {
    }

    public PartyAddress(AddressType type, string line1, string? line2, string city, string? region, string? postalCode, string country)
    {
        Uid = SequentialGuid.New();
        Type = type;
        Line1 = Guard.MaxLength(Guard.NotEmpty(line1, "parties.address-required", "Address line 1"), 200, "parties.value-too-long", "Address line 1");
        Line2 = PartyRules.Optional(line2, 200, "Address line 2");
        City = Guard.MaxLength(Guard.NotEmpty(city, "parties.city-required", "City"), 100, "parties.value-too-long", "City");
        Region = PartyRules.Optional(region, 100, "Region");
        PostalCode = PartyRules.Optional(postalCode, 20, "Postal code");
        var countryCode = Guard.NotEmpty(country, "parties.country-required", "Country").ToUpperInvariant();
        if (countryCode.Length != 2 || !countryCode.All(char.IsAsciiLetterUpper))
        {
            throw new DomainException(Error.Validation("parties.country-invalid", "Country must be a 2-letter ISO 3166 code."));
        }

        Country = countryCode;
    }

    public Guid Uid { get; private set; }

    public AddressType Type { get; private set; }

    public string Line1 { get; private set; } = string.Empty;

    public string? Line2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    public string? Region { get; private set; }

    public string? PostalCode { get; private set; }

    public string Country { get; private set; } = string.Empty;

    public bool IsDefault { get; internal set; }
}

/// <summary>Shared behaviour of customers and suppliers: identity, contacts, addresses, activation.</summary>
public abstract class Party : AggregateRoot, ICompanyScoped
{
    private readonly List<PartyContact> _contacts = [];
    private readonly List<PartyAddress> _addresses = [];

    protected Party()
    {
    }

    protected Party(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
        IsActive = true;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public string? TaxRegistrationNo { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public Guid? PaymentTermsId { get; private set; }

    public string PostingGroup { get; private set; } = PartyRules.DefaultPostingGroup;

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<PartyContact> Contacts => _contacts.AsReadOnly();

    public IReadOnlyCollection<PartyAddress> Addresses => _addresses.AsReadOnly();

    protected void SetCommon(string nameEn, string nameAr, string? taxRegistrationNo, string currency, Guid? paymentTermsId, string? postingGroup)
    {
        NameEn = PartyRules.Name(nameEn, "English name");
        NameAr = PartyRules.Name(nameAr, "Arabic name");
        TaxRegistrationNo = PartyRules.Optional(taxRegistrationNo, 50, "Tax registration number");
        Currency = PartyRules.Currency(currency);
        PaymentTermsId = paymentTermsId;
        PostingGroup = PartyRules.PostingGroup(postingGroup);
    }

    public PartyContact AddContact(string name, string? phone, string? email, string? role)
    {
        if (_contacts.Count >= 50)
        {
            throw new DomainException(Error.Validation("parties.too-many-contacts", "A party can have at most 50 contacts."));
        }

        var contact = new PartyContact(name, phone, email, role);
        _contacts.Add(contact);
        return contact;
    }

    public bool RemoveContact(Guid uid) => _contacts.RemoveAll(c => c.Uid == uid) > 0;

    /// <summary>Adds an address. The first address of a type becomes its default; a new default replaces the old one.</summary>
    public PartyAddress AddAddress(PartyAddress address, bool makeDefault)
    {
        if (_addresses.Count >= 50)
        {
            throw new DomainException(Error.Validation("parties.too-many-addresses", "A party can have at most 50 addresses."));
        }

        var sameType = _addresses.Where(a => a.Type == address.Type).ToList();
        if (makeDefault || sameType.Count == 0)
        {
            sameType.ForEach(a => a.IsDefault = false);
            address.IsDefault = true;
        }

        _addresses.Add(address);
        return address;
    }

    /// <summary>Removes an address; if it was the default of its type, the oldest remaining address of that type becomes default.</summary>
    public bool RemoveAddress(Guid uid)
    {
        var address = _addresses.FirstOrDefault(a => a.Uid == uid);
        if (address is null)
        {
            return false;
        }

        _addresses.Remove(address);
        if (address.IsDefault && _addresses.FirstOrDefault(a => a.Type == address.Type) is { } next)
        {
            next.IsDefault = true;
        }

        return true;
    }

    public PartyAddress? DefaultAddress(AddressType type) => _addresses.FirstOrDefault(a => a.Type == type && a.IsDefault);

    public virtual void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

// ---------------- Customers ----------------

public enum CustomerKind
{
    Individual = 1,
    Business = 2,
}

public enum CreditAction
{
    Warn = 1,
    RequireApproval = 2,
    Block = 3,
}

[Audited]
public sealed class CustomerGroup : AggregateRoot, ICompanyScoped
{
    private CustomerGroup()
    {
    }

    private CustomerGroup(Guid id, Guid companyId, string code)
        : base(id)
    {
        CompanyId = companyId;
        Code = code;
    }

    public Guid CompanyId { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string NameAr { get; private set; } = string.Empty;

    public Guid? DefaultPriceListId { get; private set; }

    public Guid? DefaultPaymentTermsId { get; private set; }

    public string PostingGroup { get; private set; } = PartyRules.DefaultPostingGroup;

    public static CustomerGroup Create(Guid companyId, string code, string nameEn, string nameAr, Guid? defaultPriceListId, Guid? defaultPaymentTermsId, string? postingGroup)
    {
        var group = new CustomerGroup(SequentialGuid.New(), companyId, PartyRules.Code(code, "Customer group code"));
        group.Update(nameEn, nameAr, defaultPriceListId, defaultPaymentTermsId, postingGroup);
        return group;
    }

    public void Update(string nameEn, string nameAr, Guid? defaultPriceListId, Guid? defaultPaymentTermsId, string? postingGroup)
    {
        NameEn = PartyRules.Name(nameEn, "English name");
        NameAr = PartyRules.Name(nameAr, "Arabic name");
        DefaultPriceListId = defaultPriceListId;
        DefaultPaymentTermsId = defaultPaymentTermsId;
        PostingGroup = PartyRules.PostingGroup(postingGroup);
    }
}

[Audited]
public sealed class Customer : Party
{
    public const string WalkInCode = "WALK-IN";

    private Customer()
    {
    }

    private Customer(Guid id, Guid companyId, string code, CustomerKind kind)
        : base(id, companyId, code)
    {
        Kind = kind;
        CreditAction = CreditAction.RequireApproval;
    }

    public CustomerKind Kind { get; private set; }

    public Guid? CustomerGroupId { get; private set; }

    /// <summary>Catalog price list (no database FK: different module).</summary>
    public Guid? PriceListId { get; private set; }

    /// <summary>Zero means no credit is extended (cash only) when credit limits are enabled.</summary>
    public decimal CreditLimit { get; private set; }

    public CreditAction CreditAction { get; private set; }

    public bool OnHold { get; private set; }

    /// <summary>System customer for anonymous cash/POS sales. Exactly one per company; cannot be deactivated.</summary>
    public bool IsWalkIn { get; private set; }

    /// <summary>Default salesperson (an employee, People module) for new sales documents of this customer.</summary>
    public Guid? SalespersonId { get; private set; }

    /// <summary>Assigns the default salesperson (validated by the application through the People contract).</summary>
    public void AssignSalesperson(Guid? salespersonId) => SalespersonId = salespersonId;

    public static Customer Create(
        Guid companyId, string code, CustomerKind kind, string nameEn, string nameAr, string? taxRegistrationNo, string currency,
        Guid? customerGroupId, Guid? priceListId, Guid? paymentTermsId, string? postingGroup)
    {
        var customer = new Customer(SequentialGuid.New(), companyId, PartyRules.Code(code, "Customer code"), kind);
        customer.Update(kind, nameEn, nameAr, taxRegistrationNo, currency, customerGroupId, priceListId, paymentTermsId, postingGroup);
        customer.Raise(new CustomerCreated(customer.Id, customer.Code));
        return customer;
    }

    public static Customer CreateWalkIn(Guid companyId, string currency)
    {
        var customer = new Customer(SequentialGuid.New(), companyId, WalkInCode, CustomerKind.Individual) { IsWalkIn = true };
        customer.SetCommon("Walk-in customer", "عميل نقدي", null, currency, null, null);
        customer.CreditAction = CreditAction.Block;
        return customer;
    }

    public void Update(
        CustomerKind kind, string nameEn, string nameAr, string? taxRegistrationNo, string currency,
        Guid? customerGroupId, Guid? priceListId, Guid? paymentTermsId, string? postingGroup)
    {
        if (IsWalkIn)
        {
            throw new DomainException(Error.Conflict("parties.walk-in-locked", "The walk-in customer is maintained by the system."));
        }

        Kind = kind;
        CustomerGroupId = customerGroupId;
        PriceListId = priceListId;
        SetCommon(nameEn, nameAr, taxRegistrationNo, currency, paymentTermsId, postingGroup);
    }

    public void SetCreditProfile(decimal creditLimit, CreditAction action, bool onHold)
    {
        if (IsWalkIn)
        {
            throw new DomainException(Error.Conflict("parties.walk-in-locked", "The walk-in customer cannot receive credit."));
        }

        if (creditLimit < 0)
        {
            throw new DomainException(Error.Validation("parties.credit-limit-negative", "The credit limit cannot be negative."));
        }

        if (decimal.Round(creditLimit, 4) != creditLimit)
        {
            throw new DomainException(Error.Validation("parties.credit-limit-precision", "The credit limit can have at most 4 decimals."));
        }

        var changed = creditLimit != CreditLimit || action != CreditAction || onHold != OnHold;
        CreditLimit = creditLimit;
        CreditAction = action;
        OnHold = onHold;
        if (changed)
        {
            Raise(new CustomerCreditProfileChanged(Id, creditLimit, action.ToString(), onHold));
        }
    }

    /// <summary>
    /// Evaluates a new exposure against the profile. Returns <c>null</c> when allowed, otherwise the action to take.
    /// A customer on hold is always blocked.
    /// </summary>
    public CreditAction? EvaluateExposure(decimal exposureAfterDocument)
    {
        if (OnHold)
        {
            return CreditAction.Block;
        }

        return exposureAfterDocument > CreditLimit ? CreditAction : null;
    }

    public override void Deactivate()
    {
        if (IsWalkIn)
        {
            throw new DomainException(Error.Conflict("parties.walk-in-locked", "The walk-in customer cannot be deactivated."));
        }

        base.Deactivate();
    }
}

// ---------------- Suppliers ----------------

[Audited]
public sealed class Supplier : Party
{
    private Supplier()
    {
    }

    private Supplier(Guid id, Guid companyId, string code)
        : base(id, companyId, code)
    {
    }

    public static Supplier Create(Guid companyId, string code, string nameEn, string nameAr, string? taxRegistrationNo, string currency, Guid? paymentTermsId, string? postingGroup)
    {
        var supplier = new Supplier(SequentialGuid.New(), companyId, PartyRules.Code(code, "Supplier code"));
        supplier.Update(nameEn, nameAr, taxRegistrationNo, currency, paymentTermsId, postingGroup);
        supplier.Raise(new SupplierCreated(supplier.Id, supplier.Code));
        return supplier;
    }

    public void Update(string nameEn, string nameAr, string? taxRegistrationNo, string currency, Guid? paymentTermsId, string? postingGroup) =>
        SetCommon(nameEn, nameAr, taxRegistrationNo, currency, paymentTermsId, postingGroup);
}

public sealed record CustomerCreated(Guid CustomerId, string Code) : IDomainEvent;

public sealed record CustomerCreditProfileChanged(Guid CustomerId, decimal CreditLimit, string CreditAction, bool OnHold) : IDomainEvent;

public sealed record SupplierCreated(Guid SupplierId, string Code) : IDomainEvent;
