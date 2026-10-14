using NewtrendAi;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5088");

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddNewtrendAi(options =>
{
    options.ServiceName = "Sample.Api";
    options.Endpoint = new Uri("http://127.0.0.1:4318");
    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
});

var app = builder.Build();
app.UseCors();
app.MapPost("/providers", () => Results.Ok(new { id = 1, name = "Saved" }));
app.Run();
