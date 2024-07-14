using System.Text.Json.Serialization;
using k8s.Models;
using KubeOps.Abstractions.Entities;

[KubernetesEntity(Group = "agones.dev", ApiVersion = "v1", Kind = "GameServer")]
public class GameServer :
    CustomKubernetesEntity<GameServer.GameServerSpec, GameServer.GameServerStatus>
{
    public override string ToString() => $"GameServer {{ Spec = {Spec}, Status = {Status}, Metadata = {{ Name = {Metadata.Name}, Namespace = {Metadata.NamespaceProperty}, Uid = {Metadata.Uid} }} }}";

    public record GameServerSpec
    {
        public override string ToString() => $"Ports = {string.Join(", ", Ports)}";
        public string? Container { get; set; } = string.Empty;
        public IList<GameServerPort>? Ports { get; set; } = [];
        public Health? Health { get; set; } = new();
        public string? Scheduling { get; set; } = string.Empty;
        public SdkServer? SdkServer { get; set; } = new();
        public V1PodTemplateSpec? Template { get; set; } = new();
        public PlayersSpec? Players { get; set; }
        public IDictionary<string, string>? Counters { get; set; }
        public IDictionary<string, string>? Lists { get; set; }
        public Eviction? Eviction { get; set; }
    }


    public record PlayersSpec
    {
        public long? InitialCapacity { get; set; }
    }
    public record Eviction
    {
        public string? Safe { get; set; } = string.Empty;
    }
    public record Health
    {
        public bool? Disabled { get; set; }
        public int? PeriodSeconds { get; set; }
        public int? FailureThreshold { get; set; }
        public int? InitialDelaySeconds { get; set; }
    }
    public record GameServerPort
    {
        public string? Container { get; set; } = string.Empty;
        public int ContainerPort { get; set; } = 0;
        public int HostPort { get; set; } = 0;
        public string? Name { get; set; } = string.Empty;
        public string? PortPolicy { get; set; } = string.Empty;
        public string? Protocol { get; set; } = string.Empty;
        public string? Range { get; set; } = string.Empty;
    }

    public record SdkServer
    {
        public string? LogLevel { get; set; } = string.Empty;
        [JsonPropertyName("grpcPort")]
        public int? GRPCPort { get; set; } = 0;
        [JsonPropertyName("httpPort")]
        public int? HTTPPort { get; set; } = 0;
    }

    public record GameServerStatus
    {
        public string? State { get; set; } = string.Empty;
        public IList<GameServerStatusPort>? Ports { get; set; } = [];
        public string? Address { get; set; } = string.Empty;
        public IList<V1NodeAddress>? Addresses { get; set; } = [];
        public string? NodeName { get; set; } = string.Empty;
        public DateTime? ReservedUntil { get; set; }
        public PlayerStatus? Players { get; set; }
        public IDictionary<string, CounterStatus>? Counters { get; set; }
        public IDictionary<string, ListStatus>? Lists { get; set; }
        public Eviction? Eviction { get; set; }
    }

    public record GameServerStatusPort
    {
        public string? Name { get; set; } = string.Empty;
        public int? Port { get; set; }
    }
    public record PlayerStatus
    {
        public long? Count { get; set; }
        public long? Capacity { get; set; }
        [JsonPropertyName("ids")]
        public IList<string>? IDs { get; set; } = [];
    }

    public record CounterStatus
    {
        public long? Count { get; set; }
        public long? Capacity { get; set; }
    }
    public record ListStatus
    {
        public long? Capacity { get; set; }
        public IList<string>? Values { get; set; } = [];
    }
}
