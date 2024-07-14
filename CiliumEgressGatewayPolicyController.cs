using System.Collections.Immutable;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

[EntityRbac(typeof(CiliumEgressGatewayPolicy), Verbs = RbacVerb.All)]
public class CiliumEgressGatewayPolicyController(ILogger<CiliumEgressGatewayPolicy> _logger, IKubernetesClient _client, EntityRequeue<CiliumEgressGatewayPolicy> _requeue) : IEntityController<CiliumEgressGatewayPolicy>
{
    public Task DeletedAsync(CiliumEgressGatewayPolicy policy, CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ReconcileAsync(CiliumEgressGatewayPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.IsManagedFromAnnotation())
        {
            _logger.LogDebug("CiliumEgressGatewayPolicy {Name} is not managed by this controller, ignoring", policy.Name());
            return;
        }
        else
        {
            _requeue(policy, TimeSpan.FromSeconds(10));
        }

        GameServer? gameServer = null;
        var gameServerRef = policy.GetGameServerReferenceFromAnnotation();
        if (gameServerRef != null)
        {
            gameServer = await _client.GetAsync<GameServer>(gameServerRef.Value.Name, @namespace: gameServerRef.Value.Namespace, cancellationToken: cancellationToken);
        }
        else
        {
            _logger.LogWarning("CiliumEgressGatewayPolicy {Name} did not have game server reference in annotations, this will be an error in the future", policy.Name());
        }

        V1Service? service = null;
        var serviceRef = policy.GetServiceReferenceFromAnnotation();
        if (serviceRef != null)
        {
            service = await _client.GetAsync<V1Service>(serviceRef.Value.Name, @namespace: serviceRef.Value.Namespace, cancellationToken: cancellationToken);
        }
        else
        {
            _logger.LogWarning("CiliumEgressGatewayPolicy {Name} did not have service reference in annotations, this will be an error in the future", policy.Name());
        }

        if (!(gameServer != null && service != null))
        {
            _logger.LogError("CiliumEgressGatewayPolicy {Name} owners are invalid, self-destructing", policy.Name());
            await _client.DeleteAsync(policy, cancellationToken);
            return;
        }

        var serviceLoadBalancerIps = service.Status?.LoadBalancer?.Ingress?.Select(ingress => ingress.Ip) ?? [];
        var gameServerLoadBalancerIps = gameServer.Status?.Addresses?.Where(address => address.Type == "LoadBalancer")?.Select(address => address.Address) ?? [];

        foreach (var gameServerLoadBalancerIp in gameServerLoadBalancerIps)
        {
            if (!serviceLoadBalancerIps.Contains(gameServerLoadBalancerIp))
            {
                _logger.LogError("CiliumEgressGatewayPolicy {Name} load balancer IPs between game server and service mismatch, self-destructing", policy.Name());
                await _client.DeleteAsync(policy, cancellationToken);
                return;
            }
        }
    }
}

