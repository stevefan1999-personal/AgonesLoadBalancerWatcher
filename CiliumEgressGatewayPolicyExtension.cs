using k8s.Models;

public static class CiliumEgressGatewayPolicyExtension
{
    public static bool AttachCiliumEgressGatewayPolicyFinalizer(
        this CiliumEgressGatewayPolicy policy
    ) =>
        policy.AddFinalizer(
            GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey
        );

    public const string IsManagedKey = "k8s.stevefan1999.tech/is-managed";

    public static bool IsManagedFromAnnotation(this CiliumEgressGatewayPolicy policy) =>
        policy.Annotations().TryGetValue(IsManagedKey, out var valueStr)
        && bool.TryParse(valueStr, out var enabled)
        && enabled;

    public const string GameServerReferenceKey = "k8s.stevefan1999.tech/game-server-ref";

    public static (string Namespace, string Name)? GetGameServerReferenceFromAnnotation(
        this CiliumEgressGatewayPolicy policy
    )
    {
        if (policy.Annotations().TryGetValue(GameServerReferenceKey, out var valueStr))
        {
            var parts = valueStr.Split('/');
            if (parts.Length != 2)
            {
                return null;
            }
            return (parts[0], parts[1]);
        }
        else
        {
            return null;
        }
    }

    public const string ServiceReferenceKey = "k8s.stevefan1999.tech/service-ref";

    public static (string Namespace, string Name)? GetServiceReferenceFromAnnotation(
        this CiliumEgressGatewayPolicy policy
    )
    {
        if (policy.Annotations().TryGetValue(ServiceReferenceKey, out var valueStr))
        {
            var parts = valueStr.Split('/');
            if (parts.Length != 2)
            {
                return null;
            }
            return (parts[0], parts[1]);
        }
        else
        {
            return null;
        }
    }
}
