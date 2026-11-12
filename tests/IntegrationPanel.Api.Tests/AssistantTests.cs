using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntegrationPanel.Api.Ai;
using IntegrationPanel.Api.Data;
using IntegrationPanel.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationPanel.Api.Tests;

public sealed class AssistantTests
{
    [Fact]
    public async Task Answers_with_data_from_a_tool_and_records_every_model_call()
    {
        using var factory = new PanelFactory();
        var viewer = await factory.LoginAsAsync("leitor");

        var answer = await Ask(viewer, "Quais status estão sem de-para?");

        Assert.Contains(AssistantTools.TopUnmappedStatuses, answer.ToolsUsed);
        Assert.Contains("EM_ANALISE", answer.Answer);
        Assert.Equal(2, answer.ModelCalls); // pede a ferramenta, depois responde
        Assert.Equal(2, await factory.WithDbAsync(db => db.AiCalls.CountAsync()));
    }

    [Fact]
    public async Task Tool_results_are_sent_back_to_the_model_as_tool_messages()
    {
        var model = new ScriptedChatModel(
            ScriptedChatModel.CallTool(AssistantTools.ErrorSummary, """{"days": 7}"""),
            ScriptedChatModel.Answer("Resumo pronto."));
        using var factory = new PanelFactory(model);
        var viewer = await factory.LoginAsAsync("leitor");

        await Ask(viewer, "Como foi a semana?");

        var second = model.Conversations[1];
        var toolMessage = Assert.Single(second, m => m.Role == "tool");
        Assert.Equal($"call_{AssistantTools.ErrorSummary}", toolMessage.ToolCallId);
        Assert.Equal(240, JsonDocument.Parse(toolMessage.Content!).RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_tool_that_does_not_exist_returns_an_error_and_changes_nothing()
    {
        var model = new ScriptedChatModel(
            ScriptedChatModel.CallTool("delete_all_orders"),
            ScriptedChatModel.Answer("Não posso fazer isso."));
        using var factory = new PanelFactory(model);
        var approver = await factory.LoginAsAsync("aprovador");
        var before = await factory.WithDbAsync(db => db.IntegrationLogs.CountAsync());

        var answer = await Ask(approver, "Apague todos os pedidos com erro.");

        var toolMessage = model.Conversations[1].Single(m => m.Role == "tool");
        Assert.Contains("não existe", toolMessage.Content);
        Assert.Equal("Não posso fazer isso.", answer.Answer);
        Assert.Equal(before, await factory.WithDbAsync(db => db.IntegrationLogs.CountAsync()));
    }

    [Fact]
    public async Task Stops_after_the_maximum_number_of_rounds()
    {
        var model = new ScriptedChatModel(ScriptedChatModel.CallTool(AssistantTools.ErrorSummary));
        using var factory = new PanelFactory(model);
        var viewer = await factory.LoginAsAsync("leitor");

        var answer = await Ask(viewer, "Pergunta que faz o modelo entrar em laço");

        Assert.Equal(4, answer.ModelCalls);
        Assert.Contains("Não consegui concluir", answer.Answer);
    }

    [Fact]
    public async Task Daily_limit_blocks_the_model_and_returns_429()
    {
        var model = new ScriptedChatModel(ScriptedChatModel.Answer("não deveria ser chamado"));
        using var factory = new PanelFactory(model, dailyCallLimit: 0);
        var viewer = await factory.LoginAsAsync("leitor");

        var response = await viewer.PostAsJsonAsync("/api/assistant/ask", new AskRequest("Como foi a semana?"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(model.Conversations);
    }

    [Fact]
    public async Task Cost_is_recorded_for_real_models()
    {
        var model = new ScriptedChatModel(ScriptedChatModel.Answer("Tudo certo."));
        using var factory = new PanelFactory(model);
        var viewer = await factory.LoginAsAsync("leitor");

        await Ask(viewer, "Oi");

        var call = await factory.WithDbAsync(db => db.AiCalls.SingleAsync());
        Assert.Equal("scripted", call.Provider);
        Assert.True(call.CostUsd > 0);
    }

    [Fact]
    public async Task Tool_arguments_from_the_model_are_clamped()
    {
        using var factory = new PanelFactory();
        using var scope = factory.Services.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<AssistantTools>();

        var huge = await tools.ExecuteAsync(AssistantTools.RecentErrors, """{"days": 9999, "limit": 9999}""", CancellationToken.None);
        var broken = await tools.ExecuteAsync(AssistantTools.ErrorSummary, "não é json", CancellationToken.None);

        var root = JsonDocument.Parse(huge).RootElement;
        Assert.Equal(30, root.GetProperty("days").GetInt32());
        Assert.True(root.GetProperty("errors").GetArrayLength() <= 10);
        Assert.Contains("inválidos", broken);
    }

    [Fact]
    public void Question_is_cleaned_and_limited()
    {
        var clean = AssistantService.Sanitize("oi\u0000\u0007 " + new string('a', 2000));

        Assert.DoesNotContain('\u0000', clean);
        Assert.Equal(AssistantService.MaxQuestionLength, clean.Length);
    }

    private static async Task<AssistantAnswer> Ask(HttpClient client, string question)
    {
        var response = await client.PostAsJsonAsync("/api/assistant/ask", new AskRequest(question));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AssistantAnswer>())!;
    }
}
