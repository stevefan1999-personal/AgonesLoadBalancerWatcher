using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

[EntityRbac(typeof(CiliumEgressGatewayPolicy), Verbs = RbacVerb.All)]
public class CiliumEgressGatewayPolicyController(
    ILogger<CiliumEgressGatewayPolicy> _logger,
    IKubernetesClient _client,
    EntityRequeue<CiliumEgressGatewayPolicy> _requeue
) : IEntityController<CiliumEgressGatewayPolicy>
{
    public Task DeletedAsync(
        CiliumEgressGatewayPolicy policy,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    public Task ReconcileAsync(
        CiliumEgressGatewayPolicy policy,
        CancellationToken cancellationToken
    )
    {
        if (!policy.IsManagedFromAnnotation())
        {
            _logger.LogDebug(
                "CiliumEgressGatewayPolicy {Name} is not managed by this controller, ignoring",
                policy.Name()
            );
            return Task.CompletedTask;
        }

        async Task CheckOwnerExistence()
        {
            GameServer? gameServer = null;
            var gameServerRef = policy.GetGameServerReferenceFromAnnotation();
            if (gameServerRef != null)
            {
                gameServer = await _client.GetAsync<GameServer>(
                    gameServerRef.Value.Name,
                    @namespace: gameServerRef.Value.Namespace,
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                _logger.LogWarning(
                    "CiliumEgressGatewayPolicy {Name} did not have game server reference in annotations",
                    policy.Name()
                );
            }

            V1Service? service = null;
            var serviceRef = policy.GetServiceReferenceFromAnnotation();
            if (serviceRef != null)
            {
                service = await _client.GetAsync<V1Service>(
                    serviceRef.Value.Name,
                    @namespace: serviceRef.Value.Namespace,
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                _logger.LogWarning(
                    "CiliumEgressGatewayPolicy {Name} did not have service reference in annotations",
                    policy.Name()
                );
            }

            if (gameServer != null && service != null)
            {
                return;
            }
            _logger.LogError(
                "CiliumEgressGatewayPolicy {Name} owners are invalid, self-destructing",
                policy.Name()
            );
            await _client.DeleteAsync(policy, cancellationToken);
        }

        async Task CheckLoadBalancerStatus()
        {
            GameServer? gameServer = null;
            var gameServerRef = policy.GetGameServerReferenceFromAnnotation();
            if (gameServerRef != null)
            {
                gameServer = await _client.GetAsync<GameServer>(
                    gameServerRef.Value.Name,
                    @namespace: gameServerRef.Value.Namespace,
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                _logger.LogWarning(
                    "CiliumEgressGatewayPolicy {Name} did not have game server reference in annotations, this will be an error in the future",
                    policy.Name()
                );
            }

            V1Service? service = null;
            var serviceRef = policy.GetServiceReferenceFromAnnotation();
            if (serviceRef != null)
            {
                service = await _client.GetAsync<V1Service>(
                    serviceRef.Value.Name,
                    @namespace: serviceRef.Value.Namespace,
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                _logger.LogWarning(
                    "CiliumEgressGatewayPolicy {Name} did not have service reference in annotations, this will be an error in the future",
                    policy.Name()
                );
            }

            var serviceIngressIps =
                service?.Status?.LoadBalancer?.Ingress?.Select(ingress => ingress.Ip) ?? [];

            if (!(gameServer?.Status?.Addresses?.Count > 0))
            {
                return;
            }

            var loadBalancerIpsInGameServerButNotInService = gameServer
                .Status.Addresses.Where(address => address.Type == "LoadBalancer")
                .Select(address => address.Address)
                .Except(serviceIngressIps);
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

        List<Task> tasks =
        [
            Task.Run(CheckOwnerExistence, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken),
            Task.Run(CheckLoadBalancerStatus, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken),
        ];
        Task.Run(
            () => Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken),
            cancellationToken
        );
        _requeue(policy, TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }
}
