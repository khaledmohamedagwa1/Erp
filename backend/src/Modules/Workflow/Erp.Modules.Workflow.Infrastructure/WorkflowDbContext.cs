using Erp.Infrastructure.Persistence;
using Erp.Modules.Workflow.Application;
using Erp.Modules.Workflow.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;

namespace Erp.Modules.Workflow.Infrastructure;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ErpDbContextDependencies? dependencies = null)
    : ErpDbContext(options, dependencies), IWorkflowDbContext
{
    public const string SchemaName = "workflow";

    public override string Schema => SchemaName;

    public DbSet<ApprovalDefinition> Definitions => Set<ApprovalDefinition>();

    public DbSet<ApprovalInstance> Instances => Set<ApprovalInstance>();

    public DbSet<ApprovalDelegation> Delegations => Set<ApprovalDelegation>();

    EntityEntry IWorkflowDbContext.Entry(object entity) => Entry(entity);

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Conditions, steps, actions and assigned approvers belong to their aggregate and are never queried alone:
        // JSON columns keep them under the row's tenant security, audit and rowversion.
        modelBuilder.Entity<ApprovalDefinition>(b =>
        {
            b.ToTable("ApprovalDefinition");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DocumentType).HasMaxLength(100).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(TenantIdProperty, nameof(ApprovalDefinition.CompanyId), nameof(ApprovalDefinition.DocumentType), nameof(ApprovalDefinition.Priority));
            b.OwnsMany(x => x.Conditions, c =>
            {
                c.ToJson("Conditions");
                c.Property(x => x.Operator).HasConversion<string>();
            });
            b.OwnsMany(x => x.Steps, s =>
            {
                s.ToJson("Steps");
                s.Property(x => x.ApproverKind).HasConversion<string>();
                s.Property(x => x.EscalateToKind).HasConversion<string>();
            });
            b.Navigation(x => x.Conditions).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_conditions");
            b.Navigation(x => x.Steps).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_steps");
        });

        modelBuilder.Entity<ApprovalInstance>(b =>
        {
            b.ToTable("ApprovalInstance");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DocumentType).HasMaxLength(100).IsRequired();
            b.Property(x => x.DocumentNumber).HasMaxLength(50).IsRequired();
            b.Property(x => x.DefinitionName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Amount).HasPrecision(19, 4);
            b.Property(x => x.AmountBase).HasPrecision(19, 4);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.FactsJson).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.StepStartedAtUtc).HasPrecision(3);
            b.Property(x => x.SubmittedAtUtc).HasPrecision(3);
            b.Property(x => x.CompletedAtUtc).HasPrecision(3);
            b.Ignore(x => x.Current);
            b.HasIndex(TenantIdProperty, nameof(ApprovalInstance.DocumentType), nameof(ApprovalInstance.DocumentId));
            b.HasIndex(TenantIdProperty, nameof(ApprovalInstance.CompanyId), nameof(ApprovalInstance.Status));

            // At most one pending approval per document.
            b.HasIndex(TenantIdProperty, nameof(ApprovalInstance.DocumentType), nameof(ApprovalInstance.DocumentId))
                .IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("UX_ApprovalInstance_PendingDocument");
            b.OwnsMany(x => x.Steps, s =>
            {
                s.ToJson("Steps");
                s.Property(x => x.ApproverKind).HasConversion<string>();
                s.Property(x => x.EscalateToKind).HasConversion<string>();
            });
            b.OwnsMany(x => x.AssignedApprovers, a =>
            {
                a.ToJson("AssignedApprovers");
                a.Property(x => x.Kind).HasConversion<string>();
                a.Property(x => x.Source).HasConversion<string>();
            });
            b.OwnsMany(x => x.Actions, a =>
            {
                a.ToJson("Actions");
                a.Property(x => x.Kind).HasConversion<string>();
            });
            b.Navigation(x => x.Steps).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_steps");
            b.Navigation(x => x.AssignedApprovers).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_assigned");
            b.Navigation(x => x.Actions).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_actions");
        });

        modelBuilder.Entity<ApprovalDelegation>(b =>
        {
            b.ToTable("ApprovalDelegation");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DocumentType).HasMaxLength(100);
            b.HasIndex(TenantIdProperty, nameof(ApprovalDelegation.CompanyId), nameof(ApprovalDelegation.ToUserId));
        });
    }
}

public sealed class WorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseSqlServer("Server=localhost;Database=ErpDesignTime;Integrated Security=false", sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", WorkflowDbContext.SchemaName))
        .Options);
}
