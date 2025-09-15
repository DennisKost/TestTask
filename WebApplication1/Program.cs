var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var config = builder.Configuration;
var brokerSection = config.GetSection("Broker");
var directory = brokerSection.GetValue<string>("DirectoryPath") ?? "/tmp/broker";
var mode = brokerSection.GetValue<string>("Mode") ?? "primitive";
var clientTimeoutSeconds = brokerSection.GetValue<int?>("ClientTimeoutSeconds") ?? 60;
var coalescedTtlSeconds = brokerSection.GetValue<int?>("CoalescedTtlSeconds") ?? 120;
var brokerOptions = new WebApplication1.Options.BrokerOptions(directory, mode, clientTimeoutSeconds, coalescedTtlSeconds);
builder.Services.AddSingleton(brokerOptions);

builder.Services.AddSingleton<WebApplication1.Broker.IIdentityKeyProvider, WebApplication1.Broker.IdentityKeyProviders.PathAndMethodKeyProvider>();
builder.Services.AddSingleton<WebApplication1.Broker.FileBrokerClient>(sp => new WebApplication1.Broker.FileBrokerClient(brokerOptions, sp.GetRequiredService<ILogger<WebApplication1.Broker.FileBrokerClient>>()).Start());
builder.Services.AddSingleton<WebApplication1.Broker.IBrokerClient>(sp =>
{
    var baseClient = sp.GetRequiredService<WebApplication1.Broker.FileBrokerClient>();
    if(string.Equals(brokerOptions.Mode, "advanced", StringComparison.OrdinalIgnoreCase))
    {
        return new WebApplication1.Broker.CoalescingBrokerClient(baseClient, sp.GetRequiredService<WebApplication1.Broker.IIdentityKeyProvider>(), brokerOptions, sp.GetRequiredService<ILogger<WebApplication1.Broker.CoalescingBrokerClient>>() );
    }
    return baseClient;
});

var app = builder.Build();

if(app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/_health", () => Results.Ok("ok"));

WebApplication1.Endpoints.ProxyEndpoints.MapBrokerProxy(app);

app.Run();
