# The API image (docs/DEPLOY.md). Build from the repository root: docker compose -f deploy/docker-compose.yml build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
# Infrastructure embeds i18n/strings.json (../../i18n/strings.json in its csproj); without it the image build fails with CS1566.
COPY i18n/ i18n/
COPY src/LifeDrop.Domain/ src/LifeDrop.Domain/
COPY src/LifeDrop.Application/ src/LifeDrop.Application/
COPY src/LifeDrop.Infrastructure/ src/LifeDrop.Infrastructure/
COPY src/LifeDrop.API/ src/LifeDrop.API/
RUN dotnet publish src/LifeDrop.API/LifeDrop.API.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "LifeDrop.API.dll"]
