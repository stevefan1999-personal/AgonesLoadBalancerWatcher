using System.Diagnostics;
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class GameServerController(ILogger<GameServerController> _logger, IKubernetesClient _client, EntityRequeue<GameServer> _requeue) : IEntityController<GameServer>
{
    private const string CiliumLoadBalancerManagedKey = "k8s.stevefan1999.tech/cilium-load-balancer-managed";

    public Task DeletedAsync(GameServer gameServer, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ReconcileAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reconciling entity {Entity}.", gameServer.Name());

        async Task WatchPopulationEventAndAttachFinalizer()
        {
            for (; ; )
            {
                try
                {
                    var gameServer_ = (await _client.GetAsync<GameServer>(gameServer.Name(), gameServer.Namespace(), cancellationToken)) ?? throw new UnreachableException();
                    if (gameServer_.AttachCiliumEgressGatewayPolicyFinalizer())
                    {
                        await _client.UpdateAsync(gameServer_, cancellationToken);
                        _logger.LogInformation("Updated Cilium Egress Gateway finalizer for GameServer {Entity}.", gameServer.Name());
                    }
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

        async Task CreateLoadBalancerService()
        {
            for (; ; )
            {
                try
                {
                    var name = gameServer.GetCiliumLoadBalancerServiceGeneratedName() ?? throw new UnreachableException();
                    var service = await _client.GetAsync<V1Service>(name, @namespace: gameServer.Namespace(), cancellationToken: cancellationToken);
                    if (service?.IsCiliumLoadBalancerManaged() ?? false)
                    {
                        break;
                    }
                    service = await TransformGameServerToV1Service(gameServer, cancellationToken);
                    if (service.Spec.Ports.Count > 0)
                    {
                        await _client.SaveAsync(service, cancellationToken);
                        _logger.LogInformation("Saved Cilium load balancer service for GameServer {Entity}.", gameServer.Name());
                    }
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

        List<Task> tasks = [];

        var isLoadBalancerEnabled = gameServer.IsCiliumLoadBalancerEnabledFromAnnotation();
        if (isLoadBalancerEnabled)
        {
            tasks.Add(CreateLoadBalancerService().WaitAsync(cancellationToken));
        }
        else
        {
            _logger.LogDebug("GameServer {Entity} does not have Cilium load balancer service enabled.", gameServer.Name());
        }

        var isCiliumPolicyEnabled = gameServer.IsCiliumEgressGatewayPolicyEnabledFromAnnotation();
        if (isCiliumPolicyEnabled)
        {
            tasks.Add(WatchPopulationEventAndAttachFinalizer().WaitAsync(cancellationToken));
        }
        else
        {
            _logger.LogDebug("GameServer {Entity} does not have Cilium Egress Gateway Policy enabled.", gameServer.Name());
        }

        if (isLoadBalancerEnabled || isCiliumPolicyEnabled)
        {
            _requeue(gameServer, TimeSpan.FromSeconds(10));
        }

        return Task.Run(() => Task.WhenAll(tasks).WaitAsync(cancellationToken), cancellationToken).WaitAsync(cancellationToken);
    }

    async Task<V1Service> TransformGameServerToV1Service(GameServer gameServer, CancellationToken cancellationToken)
    {
        if (gameServer == null)
        {
            throw new UnreachableException();
        }

        var name = gameServer.GetCiliumLoadBalancerServiceGeneratedName() ?? throw new UnreachableException();
        var service = (await _client.GetAsync<V1Service>(name, cancellationToken: cancellationToken)) ?? new V1Service();

        service.Metadata ??= new();
        service.Metadata.Name = name;
        service.Metadata.NamespaceProperty = gameServer.Namespace();
        service.Metadata.Annotations = new Dictionary<string, string>
        {
            ["lbipam.cilium.io/sharing-cross-namespace"] = gameServer.GetCiliumLoadBalancerSharingAcrossNamespaceFromAnnotation() ?? "*",
            ["lbipam.cilium.io/sharing-key"] = gameServer.GetCiliumLoadBalancerSharingKeyFromAnnotation() ?? " ",
        };
        service.Metadata.Labels = new Dictionary<string, string>
        {
            [CiliumLoadBalancerManagedKey] = "true"
        };
        service.Metadata.OwnerReferences = new List<V1OwnerReference> {
            new()
            {
                ApiVersion = gameServer.ApiVersion,
                Kind = gameServer.Kind,
                Name = gameServer.Name(),
                Uid = gameServer.Uid(),
                Controller = true
            }
        };
        service.Spec ??= new();
        service.Spec.Type = "LoadBalancer";
        service.Spec.Selector = new Dictionary<string, string>
        {
            ["agones.dev/gameserver"] = gameServer.Name()
        };
        service.Spec.Ports = gameServer.Spec.Ports?.Where(port => port.Protocol != "TCPUDP").Select(port => port.Protocol switch
        {
            "TCP" => new V1ServicePort()
            {
                Name = port.Name,
                Port = port.ContainerPort,
                TargetPort = port.ContainerPort,
                Protocol = "TCP"
            },
            "UDP" => new V1ServicePort()
            {
                Name = port.Name,
                Port = port.ContainerPort,
                TargetPort = port.ContainerPort,
                Protocol = "UDP"
            },
            // "TCPUDP" case maybe?
            _ => throw new NotImplementedException(),
        })?.ToList() ?? [];


        var ips = gameServer.GetCiliumLoadBalancerIpsFromAnnotation();
        if (ips != null)
        {
            service.Annotations().Add("lbipam.cilium.io/ips", ips);
        }

        return service;
    }
}
