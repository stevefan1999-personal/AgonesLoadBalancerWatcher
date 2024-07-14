using k8s.Models;
using KubeOps.Abstractions.Entities;
using KubeOps.Abstractions.Entities.Attributes;

[KubernetesEntity(Group = "cilium.io", ApiVersion = "v2", Kind = "CiliumEgressGatewayPolicy", PluralName = "ciliumegressgatewaypolicies")]
[EntityScope(EntityScope.Cluster)]
public class CiliumEgressGatewayPolicy :
    CustomKubernetesEntity<CiliumEgressGatewayPolicy.CiliumEgressGatewayPolicySpec>
{
    public override string ToString() => $"CiliumEgressGatewayPolicyEntity {{ Spec = {Spec}, Metadata = {{ Name = {Metadata.Name}, Namespace = {Metadata.NamespaceProperty}, Uid = {Metadata.Uid} }} }}";
    public record CiliumEgressGatewayPolicySpec
    {
        public IList<EgressRule> Selectors { get; set; } = [];

        public IList<string> DestinationCIDRs { get; set; } = [];

        public IList<string> ExcludedCIDRs { get; set; } = [];

        public EgressGateway EgressGateway { get; set; } = new EgressGateway();
    }

    public record EgressGateway
    {
        public string EgressIP { get; set; } = string.Empty;
        public V1LabelSelector NodeSelector { get; set; } = new V1LabelSelector();
        public string Interface { get; set; } = string.Empty;
    }

    public record EgressRule
    {
        public V1LabelSelector NamespaceSelector { get; set; } = new V1LabelSelector();
        public V1LabelSelector PodSelector { get; set; } = new V1LabelSelector();
    }
}
