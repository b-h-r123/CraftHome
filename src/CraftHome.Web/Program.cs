using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CraftHome.Web.Components;
using CraftHome.Web.Components.Account;
using CraftHome.Web.Data;
using CraftHome.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var paths = new RuntimePaths(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(paths);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.DataDirectory, "keys")));
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024
});
builder.Services.AddSingleton<McsManagerClient>();
builder.Services.AddSingleton<ServerService>();
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    { DataSource = Path.Combine(paths.DataDirectory, "crafthome.db") }.ToString();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();
await OwnerBootstrap.InitializeAsync(app.Services);
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    string[] supportedAccountPaths = ["/Account/Login", "/Account/Logout", "/Account/AccessDenied", "/Account/Manage/ChangePassword"];
    if (context.Request.Path.StartsWithSegments("/Account")
        && !supportedAccountPaths.Contains(context.Request.Path.Value?.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (builder.Configuration.GetValue<bool>("CraftHome:HttpsRedirect")) app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = "0.1.0" }));

app.Run();

public partial class Program;
