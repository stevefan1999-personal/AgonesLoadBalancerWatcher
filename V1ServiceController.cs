using System.Collections.Immutable;
using System.Diagnostics;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class V1ServiceController(
    ILogger<V1ServiceController> _logger,
    IKubernetesClient _client,
    EntityRequeue<V1Service> _requeue
) : IEntityController<V1Service>
{
    public Task DeletedAsync(V1Service service, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task ReconcileAsync(V1Service service, CancellationToken cancellationToken)
    {
        if (!service.IsCiliumLoadBalancerManagedFromLabels())
        {
            _logger.LogDebug(
                "Service {Entity} does not contain load balancer enabled label, skipping",
                service.Name()
            );
            return;
        }

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

        Task PopulateLoadBalancerAddress() =>
            Utility.ReadModifyWrite(
                fetch: async () =>
                {
                    var gameServer = await _client.GetAsync<GameServer>(
                        owner.Name,
                        service.Namespace(),
                        cancellationToken
                    );
                    return gameServer?.Status?.Addresses?.Count switch
                    {
                        <= 0 => null,
                        _ => gameServer
                    };
                },
                transact: async (gameServer) =>
                {
                    var existingLoadBalancersAddresses = (gameServer!.Status.Addresses ?? [])
                        .Where(address => address.Type == "LoadBalancer")
                        .Select(addr => addr.Address)
                        .ToImmutableSortedSet();

                    if (existingLoadBalancersAddresses != loadBalancerIps)
                    {
                        gameServer.Status.Addresses = (gameServer.Status.Addresses ?? [])
                            .Where(address => address.Type != "LoadBalancer")
                            .Concat(
                                loadBalancerIps.Select(
                                    ip =>
                                        new V1NodeAddress() { Type = "LoadBalancer", Address = ip }
                                )
                            )
                            .ToList();
                        await _client.UpdateAsync(gameServer, cancellationToken);
                        _logger.LogDebug(
                            "Service {Entity} updated for {GameServer}",
                            service.Name(),
                            gameServer.Name()
                        );
                    }
                },
                cancellationToken: cancellationToken
            );

        async Task AddCiliumEgressEntry()
        {
            var nodes = await _client.ListAsync<V1Node>(cancellationToken: cancellationToken);

            var eligibleNodesWithLoadBalancerIp = (
                from node in nodes
                from address in node.Status.Addresses
                join loadBalancerIp in loadBalancerIps on address.Address equals loadBalancerIp
                select new { node, address }
            )
                .Concat(
                    from node in nodes
                    from address in node.GetAnnotation("k0sproject.io/node-ip-external")?.Split(',')
                        ?? []
                    join loadBalancerIp in loadBalancerIps on address equals loadBalancerIp
                    select new
                    {
                        node,
                        address = new V1NodeAddress { Type = "ExternalIP", Address = address }
                    }
                )
                .ToImmutableSortedSet();

            (V1Node node, V1NodeAddress address)? GetLoadBalancerAssociatedIP(string type)
            {
                var ips = eligibleNodesWithLoadBalancerIp.Where(x => x.address.Type == type);
                var count = ips.Count();

                if (count == 0)
                {
                    return null;
                }

                var value = ips.FirstOrDefault();

                return value switch
                {
                    null => null,
                    var x => (x.node, x.address)
                };
            }

            var name =
                gameServer.GetCiliumEgressGatewayPolicyGeneratedName()
                ?? throw new UnreachableException();

            var bestNodeInfo =
                GetLoadBalancerAssociatedIP("ExternalIP")
                ?? GetLoadBalancerAssociatedIP("InternalIP");
            await Utility.ReadModifyWrite(
                fetch: () =>
                    _client.GetAsync<CiliumEgressGatewayPolicy>(
                        name,
                        cancellationToken: cancellationToken
                    ),
                admitNullForFetchResult: true,
                transact: async (egressGatewayPolicy) =>
                {
                    if (egressGatewayPolicy?.IsManagedFromAnnotation() ?? false)
                    {
                        return;
                    }

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
                    egressGatewayPolicy.Metadata.Annotations = new Dictionary<string, string>
                    {
                        [CiliumEgressGatewayPolicyExtension.IsManagedKey] = "true",
                        [CiliumEgressGatewayPolicyExtension.GameServerReferenceKey] =
                            $"{gameServer.Namespace()}/{gameServer.Name()}",
                        [CiliumEgressGatewayPolicyExtension.ServiceReferenceKey] =
                            $"{service.Namespace()}/{service.Name()}",
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

                    egressGatewayPolicy.AttachCiliumEgressGatewayPolicyFinalizer();

                    await _client.SaveAsync(egressGatewayPolicy, cancellationToken);
                    _logger.LogInformation(
                        "Created egress gateway policy for game server {GameServer}",
                        gameServer.Name()
                    );
                },
                cancellationToken: cancellationToken
            );
        }

        List<Task> tasks =
        [
            Task.Run(PopulateLoadBalancerAddress, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
        ];

        if (gameServer.IsCiliumEgressGatewayPolicyEnabledFromAnnotation())
        {
            tasks.Add(
                Task.Run(AddCiliumEgressEntry, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
            );
        }
        else
        {
            _logger.LogDebug(
                "Cilium Egress Gateway Policy is not enabled for GameServer {Entity}.",
                gameServer.Name()
            );
        }

        var _ = Task.Run(
            () => Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken),
            cancellationToken
        );
        _requeue(service, TimeSpan.FromSeconds(15));
        return;
    }
}
