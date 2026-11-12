using System.Net.Http.Headers;
using System.Net.Http.Json;
using IntegrationPanel.Api.Ai;
using IntegrationPanel.Api.Auth;
using IntegrationPanel.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntegrationPanel.Api.Tests;

/// <summary>
/// Sobe a API de verdade (rotas, JWT, perfis, migrations) sobre um SQLite em memória
/// com os dados de demonstração. Opcionalmente troca o modelo de IA por um roteirizado.
/// </summary>
public sealed class PanelFactory(IChatModel? model = null, int dailyCallLimit = 100) : WebApplicationFactory<Program>
{
    private readonly string _db = $"Data Source=panel-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _keepAlive;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Mantém o banco em memória vivo enquanto a fábrica existir.
        _keepAlive = new SqliteConnection(_db);
        _keepAlive.Open();

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Panel", _db);
        builder.UseSetting("Jwt:Key", "chave-de-teste-com-mais-de-32-caracteres!!");
        builder.UseSetting("Ai:Provider", "Simulator");
        builder.UseSetting("Ai:DailyCallLimit", dailyCallLimit.ToString());
        builder.UseSetting("Demo:Seed", "true");
        builder.UseSetting("Demo:Users:0:UserName", "leitor");
        builder.UseSetting("Demo:Users:0:DisplayName", "Leitor");
        builder.UseSetting("Demo:Users:0:Role", Roles.Viewer);
        builder.UseSetting("Demo:Users:0:Password", "senha-leitor");
        builder.UseSetting("Demo:Users:1:UserName", "aprovador");
        builder.UseSetting("Demo:Users:1:DisplayName", "Aprovador");
        builder.UseSetting("Demo:Users:1:Role", Roles.Approver);
        builder.UseSetting("Demo:Users:1:Password", "senha-aprovador");

        if (model is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatModel>();
                services.AddSingleton(model);
            });
        }
    }

    public async Task<HttpClient> LoginAsAsync(string user)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(user, user == "leitor" ? "senha-leitor" : "senha-aprovador"));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<PanelDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PanelDbContext>();
        return await query(db);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _keepAlive?.Dispose();
    }
}

/// <summary>Modelo de teste: devolve respostas prontas, na ordem, e guarda as conversas recebidas.</summary>
public sealed class ScriptedChatModel(params ChatResponse[] responses) : IChatModel
{
    private int _next;

    public List<IReadOnlyList<ChatMessage>> Conversations { get; } = [];

    public string Name => "scripted";

    public Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        Conversations.Add(messages.ToList());
        return Task.FromResult(responses[Math.Min(_next++, responses.Length - 1)]);
    }

    public static ChatResponse CallTool(string name, string args = "{}") => new(null, [new ToolCall($"call_{name}", name, args)], 1000, 50);

    public static ChatResponse Answer(string text) => new(text, [], 1000, 100);
}
