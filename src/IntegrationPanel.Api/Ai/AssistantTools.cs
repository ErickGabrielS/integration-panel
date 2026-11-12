using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationPanel.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace IntegrationPanel.Api.Ai;

/// <summary>
/// Ferramentas que o modelo pode pedir para executar. São todas SOMENTE LEITURA:
/// o assistente consulta, mas reprocessar e aprovar continuam sendo ações de uma pessoa.
/// Os argumentos vêm do modelo, então são validados e limitados aqui, nunca usados como estão.
/// </summary>
public sealed class AssistantTools(PanelDbContext db, TimeProvider clock)
{
    public const string ErrorSummary = "get_error_summary";
    public const string TopUnmappedStatuses = "get_top_unmapped_statuses";
    public const string RecentErrors = "get_recent_errors";

    // Acentos legíveis no JSON: o modelo lê "não existe", e não "não existe".
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new(ErrorSummary,
            "Resumo dos resultados de integração nos últimos N dias: total, erros e erros por tipo.",
            Schema(("days", "integer", "Quantidade de dias, de 1 a 30."))),
        new(TopUnmappedStatuses,
            "Status do ERP sem de-para cadastrado, com quantos pedidos estão parados e se já existe proposta.",
            Schema(("limit", "integer", "Quantos status listar, de 1 a 10."))),
        new(RecentErrors,
            "Erros mais recentes, opcionalmente filtrados por código de erro.",
            Schema(("days", "integer", "Quantidade de dias, de 1 a 30."),
                   ("code", "string", "Código do erro (ex.: unmapped_status). Opcional."),
                   ("limit", "integer", "Quantos erros listar, de 1 a 10.")))
    ];

    /// <summary>Executa a ferramenta e devolve o resultado em JSON. Nome desconhecido vira erro, não exceção.</summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return Error("Argumentos inválidos.");
        }

        return name switch
        {
            ErrorSummary => await ErrorSummaryAsync(Int(args, "days", 7, 1, 30), ct),
            TopUnmappedStatuses => await TopUnmappedAsync(Int(args, "limit", 5, 1, 10), ct),
            RecentErrors => await RecentErrorsAsync(Int(args, "days", 7, 1, 30), Str(args, "code"), Int(args, "limit", 5, 1, 10), ct),
            _ => Error($"Ferramenta '{name}' não existe.")
        };
    }

    private async Task<string> ErrorSummaryAsync(int days, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-days);
        var logs = db.IntegrationLogs.AsNoTracking().Where(l => l.UpdatedAtUtc >= since);

        var total = await logs.CountAsync(ct);
        var errors = await logs.CountAsync(l => l.Status != IntegrationStatus.Success, ct);
        var byCode = await logs
            .Where(l => l.Status != IntegrationStatus.Success && l.ErrorCode != null)
            .GroupBy(l => l.ErrorCode!)
            .Select(g => new { code = g.Key, count = g.Count() })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { days, total, errors, byCode = byCode.OrderByDescending(c => c.count) }, Json);
    }

    private async Task<string> TopUnmappedAsync(int limit, CancellationToken ct)
    {
        var stuck = await db.IntegrationLogs.AsNoTracking()
            .Where(l => l.ErrorCode == ErrorCodes.UnmappedStatus && l.Status != IntegrationStatus.Success && l.ErrorDetail != null)
            .GroupBy(l => l.ErrorDetail!)
            .Select(g => new { erpStatus = g.Key, stuckOrders = g.Count() })
            .ToListAsync(ct);

        var pending = (await db.StatusMappingProposals.AsNoTracking()
                .Where(p => p.State == ProposalState.Pending)
                .Select(p => p.ErpStatus)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var statuses = stuck
            .OrderByDescending(s => s.stuckOrders)
            .Take(limit)
            .Select(s => new { s.erpStatus, s.stuckOrders, hasPendingProposal = pending.Contains(s.erpStatus) });

        return JsonSerializer.Serialize(new { statuses }, Json);
    }

    private async Task<string> RecentErrorsAsync(int days, string? code, int limit, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-days);
        var query = db.IntegrationLogs.AsNoTracking()
            .Where(l => l.UpdatedAtUtc >= since && l.Status != IntegrationStatus.Success);

        if (!string.IsNullOrWhiteSpace(code))
        {
            var safeCode = code.Length <= 50 ? code : code[..50];
            query = query.Where(l => l.ErrorCode == safeCode);
        }

        var errors = await query
            .OrderByDescending(l => l.UpdatedAtUtc)
            .Take(limit)
            .Select(l => new { l.ExternalId, l.ErrorCode, l.Message, l.Attempts, status = l.Status.ToString(), l.UpdatedAtUtc })
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { days, errors }, Json);
    }

    private static int Int(JsonElement args, string name, int fallback, int min, int max) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? Math.Clamp(i, min, max)
            : fallback;

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    private static JsonObject Schema(params (string Name, string Type, string Description)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in props)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties };
    }
}
