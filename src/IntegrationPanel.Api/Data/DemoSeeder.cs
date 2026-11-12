using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IntegrationPanel.Api.Data;

public sealed class DemoUser
{
    public required string UserName { get; set; }
    public required string DisplayName { get; set; }
    public required string Role { get; set; }
    public required string Password { get; set; }
}

/// <summary>
/// Dados fictícios para rodar o painel sem nenhum sistema por trás:
/// usuários de demonstração e uma semana de resultados de integração.
/// Só roda quando a base está vazia (e quando "Demo:Seed" está ligado).
/// </summary>
public static class DemoSeeder
{
    private static readonly (string Code, string Message, string? Detail, int Weight)[] ErrorKinds =
    [
        (ErrorCodes.UnmappedStatus, "Status 'EM_ANALISE' sem de-para cadastrado.", "EM_ANALISE", 9),
        (ErrorCodes.UnmappedStatus, "Status 'AGUARDANDO_PAGAMENTO' sem de-para cadastrado.", "AGUARDANDO_PAGAMENTO", 5),
        (ErrorCodes.UnmappedStatus, "Status 'DEVOLVIDO' sem de-para cadastrado.", "DEVOLVIDO", 2),
        (ErrorCodes.MissingCustomer, "Pedido sem cliente.", null, 3),
        (ErrorCodes.NegativeTotal, "Total negativo (-10,00).", null, 1),
        (ErrorCodes.NotFoundInErp, "Pedido não encontrado no ERP.", null, 2),
        (ErrorCodes.SaveFailed, "Falha ao gravar: tempo de espera esgotado.", null, 2)
    ];

    public static async Task SeedAsync(
        PanelDbContext db, IReadOnlyList<DemoUser> users, TimeProvider clock, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(ct))
        {
            var hasher = new PasswordHasher<User>();
            foreach (var demo in users)
            {
                var user = new User { UserName = demo.UserName, DisplayName = demo.DisplayName, Role = demo.Role };
                user.PasswordHash = hasher.HashPassword(user, demo.Password);
                db.Users.Add(user);
            }
        }

        if (!await db.StatusMappings.AnyAsync(ct))
        {
            db.StatusMappings.AddRange(
                new StatusMapping { ErpStatus = "ABERTO", StoreStatus = "Pending" },
                new StatusMapping { ErpStatus = "FATURADO", StoreStatus = "Invoiced" },
                new StatusMapping { ErpStatus = "ENVIADO", StoreStatus = "Shipped" },
                new StatusMapping { ErpStatus = "CANCELADO", StoreStatus = "Cancelled" });
        }

        if (!await db.IntegrationLogs.AnyAsync(ct))
        {
            var random = new Random(42); // determinístico: a demonstração é sempre igual
            var now = clock.GetUtcNow().UtcDateTime;
            var weighted = ErrorKinds.SelectMany(k => Enumerable.Repeat(k, k.Weight)).ToArray();

            for (var i = 1; i <= 240; i++)
            {
                var at = now.AddHours(-random.Next(0, 7 * 24)).AddMinutes(-random.Next(0, 60));
                var log = new IntegrationLog
                {
                    Entity = "orders",
                    ExternalId = $"PED-{i:D5}",
                    CreatedAtUtc = at.AddMinutes(-5),
                    UpdatedAtUtc = at
                };

                if (random.NextDouble() < 0.8)
                {
                    log.Status = IntegrationStatus.Success;
                }
                else
                {
                    var kind = weighted[random.Next(weighted.Length)];
                    log.Attempts = random.Next(1, 6);
                    log.Status = log.Attempts >= 5 ? IntegrationStatus.GaveUp : IntegrationStatus.Error;
                    log.Message = kind.Message;
                    log.ErrorCode = kind.Code;
                    log.ErrorDetail = kind.Detail;
                }

                db.IntegrationLogs.Add(log);
            }

            db.StatusMappingProposals.AddRange(
                new StatusMappingProposal
                {
                    ErpStatus = "EM_ANALISE",
                    SuggestedStoreStatus = "Pending",
                    Reason = "Status intermediário, antes do faturamento: o pedido continua pendente na loja.",
                    Source = "ai:simulator",
                    AffectedOrders = 0,
                    CreatedAtUtc = now.AddHours(-3)
                },
                new StatusMappingProposal
                {
                    ErpStatus = "AGUARDANDO_PAGAMENTO",
                    SuggestedStoreStatus = "Pending",
                    Reason = "O pagamento ainda não foi confirmado, então o pedido segue pendente.",
                    Source = "ai:simulator",
                    AffectedOrders = 0,
                    CreatedAtUtc = now.AddHours(-1)
                });
        }

        await db.SaveChangesAsync(ct);

        // Atualiza a contagem de pedidos afetados nas propostas pendentes.
        var pending = await db.StatusMappingProposals.Where(p => p.State == ProposalState.Pending).ToListAsync(ct);
        foreach (var proposal in pending)
        {
            proposal.AffectedOrders = await db.IntegrationLogs.CountAsync(
                l => l.ErrorCode == ErrorCodes.UnmappedStatus
                     && l.ErrorDetail == proposal.ErpStatus
                     && l.Status != IntegrationStatus.Success, ct);
        }

        await db.SaveChangesAsync(ct);
    }
}
