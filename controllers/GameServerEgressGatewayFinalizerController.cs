using k8s.Models;
using KubeOps.Abstractions.Controller;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Rbac;
using KubeOps.KubernetesClient;
using KubeOps.KubernetesClient.LabelSelectors;
using Microsoft.Extensions.Logging;

namespace AgonesLoadBalancerWatcher;

[EntityRbac(typeof(GameServer), Verbs = RbacVerb.All)]
public class GameServerEgressGatewayFinalizerController(
    ILogger<GameServerEgressGatewayFinalizerController> _logger,
    IKubernetesClient _client
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
                            GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyEnabledKey,
                            "true"
                        )
                    }
                ).ToExpression()
            );
    }

    public async Task DeletedAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deleting entity {Entity}.", gameServer.Name());

        if (
            gameServer.RemoveFinalizer(
                GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey
            )
        )
        {
            await _client.UpdateAsync(gameServer, cancellationToken);
            _logger.LogInformation(
                "Removed Cilium Egress Gateway finalizer for GameServer {Entity}.",
                gameServer.Name()
            );
        }
    }

    public async Task ReconcileAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reconciling entity {Entity}.", gameServer.Name());

        if (
            !gameServer.AddFinalizer(
                GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey
            )
        )
        {
            return;
        }
        await _client.UpdateAsync(gameServer, cancellationToken);
        _logger.LogInformation(
            "Added Cilium Egress Gateway finalizer for GameServer {Entity}.",
            gameServer.Name()
        );
    }
}
