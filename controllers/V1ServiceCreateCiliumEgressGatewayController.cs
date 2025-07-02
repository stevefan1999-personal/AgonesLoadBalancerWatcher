using System.Collections.Immutable;
using System.Diagnostics;
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
[EntityRbac(typeof(CiliumEgressGatewayPolicy), Verbs = RbacVerb.All)]
public class V1ServiceCreateCiliumEgressGatewayController(
    ILogger<V1ServiceCreateCiliumEgressGatewayController> _logger,
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
                        new EqualsSelector(V1ServiceExtension.CiliumLoadBalancerManagedKey, "true"),
                        new EqualsSelector(
                            V1ServiceExtension.CiliumEgressGatewayPolicyEnabledKey,
                            "true"
                        )
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

        var nodes = await _client.ListAsync<V1Node>(cancellationToken: cancellationToken);

        var eligibleNodesWithLoadBalancerIp = GetEligibleNodesWithLoadBalancerIps(
            nodes,
            loadBalancerIps
        );

        (V1Node node, V1NodeAddress address)? GetLoadBalancerAssociatedIP(string type)
        {
            var matchingNode = eligibleNodesWithLoadBalancerIp
                .Where(x => x.address.Type == type)
                .FirstOrDefault();

            return matchingNode.Equals(default) ? null : matchingNode;
        }

        var name =
            gameServer.GetCiliumEgressGatewayPolicyGeneratedName()
            ?? throw new UnreachableException();

        var bestNodeInfo =
            GetLoadBalancerAssociatedIP("ExternalIP") ?? GetLoadBalancerAssociatedIP("InternalIP");

        if (bestNodeInfo == null)
        {
            _logger.LogError(
                "No node has neither any ExternalIPs nor any InternalIPs associated with load balancer IPs {LoadBalancerAddress}, skipping",
                string.Join(", ", loadBalancerIps)
            );
            return;
        }

        if (loadBalancerIps.Count != 1)
        {
            _logger.LogWarning(
                "Service {Entity} contains more than one external LB IP address, will use first one that was valid as default for egress",
                service.Name()
            );
        }
        await Utility.ReadModifyWrite(
            fetch: async () =>
                (
                    await _client.ListAsync<CiliumEgressGatewayPolicy>(
                        labelSelector: (
                            new KubeOps.KubernetesClient.LabelSelectors.LabelSelector[]
                            {
                                new NotEqualsSelector(
                                    CiliumEgressGatewayPolicyExtension.IsManagedKey,
                                    "true"
                                ),
                                new EqualsSelector(
                                    CiliumEgressGatewayPolicyExtension.GameServerReferenceNamespaceKey,
                                    gameServer.Namespace()
                                ),
                                new EqualsSelector(
                                    CiliumEgressGatewayPolicyExtension.GameServerReferenceNameKey,
                                    gameServer.Name()
                                ),
                                new EqualsSelector(
                                    CiliumEgressGatewayPolicyExtension.ServiceReferenceNamespaceKey,
                                    service.Namespace()
                                ),
                                new EqualsSelector(
                                    CiliumEgressGatewayPolicyExtension.ServiceReferenceNameKey,
                                    service.Name()
                                ),
                            }
                        ).ToExpression(),
                        cancellationToken: cancellationToken
                    )
                ).FirstOrDefault(),
            admitNullForFetchResult: true,
            transact: async (egressGatewayPolicy) =>
            {
                egressGatewayPolicy = new CiliumEgressGatewayPolicy
                {
                    // Why?
                    Kind = "CiliumEgressGatewayPolicy",
                    ApiVersion = "cilium.io/v2"
                };
                egressGatewayPolicy.Metadata ??= new();
                egressGatewayPolicy.Metadata.Name = name;
                egressGatewayPolicy.Metadata.OwnerReferences =
                [
                    gameServer.MakeOwnerReference(),
                    service.MakeOwnerReference(),
                ];
                egressGatewayPolicy.Metadata.Labels = new Dictionary<string, string>
                {
                    [CiliumEgressGatewayPolicyExtension.IsManagedKey] = "true",
                    [CiliumEgressGatewayPolicyExtension.GameServerReferenceNamespaceKey] =
                        gameServer.Namespace(),
                    [CiliumEgressGatewayPolicyExtension.GameServerReferenceNameKey] =
                        gameServer.Name(),
                    [CiliumEgressGatewayPolicyExtension.ServiceReferenceNamespaceKey] =
                        service.Namespace(),
                    [CiliumEgressGatewayPolicyExtension.ServiceReferenceNameKey] = service.Name(),
                };

                egressGatewayPolicy.Spec ??= new();
                egressGatewayPolicy.Spec.DestinationCIDRs = ["0.0.0.0/0"];

                egressGatewayPolicy.Spec.EgressGateway ??= new();

                var (bestNode, bestAddress) = bestNodeInfo.Value;
                egressGatewayPolicy.Spec.EgressGateway.EgressIP = bestAddress.Address;
                egressGatewayPolicy.Spec.EgressGateway.NodeSelector = new()
                {
                    MatchLabels = new Dictionary<string, string>()
                    {
                        ["kubernetes.io/hostname"] = bestNode.GetLabel("kubernetes.io/hostname")
                    }
                };

                egressGatewayPolicy.Spec.Selectors =
                [
                    new()
                    {
                        PodSelector = new()
                        {
                            MatchLabels = new Dictionary<string, string>()
                            {
                                ["agones.dev/gameserver"] = gameServer.Name(),
                                ["io.kubernetes.pod.namespace"] = gameServer.Namespace(),
                            }
                        }
                    }
                ];

                egressGatewayPolicy.AddFinalizer(
                    GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey
                );

                await _client.SaveAsync(egressGatewayPolicy, cancellationToken);
                _logger.LogInformation(
                    "Created egress gateway policy for game server {GameServer}",
                    gameServer.Name()
                );
            },
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Finds nodes that have addresses matching the load balancer IPs.
    /// Includes both node status addresses and external IPs from annotations.
    /// </summary>
    /// <param name="nodes">Collection of Kubernetes nodes</param>
    /// <param name="loadBalancerIps">Set of load balancer IP addresses to match</param>
    /// <returns>Immutable sorted set of eligible nodes with their matching addresses</returns>
    private static ImmutableSortedSet<(
        V1Node node,
        V1NodeAddress address
    )> GetEligibleNodesWithLoadBalancerIps(
        IList<V1Node> nodes,
        ImmutableSortedSet<string> loadBalancerIps
    )
    {
        if (nodes?.Count == 0 || loadBalancerIps.IsEmpty)
        {
            return [];
        }

        // Create a HashSet for O(1) lookup performance
        var loadBalancerIpSet = loadBalancerIps.ToHashSet();

        return
        [
            ..from node in nodes
                where node.Status?.Addresses != null
                from address in node.Status.Addresses
                where
                    !string.IsNullOrWhiteSpace(address.Address)
                    && loadBalancerIpSet.Contains(address.Address)
                select (node, address),
            .. from node in nodes
                let externalIpAnnotation = node.GetAnnotation("k0sproject.io/node-ip-external")
                where !string.IsNullOrWhiteSpace(externalIpAnnotation)
                from address in externalIpAnnotation
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(ip => ip.Trim())
                    .Where(ip => !string.IsNullOrWhiteSpace(ip))
                where loadBalancerIpSet.Contains(address)
                select (node, new V1NodeAddress { Type = "ExternalIP", Address = address })
        ];
    }
}
