using System.Diagnostics;
using k8s.Models;
using KubeOps.Abstractions.Finalizer;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

namespace AgonesLoadBalancerWatcher;

public class GameServerCiliumEgressGatewayPolicyFinalizer(
    ILogger<GameServerCiliumEgressGatewayPolicyFinalizer> _logger,
    IKubernetesClient _client
) : IEntityFinalizer<GameServer>
{
    public async Task FinalizeAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Finalizing GameServer {Entity}.", gameServer.Name());

        if (
            gameServer.GetLabel(
                GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyEnabledKey
            ) == null
        )
        {
            _logger.LogDebug(
                "GameServer {Entity} does not have egress gateway policy enabled.",
                gameServer.Name()
            );
            return;
        }

        var ciliumEgressGatewayPolicyName = gameServer.GetCiliumEgressGatewayPolicyGeneratedName();
        if (ciliumEgressGatewayPolicyName == null)
        {
            _logger.LogCritical(
                "GameServer {Entity} does not have egress gateway policy name. This should be unreachable.",
                gameServer.Name()
            );
            throw new UnreachableException();
        }

        await Utility.ReadModifyWrite(
            fetch: () =>
                _client.GetAsync<CiliumEgressGatewayPolicy>(
                    ciliumEgressGatewayPolicyName,
                    cancellationToken: cancellationToken
                ),
            admitNullForFetchResult: true,
            transact: async policy =>
            {
                if (policy != null)
                {
                    await _client.DeleteAsync(policy, cancellationToken);
                    _logger.LogInformation(
                        "Cilium Egress Gateway Policy deleted for {GameServer}",
                        gameServer.Name()
                    );
                }
            },
            cancellationToken: cancellationToken
        );
    }
}
