using k8s.Models;

public static class GameServerEgressGatewayPolicyExtension
{
    public const string CiliumEgressGatewayPolicyEnabledKey = "k8s.stevefan1999.tech/cilium-egress-gateway-policy-enabled";

    public static bool IsCiliumEgressGatewayPolicyEnabledFromAnnotation(this GameServer gameServer) =>
        gameServer.Annotations().TryGetValue(CiliumEgressGatewayPolicyEnabledKey, out var valueStr) && bool.TryParse(valueStr, out var enabled) && enabled;
    public static string? GetCiliumEgressGatewayPolicyGeneratedName(this GameServer gameServer) =>
        gameServer.IsCiliumEgressGatewayPolicyEnabledFromAnnotation() ? $"agones-generated-{gameServer.Namespace()}-{gameServer.Name()}" : null;

    public const string CiliumEgressGatewayPolicyFinalizerKey = "k8s.stevefan1999.tech/cilium-egress-gateway-policy-finalizer";
    public static bool AttachCiliumEgressGatewayPolicyFinalizer(this GameServer gameServer) => gameServer.AddFinalizer(CiliumEgressGatewayPolicyFinalizerKey);
}
