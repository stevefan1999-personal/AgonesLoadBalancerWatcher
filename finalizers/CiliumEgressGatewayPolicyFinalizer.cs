using k8s.Models;
using KubeOps.Abstractions.Finalizer;
using Microsoft.Extensions.Logging;

namespace AgonesLoadBalancerWatcher;

public class CiliumEgressGatewayPolicyFinalizer(ILogger<CiliumEgressGatewayPolicyFinalizer> _logger)
    : IEntityFinalizer<CiliumEgressGatewayPolicy>
{
    public Task FinalizeAsync(CiliumEgressGatewayPolicy policy, CancellationToken cancellationToken)
    {
        // Just a dummy finalizer
        _logger.LogInformation("Finalizing CiliumEgressGatewayPolicy {Entity}.", policy.Name());
        return Task.CompletedTask;
    }
}
