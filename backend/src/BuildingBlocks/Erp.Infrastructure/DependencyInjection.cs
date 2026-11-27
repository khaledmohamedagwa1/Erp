using Erp.Application.Abstractions;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Database;
using Erp.Infrastructure.Numbering;
using Erp.Infrastructure.Outbox;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Notifications;
using Erp.Infrastructure.Reliability;
using Erp.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;
using Erp.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddErpInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Erp")
            ?? throw new InvalidOperationException("Connection string 'Erp' is not configured. Set ConnectionStrings__Erp.");

        services.AddSingleton<IConnectionStringProvider>(new ConfigurationConnectionStringProvider(connectionString));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ErpDbSession>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ErpDbSession>());
        services.AddScoped<TenantSessionContextInterceptor>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<AuditChainVerifier>();
        services.AddScoped<ErpDbContextDependencies>();
        services.AddScoped<IDocumentNumberGenerator, DocumentNumberGenerator>();
        services.AddSingleton<Erp.Application.Reporting.IDocumentRenderer, Erp.Infrastructure.Rendering.HtmlDocumentRenderer>();
        services.AddScoped<IChangeTrackerReset>(sp => sp.GetRequiredService<ErpDbSession>());
        services.AddScoped<IInbox, SqlInbox>();
        services.AddScoped<IIdempotencyStore, SqlIdempotencyStore>();
        services.Replace(ServiceDescriptor.Singleton<ITransientFailureDetector, SqlTransientFailureDetector>());

        // Data Protection keys live in the database so all instances share them and they survive restarts.
        services.AddDataProtection().SetApplicationName("erp");
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(
            options => options.XmlRepository = new SqlXmlRepository(sp.GetRequiredService<IConnectionStringProvider>())));

        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName));
        if (configuration.GetValue<bool>($"{SmtpOptions.SectionName}:Enabled"))
        {
            services.AddSingleton<INotificationSender, SmtpNotificationSender>();
        }
        else
        {
            services.AddSingleton<INotificationSender, LoggingNotificationSender>();
        }

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName));
        services.AddSingleton<EventTypeRegistry>();
        services.AddSingleton<IOutboxProcessor, OutboxProcessor>();
        services.AddSingleton<ErpDatabaseMigrator>();

        services.AddErpDbContext<PlatformDbContext>(PlatformDbContext.SchemaName, order: 0, hasOutbox: false);
        return services;
    }

    /// <summary>
    /// Registers a module DbContext on the shared scoped connection, with its own migrations history table in its schema.
    /// </summary>
    public static IServiceCollection AddErpDbContext<TContext>(this IServiceCollection services, string schema, int order, bool hasOutbox = true)
        where TContext : ErpDbContext
    {
        services.AddDbContext<TContext>((sp, options) =>
        {
            var session = sp.GetRequiredService<ErpDbSession>();
            options.UseSqlServer(session.Connection, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", schema));
            options.AddInterceptors(sp.GetRequiredService<TenantSessionContextInterceptor>());
        });
        services.AddSingleton(new ModuleDbContextRegistration(typeof(TContext), schema, order));
        if (hasOutbox)
        {
            services.AddSingleton(new OutboxSource(schema));
        }

        return services;
    }
}
