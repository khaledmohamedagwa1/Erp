using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Messaging;
using Erp.SharedKernel.Results;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Erp.UnitTests;

public class DispatcherTests
{
    public sealed record Echo(string Text) : ICommand<string>;

    public sealed class EchoHandler : ICommandHandler<Echo, string>
    {
        public Task<Result<string>> Handle(Echo request, CancellationToken cancellationToken) => Task.FromResult<Result<string>>(request.Text);
    }

    public sealed class EchoValidator : AbstractValidator<Echo>
    {
        public EchoValidator() => RuleFor(x => x.Text).NotEmpty();
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public List<string> Calls { get; } = [];

        public bool HasActiveTransaction { get; private set; }

        public Task BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("begin");
            HasActiveTransaction = true;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("commit");
            HasActiveTransaction = false;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("rollback");
            HasActiveTransaction = false;
            return Task.CompletedTask;
        }
    }

    private static (IDispatcher Dispatcher, FakeUnitOfWork UnitOfWork) Build()
    {
        var services = new ServiceCollection().AddLogging();
        var uow = new FakeUnitOfWork();
        services.AddSingleton<IUnitOfWork>(uow);
        services.AddErpApplication();
        services.AddHandlersFromAssembly(typeof(DispatcherTests).Assembly);
        return (services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IDispatcher>(), uow);
    }

    [Fact]
    public async Task Commands_run_in_a_committed_transaction()
    {
        var (dispatcher, uow) = Build();
        (await dispatcher.Send(new Echo("hi"), TestContext.Current.CancellationToken)).Value.ShouldBe("hi");
        uow.Calls.ShouldBe(["begin", "commit"]);
    }

    [Fact]
    public async Task Invalid_requests_fail_before_any_transaction_with_field_details()
    {
        var (dispatcher, uow) = Build();
        var result = await dispatcher.Send(new Echo(""), TestContext.Current.CancellationToken);
        result.Error.Code.ShouldBe("validation-failed");
        result.Error.Details!.ShouldContainKey("text");
        uow.Calls.ShouldBeEmpty();
    }
}
