var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient().AddHeaderPropagation();
builder.Services.AddHeaderPropagation(options =>
{
    options.Headers.Add("kubernetes-route-as");
});

var app = builder.Build();

app.UseHeaderPropagation();

var serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME");

var pathBase = Environment.GetEnvironmentVariable("PATH_BASE");
app.UsePathBase($"/{pathBase}");

app.MapGet("/info", (HttpRequest request) =>
{
    
    return Results.Ok(new ServiceResponse(

        ServiceName: serviceName ?? "SERVICE_NAME not set",
        RouteHeader: request.Headers["kubernetes-route-as"]
    ));
});

app.MapGet("/{downstreamService}", async (string downstreamService, HttpClient httpClient) =>
{
    var request = new HttpRequestMessage(HttpMethod.Get, $"http://{downstreamService}/{downstreamService}/info");
    var response = await httpClient.SendAsync(request);
    var serviceResponse = await response.Content.ReadFromJsonAsync<ServiceResponse>();

    return Results.Ok(serviceResponse);
});

app.Run();

record ServiceResponse(string ServiceName, string? RouteHeader);