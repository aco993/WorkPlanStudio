using System.Reflection;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// "Every mutating service method asks the authorization policy first" is stated
/// in <c>ARCHITECTURE.md</c> and in <c>AGENTS.md</c>, and it used to be enforced
/// by a hand-written list of calls in <see cref="AuthorizationTests"/> — which
/// means it was enforced for the methods someone remembered.
/// <para>
/// Here it is enforced as a closed set, using the discriminator the codebase
/// already applies consistently: a method that can change something returns
/// <see cref="ApplicationResult{T}"/>, and a read does not. A mutating method
/// added tomorrow is covered the moment it is written, and the second test below
/// guards the discriminator itself — without it, a mutation that returned
/// <c>bool</c> would simply be invisible and the suite would stay green.
/// </para>
/// </summary>
public sealed class ServiceAuthorizationArchitectureTests
{
    private static readonly Type[] Services =
    [
        typeof(WorkCenterService),
        typeof(CostCenterService),
        typeof(WorkPlanService),
        typeof(ProductionOrderService),
        typeof(PlantSettingsService)
    ];

    public static TheoryData<string, string> MutatingMethods()
    {
        var data = new TheoryData<string, string>();
        foreach (var service in Services)
            foreach (var method in Mutations(service))
                data.Add(service.FullName!, method.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(MutatingMethods))]
    public async Task Every_mutating_service_method_refuses_a_guest(string serviceName, string methodName)
    {
        var service = Services.Single(s => s.FullName == serviceName);

        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase($"{service.Name}.{methodName}.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var instance = Activator.CreateInstance(service, database, new RoleGuard(WorkspaceRole.Guest))!;
        var method = service.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!;

        var task = (Task)method.Invoke(instance, Arguments(method))!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var status = (ApplicationResultStatus)result.GetType().GetProperty("Status")!.GetValue(result)!;

        Assert.Equal(ApplicationResultStatus.Forbidden, status);
    }

    [Fact]
    public void The_services_have_not_quietly_stopped_returning_application_results()
    {
        foreach (var service in Services)
            Assert.NotEmpty(Mutations(service));
    }

    private static IEnumerable<MethodInfo> Mutations(Type service) =>
        service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType.IsGenericType
                        && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                        && m.ReturnType.GetGenericArguments()[0].IsGenericType
                        && m.ReturnType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(ApplicationResult<>));

    /// <summary>
    /// Any argument will do: the guard must refuse before it looks at one. A
    /// service that reached its arguments first would fail this test by throwing,
    /// which is the right outcome.
    /// </summary>
    private static object?[] Arguments(MethodInfo method) =>
        [.. method.GetParameters().Select(p =>
            p.ParameterType == typeof(CancellationToken) ? CancellationToken.None
            : p.ParameterType == typeof(string) ? ""
            : p.ParameterType == typeof(int) ? 1
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
            : Activator.CreateInstance(p.ParameterType))];
}
