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

[EntityRbac(typeof(V1Service), Verbs = RbacVerb.All)]
[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class GameServerCreateV1ServiceController(
    ILogger<GameServerCreateV1ServiceController> _logger,
    IKubernetesClient _client,
    EntityRequeue<GameServer> _requeue
) : IEntityController<GameServer>
{
    public class LabelSelector : IEntityLabelSelector<GameServer>
    {
        public ValueTask<string?> GetLabelSelectorAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(
                (
                    new KubeOps.KubernetesClient.LabelSelectors.LabelSelector[]
                    {
                        new EqualsSelector(
                            GameServerLoadBalancerExtension.CiliumLoadBalancerEnabledKey,
                            "true"
                        )
                    }
                ).ToExpression()
            );
    }

    public Task DeletedAsync(GameServer gameServer, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task ReconcileAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        // using var _ = Disposable.Create(() => _requeue(gameServer, TimeSpan.FromSeconds(15)));

        _logger.LogInformation("Reconciling entity {Entity}.", gameServer.Name());
        if (gameServer.Spec.Ports?.Count < 1)
        {
            return;
        }

        await Utility.ReadModifyWrite(
            fetch: async () =>
                (
                    await _client.ListAsync<V1Service>(
                        labelSelector: (
                            new KubeOps.KubernetesClient.LabelSelectors.LabelSelector[]
                            {
                                new EqualsSelector("agones.dev/gameserver", gameServer.Name()),
                                new NotEqualsSelector(
                                    V1ServiceExtension.CiliumLoadBalancerManagedKey,
                                    "true"
                                )
                            }
                        ).ToExpression(),
                        @namespace: gameServer.Namespace(),
                        cancellationToken: cancellationToken
                    )
                ).FirstOrDefault(),
            admitNullForFetchResult: true,
            transact: async (service) =>
            {
                if (service != null)
                {
                    return;
                }

                service = await TransformGameServerToV1Service(gameServer, cancellationToken);
                if (service.Spec.Ports.Count <= 0)
                {
                    return;
                }
                await _client.SaveAsync(service, cancellationToken);
                _logger.LogInformation(
                    "Saved Cilium load balancer service for GameServer {Entity}.",
                    gameServer.Name()
                );
            },
            cancellationToken: cancellationToken
        );
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

        var name = gameServer.GetCiliumLoadBalancerServiceGeneratedName();
        var service =
            (
                await _client.GetAsync<V1Service>(
                    name,
                    gameServer.Namespace(),
                    cancellationToken: cancellationToken
                )
            ) ?? new V1Service();

        service.Metadata ??= new();
        service.Metadata.Name = name;
        service.Metadata.NamespaceProperty = gameServer.Namespace();
        service.Metadata.Labels = new Dictionary<string, string>
        {
            [V1ServiceExtension.CiliumLoadBalancerManagedKey] = "true",
            ["agones.dev/gameserver"] = gameServer.Name(),
        };
        if (
            gameServer.GetLabel(
                GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyEnabledKey
            ) == "true"
        )
        {
            service.SetLabel(V1ServiceExtension.CiliumEgressGatewayPolicyEnabledKey, "true");
        }

        service.Metadata.Annotations = new Dictionary<string, string>
        {
            ["lbipam.cilium.io/sharing-cross-namespace"] =
                gameServer.GetAnnotation(
                    GameServerLoadBalancerExtension.CiliumLoadBalancerSharingAcrossNamespaceKey
                ) ?? "*",
            ["lbipam.cilium.io/sharing-key"] =
                gameServer.GetAnnotation(
                    GameServerLoadBalancerExtension.CiliumLoadBalancerSharingKey
                ) ?? " ",
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
                Protocol = protocol
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

        var ips = gameServer.GetAnnotation(
            GameServerLoadBalancerExtension.CiliumLoadBalancerIpsKey
        );
        if (ips != null)
        {
            service.SetAnnotation("lbipam.cilium.io/ips", ips);
        }

        return service;
    }
}
