namespace IntegrationPanel.Api.Data;

// As quatro primeiras entidades seguem o mesmo modelo do incremental-sync-worker
// (github.com/ErickGabrielS/incremental-sync-worker): o painel foi pensado para
// operar a base de uma integração desse tipo.

public enum IntegrationStatus
{
    Success = 1,
    Error = 2,
    GaveUp = 3
}

/// <summary>Uma linha por item integrado: último resultado, tentativas e erro.</summary>
public sealed class IntegrationLog
{
    public long Id { get; set; }
    public required string Entity { get; set; }
    public required string ExternalId { get; set; }
    public IntegrationStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Message { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public static class ErrorCodes
{
    public const string MissingId = "missing_id";
    public const string MissingCustomer = "missing_customer";
    public const string NegativeTotal = "negative_total";
    public const string UnmappedStatus = "unmapped_status";
    public const string SaveFailed = "save_failed";
    public const string NotFoundInErp = "not_found_in_erp";
}

/// <summary>De-para de status: ERP para loja.</summary>
public sealed class StatusMapping
{
    public required string ErpStatus { get; set; }
    public required string StoreStatus { get; set; }
    public bool Active { get; set; } = true;
}

public enum ProposalState
{
    Pending = 1,
    Approved = 2,
    Rejected = 3
}

/// <summary>Sugestão de de-para que só vale depois de aprovada por uma pessoa.</summary>
public sealed class StatusMappingProposal
{
    public int Id { get; set; }
    public required string ErpStatus { get; set; }
    public required string SuggestedStoreStatus { get; set; }
    public required string Reason { get; set; }
    public required string Source { get; set; }
    public int AffectedOrders { get; set; }
    public ProposalState State { get; set; } = ProposalState.Pending;
    public string? DecisionNote { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

/// <summary>Uma chamada a um modelo de IA: base do limite diário e do controle de custo.</summary>
public sealed class AiCall
{
    public long Id { get; set; }
    public required string Provider { get; set; }
    public required string Purpose { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public bool ValidAnswer { get; set; }
    public DateTime AtUtc { get; set; }
}

public static class Roles
{
    /// <summary>Só consulta: painel, erros, propostas e assistente.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Também reprocessa erros e decide propostas.</summary>
    public const string Approver = "Approver";
}

public sealed class User
{
    public int Id { get; set; }
    public required string UserName { get; set; }
    public required string DisplayName { get; set; }
    public required string Role { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
}

/// <summary>Quem fez o quê e quando. Toda ação que altera dados gera uma linha.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public required string UserName { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public string? Details { get; set; }
    public DateTime AtUtc { get; set; }
}
