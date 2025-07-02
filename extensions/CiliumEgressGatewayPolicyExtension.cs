using k8s.Models;

namespace AgonesLoadBalancerWatcher;

public static class CiliumEgressGatewayPolicyExtension
{
    public const string IsManagedKey = "k8s.stevefan1999.tech/is-managed";

    public const string GameServerReferenceNamespaceKey =
        "k8s.stevefan1999.tech/game-server-ref-namespace";

    public const string GameServerReferenceNameKey = "k8s.stevefan1999.tech/game-server-ref-name";

    public const string ServiceReferenceNamespaceKey =
        "k8s.stevefan1999.tech/service-ref-namespace";

    public const string ServiceReferenceNameKey = "k8s.stevefan1999.tech/service-ref-name";
}
