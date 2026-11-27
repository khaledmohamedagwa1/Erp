using System.Xml.Linq;
using Erp.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;

namespace Erp.Infrastructure.Security;

/// <summary>
/// Stores the Data Protection key ring in <c>core.DataProtectionKey</c> so every instance (and every restart) can decrypt
/// what another instance encrypted — required for MFA secrets and challenge tokens. Keys are stored as XML; encrypting
/// them at rest with a certificate is configured separately (see deploy docs) and SQL Server TDE is recommended.
/// </summary>
public sealed class SqlXmlRepository(IConnectionStringProvider connectionStrings) : IXmlRepository
{
    // The key ring is read rarely (startup, then about once a day). A non-pooled connection keeps an early failure here
    // (e.g. the database not migrated yet) from putting the shared connection pool into its blocking period.
    private string ConnectionString => new SqlConnectionStringBuilder(connectionStrings.GetConnectionString()) { Pooling = false }.ConnectionString;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Xml FROM core.DataProtectionKey ORDER BY Id";
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO core.DataProtectionKey (FriendlyName, Xml, CreatedAtUtc) VALUES (@name, @xml, SYSUTCDATETIME())";
        command.Parameters.AddWithValue("@name", friendlyName);
        command.Parameters.AddWithValue("@xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
