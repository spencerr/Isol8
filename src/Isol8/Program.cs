﻿using k8s;
using Isol8;
using Serilog;

var logger = new LoggerConfiguration()
    .WriteTo.Console()
    .Enrich.FromLogContext()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<EnvoyOptions>(builder.Configuration.GetSection(EnvoyOptions.Key));

builder.Logging.AddSerilog(logger);

builder.Services.AddHostedService<PullRequestController>();
builder.Services.AddSingleton<EnvoyYaml>();
builder.Services.AddSingleton<ServiceCache>();
builder.Services.AddSingleton<IKubernetes, Kubernetes>(_ =>
{
    var config = KubernetesClientConfiguration.BuildDefaultConfig();
    var client = new Kubernetes(config);

    return client;
});

var host = builder.Build();
await host.RunAsync();