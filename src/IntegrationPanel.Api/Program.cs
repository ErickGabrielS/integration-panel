using IntegrationPanel.Api.Ai;
using IntegrationPanel.Api.Auth;
using IntegrationPanel.Api.Data;
using IntegrationPanel.Api.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

// Banco: SQLite para rodar sem instalar nada, SQL Server para produção. Esquema via migrations.
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("Panel") ?? "Data Source=panel.db";
builder.Services.AddDbContext<PanelDbContext>(options =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

// Autenticação JWT com dois perfis: Viewer (consulta) e Approver (também reprocessa e aprova).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        // Lido na hora de configurar (e não no início), para que testes e ambientes possam trocar a chave.
        var jwt = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = AuthEndpoints.SigningKey(jwt),
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

// IA generativa: assistente de diagnóstico com ferramentas somente leitura.
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
var ai = builder.Configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
if (ai.Provider == AiProvider.Simulator)
{
    builder.Services.AddSingleton<IChatModel, SimulatedChatModel>();
}
else if (ai.Provider == AiProvider.OpenAiCompatible)
{
    builder.Services
        .AddHttpClient<IChatModel, OpenAiCompatibleChatModel>(client => client.BaseAddress = new Uri(ai.BaseUrl))
        .AddStandardResilienceHandler();
}

if (ai.Provider != AiProvider.Off)
{
    builder.Services.AddScoped<AssistantTools>();
    builder.Services.AddScoped<AssistantService>();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }

    if (app.Configuration.GetValue("Demo:Seed", false))
    {
        var users = app.Configuration.GetSection("Demo:Users").Get<List<DemoUser>>() ?? [];
        await DemoSeeder.SeedAsync(db, users, TimeProvider.System);
    }
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles(); // a tela React compilada fica em wwwroot
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapOperationsEndpoints();
app.MapAssistantEndpoints();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
