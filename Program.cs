using AgonesLoadBalancerWatcher;
using k8s.Models;
using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Trace);

builder
    .Services.AddTransient<GameServerCiliumEgressGatewayPolicyFinalizer>()
    .AddTransient<CiliumEgressGatewayPolicyFinalizer>()
    .AddKubernetesOperator()
    .AddController<
        GameServerCreateV1ServiceController,
        GameServer,
        GameServerCreateV1ServiceController.LabelSelector
    >()
    .AddController<
        GameServerEgressGatewayFinalizerController,
        GameServer,
        GameServerEgressGatewayFinalizerController.LabelSelector
    >()
    .AddController<
        V1ServiceCreateCiliumEgressGatewayController,
        V1Service,
        V1ServiceCreateCiliumEgressGatewayController.LabelSelector
    >()
    .AddController<
        V1ServiceLoadBalancerGameServerUpdateController,
        V1Service,
        V1ServiceLoadBalancerGameServerUpdateController.LabelSelector
    >()
    .AddController<
        CiliumEgressGatewayPolicyController,
        CiliumEgressGatewayPolicy,
        CiliumEgressGatewayPolicyController.LabelSelector
    >()
    .AddFinalizer<GameServerCiliumEgressGatewayPolicyFinalizer, GameServer>(
        GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey
    )
    .AddFinalizer<CiliumEgressGatewayPolicyFinalizer, CiliumEgressGatewayPolicy>(
        GameServerEgressGatewayPolicyExtension.CiliumEgressGatewayPolicyFinalizerKey
    )
    .RegisterComponents();

using var host = builder.Build();
await host.RunAsync();
