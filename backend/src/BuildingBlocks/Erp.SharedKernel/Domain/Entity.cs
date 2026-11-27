namespace Erp.SharedKernel.Domain;

/// <summary>Base type for all entities. Identifiers are generated in the application (see <see cref="SequentialGuid"/>).</summary>
public abstract class Entity
{
    protected Entity()
    {
    }

    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; protected set; }
}

/// <summary>
/// Consistency boundary. Concurrency (rowversion), audit stamps and tenant ownership are applied by
/// infrastructure as shadow properties so the domain model stays persistence-agnostic.
/// </summary>
public abstract class AggregateRoot : Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot()
    {
    }

    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Something that happened in the domain. Persisted to the module outbox in the same transaction.</summary>
public interface IDomainEvent
{
}
