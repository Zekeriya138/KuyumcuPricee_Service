using KuyumcuVomsisWorker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHttpClient<VomsisApiClient>();
builder.Services.AddHttpClient<ErpImportClient>();
builder.Services.AddHttpClient<ErpWorkerConfigClient>();
builder.Services.AddHostedService<VomsisSyncWorker>();

var host = builder.Build();
host.Run();
