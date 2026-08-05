var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "sample-api",
    message = "Exported by AppHost A"
}));
app.MapHealthChecks("/health");

app.Run();
