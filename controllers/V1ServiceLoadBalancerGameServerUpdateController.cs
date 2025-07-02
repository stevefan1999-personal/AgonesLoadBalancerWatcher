using System.Collections.Immutable;
using System.Reactive.Disposables;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.LabelSelectors;
using Microsoft.Extensions.Logging;

namespace AgonesLoadBalancerWatcher;

[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
public class V1ServiceLoadBalancerGameServerUpdateController(
    ILogger<V1ServiceLoadBalancerGameServerUpdateController> _logger,
    IKubernetesClient _client,
    EntityRequeue<V1Service> _requeue
) : IEntityController<V1Service>
{
    public class LabelSelector : IEntityLabelSelector<V1Service>
    {
        public ValueTask<string?> GetLabelSelectorAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(
                (
                    new KubeOps.KubernetesClient.LabelSelectors.LabelSelector[]
                    {
                        new EqualsSelector(V1ServiceExtension.CiliumLoadBalancerManagedKey, "true")
                    }
                ).ToExpression()
            );
    }

    public Task DeletedAsync(V1Service service, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task ReconcileAsync(V1Service service, CancellationToken cancellationToken)
    {
        // using var _ = Disposable.Create(() => _requeue(service, TimeSpan.FromSeconds(15)));
        if (service.Spec.Type != "LoadBalancer")
        {
            _logger.LogDebug("Service {Entity} is not a load balancer, skipping", service.Name());
            return;
        }

        var loadBalancerIps = (
            service.Status.LoadBalancer?.Ingress?.Select(x => x.Ip) ?? []
        ).ToImmutableSortedSet();
        if (loadBalancerIps.IsEmpty)
        {
            _logger.LogDebug(
                "Service {Entity} does not contain any allocated external LB IP address, skipping",
                service.Name()
            );
            return;
        }

        var owner = service.OwnerReferences()?.Where(x => x.Kind == "GameServer").FirstOrDefault();
        if (owner == null)
        {
            _logger.LogDebug(
                "Service {Entity} does not have a valid owner reference, skipping",
                service.Name()
            );
            return;
        }

        var gameServer = await _client.GetAsync<GameServer>(
            owner.Name,
            service.Namespace(),
            cancellationToken
        );
        if (gameServer == null)
        {
            _logger.LogDebug("Service {Entity} owner was dead, ignoring", service.Name());
            // await _client.DeleteAsync(service, cancellationToken);
            return;
        }

        if (gameServer?.Status?.Addresses?.Count <= 0)
        {
            return;
        }

        var existingLoadBalancersAddresses = (gameServer!.Status.Addresses ?? [])
            .Where(address => address.Type == "LoadBalancer")
            .Select(addr => addr.Address)
            .ToImmutableSortedSet();

        if (existingLoadBalancersAddresses == loadBalancerIps)
        {
            return;
        }
        gameServer.Status.Addresses =
        [
            .. gameServer.Status.Addresses?.Where(address => address.Type != "LoadBalancer") ?? [],
            .. loadBalancerIps.Select(ip => new V1NodeAddress() { Type = "LoadBalancer", Address = ip }),
        ];
        await _client.UpdateAsync(gameServer, cancellationToken);
        _logger.LogDebug(
            "Service {Entity} updated for {GameServer}",
            service.Name(),
            gameServer.Name()
        );
    }
}
