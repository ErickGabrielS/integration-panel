using IntegrationPanel.Api.Ai;

namespace IntegrationPanel.Api.Endpoints;

public sealed record AskRequest(string Question);

public static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assistant").RequireAuthorization();

        group.MapPost("/ask", async (AskRequest request, IServiceProvider services, CancellationToken ct) =>
        {
            var assistant = services.GetService<AssistantService>();
            if (assistant is null)
            {
                return Results.Problem("O assistente de IA está desligado (Ai:Provider = Off).", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var answer = await assistant.AskAsync(request.Question, ct);
            return answer.BudgetExhausted
                ? Results.Json(answer, statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Ok(answer);
        });

        group.MapGet("/usage", async (IServiceProvider services, CancellationToken ct) =>
            services.GetService<AssistantService>() is { } assistant
                ? Results.Ok(await assistant.UsageTodayAsync(ct))
                : Results.Ok(new { provider = "off" }));
    }
}
