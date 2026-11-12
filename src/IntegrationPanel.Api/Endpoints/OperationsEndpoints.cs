using System.Security.Claims;
using IntegrationPanel.Api.Auth;
using IntegrationPanel.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace IntegrationPanel.Api.Endpoints;

public sealed record DailyCount(DateOnly Day, int Success, int Errors);
public sealed record CodeCount(string Code, int Count);
public sealed record DashboardSummary(
    int Total, int Success, int Errors, int GaveUp, int PendingProposals,
    IReadOnlyList<CodeCount> ErrorsByCode, IReadOnlyList<DailyCount> Last7Days);

public sealed record ErrorItem(
    long Id, string ExternalId, string Status, int Attempts, string? ErrorCode,
    string? ErrorDetail, string? Message, DateTime UpdatedAtUtc);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize);

public sealed record ProposalItem(
    int Id, string ErpStatus, string SuggestedStoreStatus, string Reason, string Source,
    int AffectedOrders, string State, string? DecisionNote, string? DecidedBy, DateTime CreatedAtUtc);

public sealed record ApproveRequest(string? StoreStatus, string? Note);
public sealed record RejectRequest(string Note);
public sealed record DecisionResponse(string Message, int ReopenedOrders);

public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api").RequireAuthorization();
        read.MapGet("/dashboard", GetDashboardAsync);
        read.MapGet("/errors", ListErrorsAsync);
        read.MapGet("/proposals", ListProposalsAsync);
        read.MapGet("/store-statuses", async (PanelDbContext db, CancellationToken ct) =>
            await db.StatusMappings.Select(m => m.StoreStatus).Distinct().OrderBy(s => s).ToListAsync(ct));

        var write = app.MapGroup("/api").RequireAuthorization(p => p.RequireRole(Roles.Approver));
        write.MapPost("/errors/{id:long}/reprocess", ReprocessAsync);
        write.MapPost("/proposals/{id:int}/approve", ApproveAsync);
        write.MapPost("/proposals/{id:int}/reject", RejectAsync);
        write.MapGet("/audit", async (PanelDbContext db, CancellationToken ct) =>
            await db.AuditEntries.AsNoTracking().OrderByDescending(a => a.AtUtc).Take(100).ToListAsync(ct));
    }

    internal static async Task<DashboardSummary> GetDashboardAsync(PanelDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var byStatus = await db.IntegrationLogs
            .GroupBy(l => l.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(IntegrationStatus s) => byStatus.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        var byCode = await db.IntegrationLogs
            .Where(l => l.Status != IntegrationStatus.Success && l.ErrorCode != null)
            .GroupBy(l => l.ErrorCode!)
            .Select(g => new CodeCount(g.Key, g.Count()))
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var since = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);

        // Agrupamento por dia feito na memória: só as duas colunas necessárias dos últimos 7 dias.
        var recent = await db.IntegrationLogs
            .Where(l => l.UpdatedAtUtc >= since)
            .Select(l => new { l.UpdatedAtUtc, l.Status })
            .ToListAsync(ct);

        var days = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(-6 + i))
            .Select(day => new DailyCount(
                day,
                recent.Count(r => DateOnly.FromDateTime(r.UpdatedAtUtc) == day && r.Status == IntegrationStatus.Success),
                recent.Count(r => DateOnly.FromDateTime(r.UpdatedAtUtc) == day && r.Status != IntegrationStatus.Success)))
            .ToList();

        var pending = await db.StatusMappingProposals.CountAsync(p => p.State == ProposalState.Pending, ct);

        return new DashboardSummary(
            byStatus.Sum(x => x.Count), Count(IntegrationStatus.Success), Count(IntegrationStatus.Error),
            Count(IntegrationStatus.GaveUp), pending,
            byCode.OrderByDescending(c => c.Count).ToList(), days);
    }

    internal static async Task<Page<ErrorItem>> ListErrorsAsync(
        PanelDbContext db, string? code, string? status, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.IntegrationLogs.AsNoTracking().Where(l => l.Status != IntegrationStatus.Success);

        if (!string.IsNullOrWhiteSpace(code))
        {
            query = query.Where(l => l.ErrorCode == code);
        }

        if (Enum.TryParse<IntegrationStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(l => l.Status == parsed);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ErrorItem(l.Id, l.ExternalId, l.Status.ToString(), l.Attempts, l.ErrorCode,
                l.ErrorDetail, l.Message, l.UpdatedAtUtc))
            .ToListAsync(ct);

        return new Page<ErrorItem>(items, total, page, pageSize);
    }

    /// <summary>
    /// Devolve o item para a fila de reprocesso do worker: status Error e tentativas zeradas.
    /// O painel não chama o ERP; quem reprocessa é o worker, no próximo ciclo.
    /// </summary>
    internal static async Task<IResult> ReprocessAsync(
        long id, PanelDbContext db, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        var log = await db.IntegrationLogs.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (log is null)
        {
            return Results.NotFound();
        }

        if (log.Status == IntegrationStatus.Success)
        {
            return Results.Problem("Este item já foi integrado com sucesso.", statusCode: StatusCodes.Status409Conflict);
        }

        var before = $"{log.Status}, {log.Attempts} tentativa(s)";
        log.Status = IntegrationStatus.Error;
        log.Attempts = 0;
        log.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        Audit(db, user, clock, "reprocess", log.ExternalId, $"Antes: {before}.");
        await db.SaveChangesAsync(ct);

        return Results.Ok(new DecisionResponse($"{log.ExternalId} voltou para a fila de reprocesso.", 1));
    }

    internal static async Task<IReadOnlyList<ProposalItem>> ListProposalsAsync(
        PanelDbContext db, string? state, CancellationToken ct)
    {
        var query = db.StatusMappingProposals.AsNoTracking();

        if (Enum.TryParse<ProposalState>(state, ignoreCase: true, out var parsed))
        {
            query = query.Where(p => p.State == parsed);
        }

        return await query
            .OrderBy(p => p.State).ThenByDescending(p => p.AffectedOrders)
            .Select(p => new ProposalItem(p.Id, p.ErpStatus, p.SuggestedStoreStatus, p.Reason, p.Source,
                p.AffectedOrders, p.State.ToString(), p.DecisionNote, p.DecidedBy, p.CreatedAtUtc))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Aprovar cria (ou reativa) o de-para e devolve para a fila os pedidos parados por
    /// aquele status, inclusive os que já tinham desistido. Mesma regra do worker.
    /// </summary>
    internal static async Task<IResult> ApproveAsync(
        int id, ApproveRequest request, PanelDbContext db, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        var proposal = await db.StatusMappingProposals.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (proposal is null || proposal.State != ProposalState.Pending)
        {
            return Results.Problem($"A proposta {id} não existe ou já foi decidida.", statusCode: StatusCodes.Status409Conflict);
        }

        var storeStatus = string.IsNullOrWhiteSpace(request.StoreStatus) ? proposal.SuggestedStoreStatus : request.StoreStatus.Trim();
        var allowed = await db.StatusMappings.Select(m => m.StoreStatus).Distinct().ToListAsync(ct);
        if (!allowed.Contains(storeStatus))
        {
            return Results.Problem($"'{storeStatus}' não é um status da loja. Use: {string.Join(", ", allowed)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var mapping = await db.StatusMappings.SingleOrDefaultAsync(m => m.ErpStatus == proposal.ErpStatus, ct);
        if (mapping is null)
        {
            db.StatusMappings.Add(new StatusMapping { ErpStatus = proposal.ErpStatus, StoreStatus = storeStatus });
        }
        else
        {
            mapping.StoreStatus = storeStatus;
            mapping.Active = true;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        proposal.State = ProposalState.Approved;
        proposal.DecidedAtUtc = now;
        proposal.DecidedBy = user.UserName();
        proposal.DecisionNote = storeStatus == proposal.SuggestedStoreStatus
            ? request.Note
            : $"Aprovado com '{storeStatus}' no lugar de '{proposal.SuggestedStoreStatus}'. {request.Note}".Trim();

        var stuck = await db.IntegrationLogs
            .Where(l => l.ErrorCode == ErrorCodes.UnmappedStatus
                        && l.ErrorDetail == proposal.ErpStatus
                        && l.Status != IntegrationStatus.Success)
            .ToListAsync(ct);

        foreach (var log in stuck)
        {
            log.Status = IntegrationStatus.Error;
            log.Attempts = 0;
            log.UpdatedAtUtc = now;
        }

        Audit(db, user, clock, "approve_proposal", $"{proposal.ErpStatus} -> {storeStatus}",
            $"{stuck.Count} pedido(s) voltaram para a fila. {request.Note}".Trim());
        await db.SaveChangesAsync(ct);

        return Results.Ok(new DecisionResponse($"De-para {proposal.ErpStatus} -> {storeStatus} criado.", stuck.Count));
    }

    internal static async Task<IResult> RejectAsync(
        int id, RejectRequest request, PanelDbContext db, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            return Results.Problem("Informe o motivo da recusa.", statusCode: StatusCodes.Status400BadRequest);
        }

        var proposal = await db.StatusMappingProposals.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (proposal is null || proposal.State != ProposalState.Pending)
        {
            return Results.Problem($"A proposta {id} não existe ou já foi decidida.", statusCode: StatusCodes.Status409Conflict);
        }

        proposal.State = ProposalState.Rejected;
        proposal.DecidedAtUtc = clock.GetUtcNow().UtcDateTime;
        proposal.DecidedBy = user.UserName();
        proposal.DecisionNote = request.Note.Trim();
        Audit(db, user, clock, "reject_proposal", proposal.ErpStatus, request.Note.Trim());
        await db.SaveChangesAsync(ct);

        return Results.Ok(new DecisionResponse($"Proposta para {proposal.ErpStatus} recusada.", 0));
    }

    private static void Audit(PanelDbContext db, ClaimsPrincipal user, TimeProvider clock, string action, string target, string? details) =>
        db.AuditEntries.Add(new AuditEntry
        {
            UserName = user.UserName(),
            Action = action,
            Target = target.Length <= 100 ? target : target[..100],
            Details = details is { Length: > 500 } ? details[..500] : details,
            AtUtc = clock.GetUtcNow().UtcDateTime
        });
}
