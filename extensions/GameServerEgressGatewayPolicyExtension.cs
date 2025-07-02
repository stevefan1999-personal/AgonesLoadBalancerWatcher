using k8s.Models;

namespace AgonesLoadBalancerWatcher;

public static class GameServerEgressGatewayPolicyExtension
{
    public const string CiliumEgressGatewayPolicyEnabledKey =
        "k8s.stevefan1999.tech/cilium-egress-gateway-policy-enabled";

    public static string? GetCiliumEgressGatewayPolicyGeneratedName(this GameServer gameServer) =>
        $"agones-generated-{gameServer.Namespace()}-{gameServer.Name()}";

    public const string CiliumEgressGatewayPolicyFinalizerKey =
        "k8s.stevefan1999.tech/cilium-egress-gateway-policy-finalizer";
}
