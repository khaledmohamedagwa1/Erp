using Erp.SharedKernel.Results;

namespace Erp.SharedKernel.Domain;

/// <summary>Thrown when an aggregate invariant would be violated. Mapped to an HTTP problem response.</summary>
public sealed class DomainException : Exception
{
    public DomainException(Error error)
        : base(error.Message)
    {
        Error = error;
    }

    public Error Error { get; }
}

public static class Guard
{
    public static string NotEmpty(string? value, string code, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException(Error.Validation(code, $"{field} is required."));
        }

        return value.Trim();
    }

    public static string MaxLength(string value, int max, string code, string field)
    {
        if (value.Length > max)
        {
            throw new DomainException(Error.Validation(code, $"{field} must be at most {max} characters."));
        }

        return value;
    }
}
