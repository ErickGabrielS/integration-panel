# 1) Tela React
FROM node:22-alpine AS web
WORKDIR /src/src/IntegrationPanel.Web
COPY src/IntegrationPanel.Web/package*.json ./
RUN npm ci
COPY src/IntegrationPanel.Web/ ./
RUN npm run build

# 2) API .NET 10
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/IntegrationPanel.Api/IntegrationPanel.Api.csproj src/IntegrationPanel.Api/
RUN dotnet restore src/IntegrationPanel.Api/IntegrationPanel.Api.csproj
COPY src/IntegrationPanel.Api/ src/IntegrationPanel.Api/
COPY --from=web /src/src/IntegrationPanel.Api/wwwroot src/IntegrationPanel.Api/wwwroot
RUN dotnet publish src/IntegrationPanel.Api/IntegrationPanel.Api.csproj -c Release -o /app --no-restore

# 3) Imagem final, só com o runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Panel="Data Source=/data/panel.db"
RUN mkdir -p /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "IntegrationPanel.Api.dll"]
