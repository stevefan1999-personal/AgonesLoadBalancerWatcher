using k8s.Models;

namespace AgonesLoadBalancerWatcher;

public static class V1ServiceExtension
{
    public const string CiliumLoadBalancerManagedKey =
        "k8s.stevefan1999.tech/cilium-load-balancer-managed";
    public const string CiliumEgressGatewayPolicyEnabledKey =
        GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyEnabledKey;
}
