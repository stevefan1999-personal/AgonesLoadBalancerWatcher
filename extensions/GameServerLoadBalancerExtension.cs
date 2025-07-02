using k8s.Models;

namespace AgonesLoadBalancerWatcher;

public static class GameServerLoadBalancerExtension
{
    public const string CiliumLoadBalancerEnabledKey =
        "k8s.stevefan1999.tech/cilium-load-balancer-enabled";

    public const string CiliumLoadBalancerSharingKey =
        "k8s.stevefan1999.tech/cilium-load-balancer-sharing-key";

    public const string CiliumLoadBalancerSharingAcrossNamespaceKey =
        "k8s.stevefan1999.tech/cilium-load-balancer-sharing-cross-namespace";

    public const string CiliumLoadBalancerIpsKey = "k8s.stevefan1999.tech/cilium-load-balancer-ips";

    public static string GetCiliumLoadBalancerServiceGeneratedName(this GameServer gameServer) =>
        $"agones-generated-{gameServer.Name()}";
}
