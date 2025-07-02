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

[EntityRbac(typeof(CiliumEgressGatewayPolicy), Verbs = RbacVerb.All)]
[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class CiliumEgressGatewayPolicyController(
    ILogger<CiliumEgressGatewayPolicyController> _logger,
    IKubernetesClient _client,
    EntityRequeue<CiliumEgressGatewayPolicy> _requeue
) : IEntityController<CiliumEgressGatewayPolicy>
{
    public class LabelSelector : IEntityLabelSelector<CiliumEgressGatewayPolicy>
    {
        public ValueTask<string?> GetLabelSelectorAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(
                (
                    new KubeOps.KubernetesClient.LabelSelectors.LabelSelector[]
                    {
                        new EqualsSelector(CiliumEgressGatewayPolicyExtension.IsManagedKey, "true")
                    }
                ).ToExpression()
            );
    }

    public Task DeletedAsync(
        CiliumEgressGatewayPolicy policy,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public async Task ReconcileAsync(
        CiliumEgressGatewayPolicy policy,
        CancellationToken cancellationToken
    )
    {
        // using var _ = Disposable.Create(() => _requeue(policy, TimeSpan.FromSeconds(15)));

        var gameServer = await _client.GetAsync<GameServer>(
            policy.GetLabel(CiliumEgressGatewayPolicyExtension.GameServerReferenceNameKey),
            policy.GetLabel(CiliumEgressGatewayPolicyExtension.GameServerReferenceNamespaceKey),
            cancellationToken: cancellationToken
        );

        if (gameServer == null)
        {
            _logger.LogWarning(
                "CiliumEgressGatewayPolicy {Name} did not have game server reference in labels",
                policy.Name()
            );
        }

        if (!(gameServer?.Status?.Addresses?.Count > 0))
        {
            return;
        }

        V1Service? service = await _client.GetAsync<V1Service>(
            policy.GetLabel(CiliumEgressGatewayPolicyExtension.ServiceReferenceNameKey),
            policy.GetLabel(CiliumEgressGatewayPolicyExtension.ServiceReferenceNamespaceKey),
            cancellationToken: cancellationToken
        );
        if (service == null)
        {
            _logger.LogWarning(
                "CiliumEgressGatewayPolicy {Name} did not have service reference in labels",
                policy.Name()
            );
        }

        if (gameServer == null && service == null)
        {
            _logger.LogError(
                "CiliumEgressGatewayPolicy {Name} owners are invalid, self-destructing",
                policy.Name()
            );
            await _client.DeleteAsync(policy, cancellationToken);
            return;
        }

        var loadBalancerIpsInGameServerButNotInService = gameServer!
            .Status.Addresses.Where(address => address.Type == "LoadBalancer")
            .Select(address => address.Address)
            .Except(service?.Status?.LoadBalancer?.Ingress?.Select(ingress => ingress.Ip) ?? []);
        if (!loadBalancerIpsInGameServerButNotInService.Any())
        {
            return;
        }

        // Let the Service controller reattach a new CiliumEgressGatewayPolicy that matches the load balancer IPs
        _logger.LogError(
            "CiliumEgressGatewayPolicy {Name} contains load balancer IPs in game server but not in service, self-destructing",
            policy.Name()
        );
        await _client.DeleteAsync(policy, cancellationToken);
    }
}
