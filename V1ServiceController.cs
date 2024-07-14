using System.Diagnostics;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class V1ServiceController(ILogger<V1ServiceController> _logger, IKubernetesClient _client, EntityRequeue<V1Service> _requeue) : IEntityController<V1Service>
{

    public Task DeletedAsync(V1Service service, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task ReconcileAsync(V1Service service, CancellationToken cancellationToken)
    {
        if (!service.IsCiliumLoadBalancerManaged())
        {
            _logger.LogDebug("Service {Entity} does not contain load balancer enabled label, skipping", service.Name());
            return;
        }
        else
        {
            _requeue(service, TimeSpan.FromSeconds(10));
        }

        if (service.Spec.Type != "LoadBalancer")
        {
            _logger.LogDebug("Service {Entity} is not a load balancer, skipping", service.Name());
            return;
        }

        var loadBalancerIps = service.Status.LoadBalancer?.Ingress?.Select(x => x.Ip) ?? [];
        if (!loadBalancerIps.Any())
        {
            _logger.LogDebug("Service {Entity} does not contain any allocated external LB IP address, skipping", service.Name());
            return;
        }

        if (loadBalancerIps.Count() != 1)
        {
            _logger.LogWarning("Service {Entity} contains more than one external LB IP address, will use first one that was valid as default", service.Name());
        }

        var owner = service.OwnerReferences()?.Where(x => x.Kind == "GameServer").FirstOrDefault();
        if (owner == null)
        {
            _logger.LogDebug("Service {Entity} does not have a valid owner reference, skipping", service.Name());
            return;
        }

        async Task PopulateLoadBalancerAddress()
        {
            var gameServer = await _client.GetAsync<GameServer>(owner.Name, service.Namespace(), cancellationToken) ?? throw new UnreachableException();
            if (gameServer == null)
            {
                _logger.LogDebug("Service {Entity} does not have a valid game server, skipping", service.Name());
                return;
            }
            for (; ; )
            {
                try
                {
                    gameServer = await _client.GetAsync<GameServer>(gameServer.Name(), gameServer.Namespace(), cancellationToken) ?? throw new UnreachableException();
                    if (gameServer.Status?.Addresses == null)
                    {
                        throw new UnreachableException();
                    }

                    if (gameServer.Status.Addresses.Count <= 0)
                    {
                        continue;
                    }

                    var shouldUpdate = false;

                    foreach (var address in loadBalancerIps)
                    {
                        if (!gameServer.Status.Addresses.Any(x => x.Address == address))
                        {
                            gameServer.Status.Addresses.Add(new() { Address = address, Type = "LoadBalancer" });
                            shouldUpdate = true;
                        }
                    }


                    if (shouldUpdate)
                    {
                        await _client.UpdateAsync((GameServer)gameServer, cancellationToken);
                        _logger.LogDebug("Service {Entity} updated for {GameServer}", service.Name(), gameServer.Name());
                    }

                    return;
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict || ex.Response.Content.Contains("leader changed"))
                {
                    continue;
                }
                catch (KubernetesException ex) when (ex.Status.Code == (int)HttpStatusCode.Gone)
                {
                    continue;
                }

            }
        }

        async Task AddCiliumEgressEntry()
        {
            var gameServer = await _client.GetAsync<GameServer>(owner.Name, service.Namespace(), cancellationToken);
            if (gameServer == null)
            {
                _logger.LogDebug("Service {Entity} does not have a valid game server, skipping", service.Name());
                return;
            }

            var nodes = await _client.ListAsync<V1Node>(cancellationToken: cancellationToken);

            var eligibleNodesWithLoadBalancerIp =
                from node in nodes
                from address in node.Status.Addresses
                join loadBalancerIp in loadBalancerIps on address.Address equals loadBalancerIp
                select new { node, address }
            ;

            (V1Node node, V1NodeAddress address)? GetLoadBalancerAssociatedIP(string type)
            {
                var ips = eligibleNodesWithLoadBalancerIp.Where(x => x.address.Type == type);
                var count = ips.Count();

                if (count == 0)
                {
                    _logger.LogWarning($"No {type} detected on the nodes with the associated load balancer");
                    return null;
                }

                if (count != 1)
                {
                    _logger.LogWarning($"More than one {type} detected on the nodes with the associated load balancer IP, using the first one as a default");
                }

                var value = ips.FirstOrDefault();

                return value switch
                {
                    null => null,
                    var x => (x.node, x.address)
                };
            }


            var name = gameServer.GetCiliumEgressGatewayPolicyGeneratedName() ?? throw new UnreachableException();

            for (; ; )
            {
                try
                {
                    var egressGatewayPolicy = await _client.GetAsync<CiliumEgressGatewayPolicy>(name, cancellationToken: cancellationToken);

                    if (egressGatewayPolicy?.IsManagedFromAnnotation() ?? false)
                    {
                        break;
                    }

                    egressGatewayPolicy = new CiliumEgressGatewayPolicy();

                    // Why?
                    egressGatewayPolicy.Kind = "CiliumEgressGatewayPolicy";
                    egressGatewayPolicy.ApiVersion = "cilium.io/v2";
                    egressGatewayPolicy.Metadata ??= new();
                    egressGatewayPolicy.Metadata.Name = name;
                    egressGatewayPolicy.Metadata.OwnerReferences = [
                        gameServer.MakeOwnerReference(),
                        service.MakeOwnerReference(),
                    ];
                    egressGatewayPolicy.Metadata.Annotations = new Dictionary<string, string>
                    {
                        [CiliumEgressGatewayPolicyExtension.IsManagedKey] = "true",
                        [CiliumEgressGatewayPolicyExtension.GameServerReferenceKey] = $"{gameServer.Namespace()}/{gameServer.Name()}",
                        [CiliumEgressGatewayPolicyExtension.ServiceReferenceKey] = $"{service.Namespace()}/{service.Name()}",
                    };

                    egressGatewayPolicy.Spec ??= new();
                    egressGatewayPolicy.Spec.DestinationCIDRs = ["0.0.0.0/0"];

                    egressGatewayPolicy.Spec.EgressGateway ??= new();

                    var bestNodeInfo = GetLoadBalancerAssociatedIP("ExternalIP") ?? GetLoadBalancerAssociatedIP("InternalIP");
                    if (bestNodeInfo == null)
                    {
                        _logger.LogError("No node has neither any ExternalIPs nor any InternalIPs associated with load balancer IPs {LoadBalancerAddress}, skipping", string.Join(", ", loadBalancerIps));
                        return;
                    }

                    var (bestNode, bestAddress) = bestNodeInfo.Value;
                    egressGatewayPolicy.Spec.EgressGateway.EgressIP = bestAddress.Address;
                    egressGatewayPolicy.Spec.EgressGateway.NodeSelector = new()
                    {
                        MatchLabels = new Dictionary<string, string>()
                        {
                            ["kubernetes.io/hostname"] = bestNode.GetLabel("kubernetes.io/hostname")
                        }
                    };

                    egressGatewayPolicy.Spec.Selectors = [
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
                    _logger.LogInformation("Created egress gateway policy for game server {GameServer}", gameServer.Name());
                    break;
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict || ex.Response.Content.Contains("leader changed"))
                {
                    continue;
                }
                catch (KubernetesException ex) when (ex.Status.Code == (int)HttpStatusCode.Gone)
                {
                    continue;
                }
            }
        }

        List<Task> tasks = [PopulateLoadBalancerAddress().WaitAsync(cancellationToken)];

        var gameServer = await _client.GetAsync<GameServer>(owner.Name, service.Namespace(), cancellationToken) ?? throw new UnreachableException();
        if (gameServer.IsCiliumEgressGatewayPolicyEnabledFromAnnotation())
        {
            tasks.Add(AddCiliumEgressEntry().WaitAsync(cancellationToken));
        }
        else
        {
            _logger.LogDebug("Cilium Egress Gateway Policy is not enabled for GameServer {Entity}.", gameServer.Name());
        }

        var _ = Task.Run(() => Task.WhenAll(tasks).WaitAsync(cancellationToken), cancellationToken).WaitAsync(cancellationToken);
    }
}
