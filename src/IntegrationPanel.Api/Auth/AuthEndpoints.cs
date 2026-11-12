using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IntegrationPanel.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IntegrationPanel.Api.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "integration-panel";
    public string Audience { get; set; } = "integration-panel";

    /// <summary>Chave de assinatura (32+ caracteres). Em produção, só por variável de ambiente ou user-secrets.</summary>
    public string Key { get; set; } = string.Empty;

    public int ExpiresMinutes { get; set; } = 120;
}

public sealed record LoginRequest(string UserName, string Password);

public sealed record LoginResponse(string Token, string UserName, string DisplayName, string Role, DateTime ExpiresAtUtc);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", LoginAsync).AllowAnonymous();
    }

    internal static async Task<IResult> LoginAsync(
        LoginRequest request,
        PanelDbContext db,
        IOptions<JwtOptions> jwt,
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserName == request.UserName, ct);

        // Mesma resposta para usuário inexistente e senha errada: não revela quais usuários existem.
        if (user is null ||
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, request.Password)
                == PasswordVerificationResult.Failed)
        {
            return Results.Problem("Usuário ou senha inválidos.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var opt = jwt.Value;
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(opt.ExpiresMinutes);
        var token = new JwtSecurityToken(
            issuer: opt.Issuer,
            audience: opt.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.UserName),
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim("display_name", user.DisplayName),
                new Claim(ClaimTypes.Role, user.Role)
            ],
            expires: expires,
            signingCredentials: new SigningCredentials(SigningKey(opt), SecurityAlgorithms.HmacSha256));

        return Results.Ok(new LoginResponse(
            new JwtSecurityTokenHandler().WriteToken(token), user.UserName, user.DisplayName, user.Role, expires));
    }

    public static SymmetricSecurityKey SigningKey(JwtOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.Key) || opt.Key.Length < 32)
        {
            throw new InvalidOperationException("Configure Jwt:Key com pelo menos 32 caracteres.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.Key));
    }

    public static string UserName(this ClaimsPrincipal user) => user.Identity?.Name ?? "desconhecido";
}
