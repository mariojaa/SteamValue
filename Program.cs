using SteamValue.Configuration;
using SteamValue.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Steam API settings
builder.Services.Configure<SteamApiConfig>(
    builder.Configuration.GetSection(SteamApiConfig.ConfigSection));

builder.Services.AddControllersWithViews();

// Register HTTP clients
builder.Services.AddHttpClient<SteamService>();
builder.Services.AddHttpClient<SteamHttpClient>();

builder.Services.AddMemoryCache();

// Register services
builder.Services.AddSingleton<SteamHttpClient>();
builder.Services.AddSingleton<SteamWebApiService>();
builder.Services.AddSingleton<SteamService>();

// Configure SignalR with settings from config
var steamConfig = builder.Configuration.GetSection(SteamApiConfig.ConfigSection).Get<SteamApiConfig>() ?? new SteamApiConfig();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 20 * 1024 * 1024; // 20 MB
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(steamConfig.Timeouts.SignalRClientTimeoutSeconds);
    options.KeepAliveInterval = TimeSpan.FromSeconds(steamConfig.Timeouts.SignalRKeepAliveSeconds);
    options.HandshakeTimeout = TimeSpan.FromSeconds(steamConfig.Timeouts.SignalRHandshakeTimeoutSeconds);
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<SteamValue.Services.CalculationHub>("/calculationHub");

app.Run();