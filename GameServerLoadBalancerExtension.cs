using k8s.Models;

public static class GameServerLoadBalancerExtension
{
    public const string CiliumLoadBalancerEnabledKey = "k8s.stevefan1999.tech/cilium-load-balancer-enabled";
    public static bool IsCiliumLoadBalancerEnabledFromAnnotation(this GameServer gameServer) =>
        gameServer.Annotations().TryGetValue(CiliumLoadBalancerEnabledKey, out var valueStr) && bool.TryParse(valueStr, out var enabled) && enabled;

    public const string CiliumLoadBalancerSharingKey = "k8s.stevefan1999.tech/cilium-load-balancer-sharing-key";
    public static string? GetCiliumLoadBalancerSharingKeyFromAnnotation(this GameServer gameServer) =>
        gameServer.Annotations().GetValueOrDefault(CiliumLoadBalancerSharingKey);
    public const string CiliumLoadBalancerSharingAcrossNamespaceKey = "k8s.stevefan1999.tech/cilium-load-balancer-sharing-cross-namespace";
    public static string? GetCiliumLoadBalancerSharingAcrossNamespaceFromAnnotation(this GameServer gameServer) =>
        gameServer.Annotations().GetValueOrDefault(CiliumLoadBalancerSharingAcrossNamespaceKey);
    public const string CiliumLoadBalancerIpsKey = "k8s.stevefan1999.tech/cilium-load-balancer-ips";
    public static string? GetCiliumLoadBalancerIpsFromAnnotation(this GameServer gameServer) =>
        gameServer.Annotations().GetValueOrDefault(CiliumLoadBalancerIpsKey);
    public static string? GetCiliumLoadBalancerServiceGeneratedName(this GameServer gameServer) =>
        gameServer.IsCiliumLoadBalancerEnabledFromAnnotation() ? $"agones-generated-{gameServer.Name()}" : null;
}
