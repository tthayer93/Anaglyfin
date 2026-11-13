using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Anaglyfin.RuntimeStatus;
using Anaglyfin.Tests.Stubs;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Anaglyfin.Tests.RuntimeStatus;

/// <summary>
/// Proves the runtime-status endpoint is the surface it has to be: discovered as a plugin
/// controller, routed at <c>/Anaglyfin/RuntimeStatus</c>, and gated on the server's admin policy —
/// not on merely being signed in. The authorization claim is read off the attributes the server
/// itself acts on, so a change that quietly downgrades <c>RequiresElevation</c> fails here.
/// </summary>
public sealed class RuntimeStatusControllerTests
{
    private static readonly Type ControllerType = typeof(RuntimeStatusController);

    [Fact]
    public void ItIsADiscoveredPluginControllerAndNotARegisteredService()
    {
        Assert.True(ControllerType.IsPublic);
        Assert.False(ControllerType.IsAbstract);
        Assert.True(typeof(ControllerBase).IsAssignableFrom(ControllerType));

        // Like the media source provider, the controller is discovered as an application part and is
        // never registered into the container; only the service it is handed is.
        var services = new FakeServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == ControllerType);
        Assert.DoesNotContain(
            services,
            descriptor => typeof(ControllerBase).IsAssignableFrom(descriptor.ServiceType));
    }

    [Fact]
    public void TheWholeControllerIsGatedOnTheServerAdminPolicy()
    {
        var authorize = Assert.Single(ControllerType.GetCustomAttributes<AuthorizeAttribute>());

        // The server's approved admin mechanism: the RequiresElevation policy, which resolves the
        // Administrator role claim. It is a policy, not a bare role string or a scheme override.
        Assert.Equal(Policies.RequiresElevation, authorize.Policy);
        Assert.Null(authorize.Roles);
        Assert.Null(authorize.AuthenticationSchemes);

        // Nothing lets a non-administrator, or an anonymous caller, through.
        Assert.Empty(ControllerType.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.DoesNotContain(
            ControllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttributes<AllowAnonymousAttribute>().Any());
    }

    [Fact]
    public void TheActionIsRoutedAtAnaglyfinRuntimeStatus()
    {
        var route = Assert.Single(ControllerType.GetCustomAttributes<RouteAttribute>());
        Assert.Equal("Anaglyfin", route.Template);

        var action = GetAction();

        // Exactly one verb, and it is a GET: a read-only surface exposes no write.
        var verb = Assert.Single(action.GetCustomAttributes<HttpMethodAttribute>());
        var httpGet = Assert.IsType<HttpGetAttribute>(verb);
        Assert.Equal("RuntimeStatus", httpGet.Template);

        Assert.Equal(
            "/Anaglyfin/RuntimeStatus",
            "/" + string.Join(
                '/',
                new[] { route.Template ?? string.Empty, httpGet.Template ?? string.Empty }
                    .Select(part => part.Trim('/'))));
    }

    [Fact]
    public void TheActionAcceptsAnOptionalRefreshQuery()
    {
        var action = GetAction();

        var refresh = Assert.Single(
            action.GetParameters(),
            parameter => parameter.Name == "refresh");

        Assert.Equal(typeof(bool?), refresh.ParameterType);
        Assert.True(refresh.HasDefaultValue);
        Assert.Null(refresh.DefaultValue);
        Assert.Single(refresh.GetCustomAttributes<FromQueryAttribute>());
    }

    [Fact]
    public void ItIsAnApiController()
    {
        Assert.NotNull(ControllerType.GetCustomAttribute<ApiControllerAttribute>());
    }

    [Fact]
    public async Task TheActionReturnsTheServiceSnapshotWithStatusOk()
    {
        var response = new RuntimeStatusResponse(
            RuntimeStatusLevel.Ok,
            "1.2.3",
            DateTimeOffset.UnixEpoch,
            Array.Empty<RuntimeBinaryStatus>(),
            Array.Empty<RuntimeEnvironmentStatus>());
        var service = new FakeRuntimeStatusService(response);

        var controller = new RuntimeStatusController(service);
        var result = await controller.GetRuntimeStatus(null, CancellationToken.None);

        // The action answers with the service's snapshot itself. Ok carries it either as the model
        // value or as an OkObjectResult's value, depending on how the result was constructed; the
        // snapshot has to be reachable either way, and exactly the one the service handed back.
        var payload = result.Value ?? Assert.IsType<OkObjectResult>(result.Result).Value;
        Assert.Same(response, payload);
        Assert.Equal(1, service.GetCallCount);
        Assert.Equal(0, service.RefreshCallCount);
    }

    [Fact]
    public async Task TheActionRefreshesWhenRefreshIsTrue()
    {
        var response = new RuntimeStatusResponse(
            RuntimeStatusLevel.Ok,
            "1.2.3",
            DateTimeOffset.UnixEpoch,
            Array.Empty<RuntimeBinaryStatus>(),
            Array.Empty<RuntimeEnvironmentStatus>());
        var service = new FakeRuntimeStatusService(response);

        var controller = new RuntimeStatusController(service);
        var result = await controller.GetRuntimeStatus(true, CancellationToken.None);

        var payload = result.Value ?? Assert.IsType<OkObjectResult>(result.Result).Value;
        Assert.Same(response, payload);
        Assert.Equal(0, service.GetCallCount);
        Assert.Equal(1, service.RefreshCallCount);
    }

    [Fact]
    public async Task TheActionDoesNotRefreshWhenRefreshIsFalse()
    {
        var response = new RuntimeStatusResponse(
            RuntimeStatusLevel.Ok,
            "1.2.3",
            DateTimeOffset.UnixEpoch,
            Array.Empty<RuntimeBinaryStatus>(),
            Array.Empty<RuntimeEnvironmentStatus>());
        var service = new FakeRuntimeStatusService(response);

        var controller = new RuntimeStatusController(service);
        var result = await controller.GetRuntimeStatus(false, CancellationToken.None);

        var payload = result.Value ?? Assert.IsType<OkObjectResult>(result.Result).Value;
        Assert.Same(response, payload);
        Assert.Equal(1, service.GetCallCount);
        Assert.Equal(0, service.RefreshCallCount);
    }

    [Fact]
    public void TheActionRequiresTheService()
    {
        Assert.Throws<ArgumentNullException>(() => new RuntimeStatusController(null!));
    }

    private static MethodInfo GetAction()
        => typeof(RuntimeStatusController).GetMethod("GetRuntimeStatus")
            ?? throw new InvalidOperationException("The controller exposes no GetRuntimeStatus action.");

    private sealed class FakeRuntimeStatusService : IRuntimeStatusService
    {
        private readonly RuntimeStatusResponse _response;

        public FakeRuntimeStatusService(RuntimeStatusResponse response)
            => _response = response;

        public int GetCallCount { get; private set; }

        public int RefreshCallCount { get; private set; }

        public Task<RuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
        {
            GetCallCount++;
            return Task.FromResult(_response);
        }

        public Task<RuntimeStatusResponse> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCallCount++;
            return Task.FromResult(_response);
        }
    }
}
