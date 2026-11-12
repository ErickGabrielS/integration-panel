using System.Net;
using System.Net.Http.Json;
using IntegrationPanel.Api.Auth;
using IntegrationPanel.Api.Data;
using IntegrationPanel.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace IntegrationPanel.Api.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_401()
    {
        using var factory = new PanelFactory();
        var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("aprovador", "errada"));
        var unknownUser = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ninguem", "errada"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(await DetailOf(wrongPassword), await DetailOf(unknownUser));
    }

    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/errors")]
    [InlineData("/api/proposals")]
    public async Task Data_endpoints_require_a_token(string url)
    {
        using var factory = new PanelFactory();

        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("detail").GetString();

    [Fact]
    public async Task Viewer_reads_the_dashboard_but_cannot_change_anything()
    {
        using var factory = new PanelFactory();
        var viewer = await factory.LoginAsAsync("leitor");

        var dashboard = await viewer.GetFromJsonAsync<DashboardSummary>("/api/dashboard");
        var approve = await viewer.PostAsJsonAsync("/api/proposals/1/approve", new ApproveRequest(null, null));
        var reprocess = await viewer.PostAsync("/api/errors/1/reprocess", null);

        Assert.Equal(240, dashboard!.Total);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reprocess.StatusCode);
    }
}

public sealed class OperationsTests
{
    [Fact]
    public async Task Approving_creates_the_mapping_reopens_stuck_orders_and_is_audited()
    {
        using var factory = new PanelFactory();
        var approver = await factory.LoginAsAsync("aprovador");
        var proposal = (await approver.GetFromJsonAsync<List<ProposalItem>>("/api/proposals?state=Pending"))!
            .Single(p => p.ErpStatus == "EM_ANALISE");

        var response = await approver.PostAsJsonAsync($"/api/proposals/{proposal.Id}/approve", new ApproveRequest(null, "ok"));
        var decision = await response.Content.ReadFromJsonAsync<DecisionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(proposal.AffectedOrders, decision!.ReopenedOrders);
        Assert.True(await factory.WithDbAsync(db => db.StatusMappings.AnyAsync(m => m.ErpStatus == "EM_ANALISE" && m.StoreStatus == "Pending")));
        Assert.Equal(0, await factory.WithDbAsync(db => db.IntegrationLogs.CountAsync(
            l => l.ErrorDetail == "EM_ANALISE" && (l.Status == IntegrationStatus.GaveUp || l.Attempts > 0))));

        var audit = await factory.WithDbAsync(db => db.AuditEntries.SingleAsync());
        Assert.Equal("aprovador", audit.UserName);
        Assert.Equal("approve_proposal", audit.Action);
    }

    [Fact]
    public async Task Approving_with_a_status_that_does_not_exist_in_the_store_is_refused()
    {
        using var factory = new PanelFactory();
        var approver = await factory.LoginAsAsync("aprovador");

        var response = await approver.PostAsJsonAsync("/api/proposals/1/approve", new ApproveRequest("Inventado", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(4, await factory.WithDbAsync(db => db.StatusMappings.CountAsync()));
    }

    [Fact]
    public async Task Rejecting_requires_a_reason()
    {
        using var factory = new PanelFactory();
        var approver = await factory.LoginAsAsync("aprovador");

        var withoutReason = await approver.PostAsJsonAsync("/api/proposals/1/reject", new RejectRequest(" "));
        var withReason = await approver.PostAsJsonAsync("/api/proposals/1/reject", new RejectRequest("status de teste"));

        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withReason.StatusCode);
    }

    [Fact]
    public async Task Reprocess_puts_the_item_back_in_the_queue_and_refuses_items_that_already_succeeded()
    {
        using var factory = new PanelFactory();
        var approver = await factory.LoginAsAsync("aprovador");
        var gaveUp = await factory.WithDbAsync(db => db.IntegrationLogs.FirstAsync(l => l.Status == IntegrationStatus.GaveUp));
        var ok = await factory.WithDbAsync(db => db.IntegrationLogs.FirstAsync(l => l.Status == IntegrationStatus.Success));

        var reprocess = await approver.PostAsync($"/api/errors/{gaveUp.Id}/reprocess", null);
        var alreadyOk = await approver.PostAsync($"/api/errors/{ok.Id}/reprocess", null);

        Assert.Equal(HttpStatusCode.OK, reprocess.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, alreadyOk.StatusCode);
        var after = await factory.WithDbAsync(db => db.IntegrationLogs.SingleAsync(l => l.Id == gaveUp.Id));
        Assert.Equal(IntegrationStatus.Error, after.Status);
        Assert.Equal(0, after.Attempts);
        Assert.Equal("reprocess", (await factory.WithDbAsync(db => db.AuditEntries.SingleAsync())).Action);
    }

    [Fact]
    public async Task Errors_list_is_paged_and_filtered_by_code()
    {
        using var factory = new PanelFactory();
        var viewer = await factory.LoginAsAsync("leitor");

        var page = await viewer.GetFromJsonAsync<Page<ErrorItem>>("/api/errors?code=unmapped_status&pageSize=5");

        Assert.Equal(5, page!.Items.Count);
        Assert.All(page.Items, i => Assert.Equal(ErrorCodes.UnmappedStatus, i.ErrorCode));
        Assert.True(page.Total > 5);
    }
}
