using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace IntegrationPanel.Api.Ai;

public enum AiProvider
{
    Off,
    Simulator,
    OpenAiCompatible
}

/// <summary>Configurações do assistente (seção "Ai" do appsettings).</summary>
public sealed class AiOptions
{
    public const string Section = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.Simulator;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Nunca no appsettings: use user-secrets ou a variável de ambiente Ai__ApiKey.</summary>
    public string? ApiKey { get; set; }

    public decimal InputPricePerMillion { get; set; } = 0.15m;
    public decimal OutputPricePerMillion { get; set; } = 0.60m;
    public int DailyCallLimit { get; set; } = 100;
    public decimal DailyCostLimitUsd { get; set; } = 1.00m;

    /// <summary>Quantas idas e voltas (modelo, ferramenta, modelo...) uma pergunta pode ter.</summary>
    public int MaxToolRounds { get; set; } = 4;
}

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Mensagem da conversa: role = system | user | assistant | tool.</summary>
public sealed record ChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>Uma ferramenta que o modelo pode pedir para executar. Parameters é um JSON Schema.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject Parameters);

public sealed record ChatResponse(string? Content, IReadOnlyList<ToolCall> ToolCalls, int InputTokens, int OutputTokens);

/// <summary>
/// Modelo de linguagem com suporte a ferramentas (function calling).
/// A aplicação só conhece esta interface; trocar de provedor é configuração.
/// </summary>
public interface IChatModel
{
    string Name { get; }

    Task<ChatResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
}

/// <summary>Cliente para APIs compatíveis com /v1/chat/completions (OpenAI, OpenRouter, Ollama...).</summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, IOptions<AiOptions> options) : IChatModel
{
    public string Name => options.Value.Model;

    public async Task<ChatResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var opt = options.Value;

        var body = new JsonObject
        {
            ["model"] = opt.Model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray<JsonNode?>()),
        };

        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone()
                }
            }).ToArray<JsonNode?>());
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                calls.Add(new ToolCall(
                    call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    fn.GetProperty("name").GetString() ?? string.Empty,
                    fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}"));
            }
        }

        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;

        return new ChatResponse(
            content,
            calls,
            usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("prompt_tokens").GetInt32() : 0,
            usage.ValueKind == JsonValueKind.Object ? usage.GetProperty("completion_tokens").GetInt32() : 0);
    }

    private static JsonNode ToJson(ChatMessage m)
    {
        var node = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };

        if (m.ToolCalls is { Count: > 0 })
        {
            node["tool_calls"] = new JsonArray(m.ToolCalls.Select(tc => (JsonNode)new JsonObject
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.ArgumentsJson }
            }).ToArray<JsonNode?>());
        }

        if (m.ToolCallId is not null)
        {
            node["tool_call_id"] = m.ToolCallId;
        }

        return node;
    }
}

/// <summary>
/// Modelo simulado e determinístico, com o mesmo contrato de ferramentas de um modelo real:
/// na pergunta, escolhe uma ferramenta por palavra-chave; com o resultado da ferramenta,
/// escreve a resposta. Permite rodar o painel, os testes e o CI sem chave de API.
/// </summary>
public sealed class SimulatedChatModel : IChatModel
{
    public string Name => "simulator";

    public Task<ChatResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        var last = messages[^1];
        var tokensIn = messages.Sum(m => (m.Content?.Length ?? 0) / 4);

        if (last.Role == "user")
        {
            var question = (last.Content ?? string.Empty).ToLowerInvariant();
            var (tool, args) = question switch
            {
                _ when question.Contains("de-para") || question.Contains("depara") || question.Contains("status")
                    => (AssistantTools.TopUnmappedStatuses, """{"limit": 5}"""),
                _ when question.Contains("recente") || question.Contains("últimos erros") || question.Contains("ultimos erros") || question.Contains("lista")
                    => (AssistantTools.RecentErrors, $$"""{"days": {{DaysFrom(question)}}, "limit": 5}"""),
                _ => (AssistantTools.ErrorSummary, $$"""{"days": {{DaysFrom(question)}}}""")
            };

            if (tools.Any(t => t.Name == tool))
            {
                return Task.FromResult(new ChatResponse(null, [new ToolCall("call_1", tool, args)], tokensIn, 20));
            }
        }

        if (last.Role == "tool")
        {
            var toolName = messages
                .SelectMany(m => m.ToolCalls ?? [])
                .FirstOrDefault(tc => tc.Id == last.ToolCallId)?.Name;

            var answer = Describe(toolName, last.Content ?? "{}");
            return Task.FromResult(new ChatResponse(answer, [], tokensIn, answer.Length / 4));
        }

        return Task.FromResult(new ChatResponse("Não entendi a pergunta. Tente perguntar sobre erros, status sem de-para ou os últimos erros.", [], tokensIn, 20));
    }

    private static int DaysFrom(string question) =>
        question.Contains("hoje") ? 1
        : question.Contains("ontem") ? 2
        : question.Contains("mês") || question.Contains("mes") ? 30
        : 7;

    private static string Describe(string? toolName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            return $"Não consegui consultar: {error.GetString()}";
        }

        switch (toolName)
        {
            case AssistantTools.TopUnmappedStatuses:
            {
                var items = root.GetProperty("statuses").EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    return "Nenhum pedido está parado por falta de de-para.";
                }

                var lines = items.Select(i =>
                    $"- {i.GetProperty("erpStatus").GetString()}: {i.GetProperty("stuckOrders").GetInt32()} pedido(s) parado(s)" +
                    (i.GetProperty("hasPendingProposal").GetBoolean() ? ", já com proposta aguardando aprovação" : ", sem proposta ainda"));
                return "Estes status do ERP estão sem de-para:\n" + string.Join('\n', lines) +
                       "\nAprovar uma proposta devolve esses pedidos para a fila.";
            }

            case AssistantTools.RecentErrors:
            {
                var items = root.GetProperty("errors").EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    return "Não há erros no período.";
                }

                var lines = items.Select(i =>
                    $"- {i.GetProperty("externalId").GetString()}: {i.GetProperty("message").GetString()} ({i.GetProperty("attempts").GetInt32()} tentativa(s))");
                return "Os erros mais recentes:\n" + string.Join('\n', lines);
            }

            default:
            {
                var days = root.GetProperty("days").GetInt32();
                var total = root.GetProperty("total").GetInt32();
                var errors = root.GetProperty("errors").GetInt32();
                var byCode = root.GetProperty("byCode").EnumerateArray()
                    .Select(c => $"{c.GetProperty("code").GetString()} ({c.GetProperty("count").GetInt32()})")
                    .ToList();

                var rate = total == 0 ? 0 : errors * 100.0 / total;
                return $"Nos últimos {days} dia(s): {total} itens processados, {errors} com erro ({rate:0.#}%)." +
                       (byCode.Count > 0 ? $" Erros por tipo: {string.Join(", ", byCode)}." : string.Empty);
            }
        }
    }
}
