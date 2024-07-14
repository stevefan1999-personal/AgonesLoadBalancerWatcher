using k8s.Models;

public static class V1ServiceLabelExtension
{
    const string CiliumLoadBalancerManagedKey = "k8s.stevefan1999.tech/cilium-load-balancer-managed";
    public static bool IsCiliumLoadBalancerManaged(this V1Service entity) => entity.Labels() switch
    {
        null => false,
        var labels => labels.TryGetValue(CiliumLoadBalancerManagedKey, out var enabledStr) && bool.TryParse(enabledStr, out var enabled) && enabled
    };
}
