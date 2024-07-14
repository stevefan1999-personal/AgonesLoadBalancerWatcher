using k8s.Models;
using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Trace);

builder.Services
    .AddTransient<GameServerCiliumEgressGatewayPolicyFinalizer>()
    .AddTransient<CiliumEgressGatewayPolicyFinalizer>()
    .AddKubernetesOperator()
    .AddController<V1ServiceController, V1Service>()
    .AddController<CiliumEgressGatewayPolicyController, CiliumEgressGatewayPolicy>()
    .AddFinalizer<GameServerCiliumEgressGatewayPolicyFinalizer, GameServer>(GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey)
    .AddFinalizer<CiliumEgressGatewayPolicyFinalizer, CiliumEgressGatewayPolicy>(GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey)
    .RegisterComponents()
    ;

using var host = builder.Build();
await host.RunAsync();
