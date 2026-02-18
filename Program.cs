var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SteamService>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 20 * 1024 * 1024; // 20 MB
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(30);  // era 10 — aumentado para operações longas
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);  // era 15 — mais agressivo para detectar quedas
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);  // novo: evita timeout no handshake inicial
    options.EnableDetailedErrors = true;                      // mostra erros detalhados no cliente (desative em produção)
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

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