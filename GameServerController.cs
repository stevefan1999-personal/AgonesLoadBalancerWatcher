using System.Diagnostics;
using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Queue;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class GameServerController(
    ILogger<GameServerController> _logger,
    IKubernetesClient _client,
    EntityRequeue<GameServer> _requeue
) : IEntityController<GameServer>
{
    private const string CiliumLoadBalancerManagedKey =
        "k8s.stevefan1999.tech/cilium-load-balancer-managed";

    public Task DeletedAsync(GameServer gameServer, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ReconcileAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reconciling entity {Entity}.", gameServer.Name());

        async Task WatchPopulationEventAndAttachFinalizer()
        {
            await Utility.ReadModifyWrite(
                fetch: async () =>
                {
                    var gameServer_ = await _client.GetAsync<GameServer>(
                        gameServer.Name(),
                        gameServer.Namespace(),
                        cancellationToken
                    );
                    return gameServer_?.Spec?.Ports?.Count switch
                    {
                        <= 0 => null,
                        _ => gameServer_
                    };
                },
                transact: async (gameServer) =>
                {
                    if (gameServer!.AttachCiliumEgressGatewayPolicyFinalizer())
                    {
                        await _client.UpdateAsync(gameServer!, cancellationToken);
                        _logger.LogInformation(
                            "Updated Cilium Egress Gateway finalizer for GameServer {Entity}.",
                            gameServer.Name()
                        );
                    }
                },
                cancellationToken: cancellationToken
            );
        }

        async Task CreateLoadBalancerService()
        {
            var name =
                gameServer.GetCiliumLoadBalancerServiceGeneratedName()
                ?? throw new UnreachableException();
            await Utility.ReadModifyWrite(
                fetch: () =>
                    _client.GetAsync<V1Service>(
                        name,
                        @namespace: gameServer.Namespace(),
                        cancellationToken: cancellationToken
                    ),
                admitNullForFetchResult: true,
                transact: async (service) =>
                {
                    if (service?.IsCiliumLoadBalancerManagedFromLabels() ?? false)
                    {
                        return;
                    }

                    service = await TransformGameServerToV1Service(gameServer, cancellationToken);
                    if (service.Spec.Ports.Count > 0)
                    {
                        await _client.SaveAsync(service, cancellationToken);
                        _logger.LogInformation(
                            "Saved Cilium load balancer service for GameServer {Entity}.",
                            gameServer.Name()
                        );
                    }
                },
                cancellationToken: cancellationToken
            );
        }

        List<Task> tasks = [];

        var isLoadBalancerEnabled = gameServer.IsCiliumLoadBalancerEnabledFromAnnotation();
        if (isLoadBalancerEnabled)
        {
            tasks.Add(
                Task.Run(CreateLoadBalancerService, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
            );
        }
        else
        {
            _logger.LogDebug(
                "GameServer {Entity} does not have Cilium load balancer service enabled.",
                gameServer.Name()
            );
        }

        var isCiliumPolicyEnabled = gameServer.IsCiliumEgressGatewayPolicyEnabledFromAnnotation();
        if (isCiliumPolicyEnabled)
        {
            tasks.Add(
                Task.Run(WatchPopulationEventAndAttachFinalizer, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
            );
        }
        else
        {
            _logger.LogDebug(
                "GameServer {Entity} does not have Cilium Egress Gateway Policy enabled.",
                gameServer.Name()
            );
        }

        Task.Run(
            () => Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken),
            cancellationToken
        );
        if (isLoadBalancerEnabled || isCiliumPolicyEnabled)
        {
            _requeue(gameServer, TimeSpan.FromSeconds(15));
        }
        return Task.CompletedTask;
    }

    async Task<V1Service> TransformGameServerToV1Service(
        GameServer gameServer,
        CancellationToken cancellationToken
    )
    {
        if (gameServer == null)
        {
            throw new UnreachableException();
        }

        var name =
            gameServer.GetCiliumLoadBalancerServiceGeneratedName()
            ?? throw new UnreachableException();
        var service =
            (await _client.GetAsync<V1Service>(name, cancellationToken: cancellationToken))
            ?? new V1Service();

        service.Metadata ??= new();
        service.Metadata.Name = name;
        service.Metadata.NamespaceProperty = gameServer.Namespace();
        service.Metadata.Annotations = new Dictionary<string, string>
        {
            ["lbipam.cilium.io/sharing-cross-namespace"] =
                gameServer.GetCiliumLoadBalancerSharingAcrossNamespaceFromAnnotation() ?? "*",
            ["lbipam.cilium.io/sharing-key"] =
                gameServer.GetCiliumLoadBalancerSharingKeyFromAnnotation() ?? " ",
        };
        service.Metadata.Labels = new Dictionary<string, string>
        {
            [CiliumLoadBalancerManagedKey] = "true"
        };
        service.Metadata.OwnerReferences =
        [
            new()
            {
                ApiVersion = gameServer.ApiVersion,
                Kind = gameServer.Kind,
                Name = gameServer.Name(),
                Uid = gameServer.Uid(),
                Controller = true
            }
        ];
        service.Spec ??= new();
        service.Spec.Type = "LoadBalancer";
        service.Spec.Selector = new Dictionary<string, string>
        {
            ["agones.dev/gameserver"] = gameServer.Name()
        };

        var genPort = (GameServer.GameServerPort port, string protocol, string? name = null) =>
            new V1ServicePort()
            {
                Name = name ?? port.Name,
                Port = port.HostPort ?? port.ContainerPort ?? throw new UnreachableException(),
                TargetPort = port.ContainerPort,
                Protocol = "TCP"
            };

        service.Spec.Ports =
            gameServer
                .Spec.Ports?.SelectMany(
                    port =>
                        port.Protocol switch
                        {
                            "TCP" => new List<V1ServicePort> { genPort(port, "TCP") },
                            "UDP" => [genPort(port, "UDP")],
                            "TCPUDP"
                                =>
                                [
                                    genPort(port, "TCP", $"{port.Name}-tcp"),
                                    genPort(port, "UDP", $"{port.Name}-udp"),
                                ],
                            _ => throw new NotImplementedException(),
                        }
                )
                ?.ToList() ?? [];

        var ips = gameServer.GetCiliumLoadBalancerIpsFromAnnotation();
        if (ips != null)
        {
            service.Annotations().Add("lbipam.cilium.io/ips", ips);
        }

        return service;
    }
}
