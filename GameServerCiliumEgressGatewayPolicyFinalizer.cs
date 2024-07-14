using System.Diagnostics;
using k8s.Models;
using KubeOps.Abstractions.Finalizer;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;

public class GameServerCiliumEgressGatewayPolicyFinalizer(ILogger<GameServerCiliumEgressGatewayPolicyFinalizer> _logger, IKubernetesClient _client) : IEntityFinalizer<GameServer>
{
    public async Task FinalizeAsync(GameServer gameServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Finalizing GameServer {Entity}.", gameServer.Name());

        if (!gameServer.IsCiliumEgressGatewayPolicyEnabledFromAnnotation())
        {
            _logger.LogDebug("GameServer {Entity} does not have egress gateway policy enabled.", gameServer.Name());
            return;
        }

        var ciliumEgressGatewayPolicyName = gameServer.GetCiliumEgressGatewayPolicyGeneratedName();
        if (ciliumEgressGatewayPolicyName == null)
        {
            _logger.LogCritical("GameServer {Entity} does not have egress gateway policy name. This should be unreachable.", gameServer.Name());
            throw new UnreachableException();
        }

        var ciliumEgressGatewayPolicy = await _client.GetAsync<CiliumEgressGatewayPolicy>(ciliumEgressGatewayPolicyName, cancellationToken: cancellationToken);
        if (ciliumEgressGatewayPolicy == null)
        {
            _logger.LogError("GameServer {Entity} should have egress gateway policy created. Please look for the relevant resources manually", gameServer.Name());
            return;
        }

        await _client.DeleteAsync(ciliumEgressGatewayPolicy, cancellationToken);
        _logger.LogInformation("Cilium Egress Gateway Policy deleted for {GameServer}", gameServer.Name());
    }
}
