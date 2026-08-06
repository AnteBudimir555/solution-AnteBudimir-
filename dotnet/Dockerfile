# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage: restore, compile and publish the framework-dependent output.
# The build context is this `dotnet/` directory, so the Java service's Dockerfile
# at the repository root is untouched and both images can be built side by side.
# Tests are not run here — CI runs `dotnet test`; the image build produces the
# artifact.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /workspace

# Project files first, so the restore layer is keyed on dependencies alone and is
# only redone when a csproj changes — not on every source edit. The BuildKit cache
# mount keeps the NuGet package cache across builds independently of that, so a
# dependency change re-fetches only what actually changed.
COPY Directory.Build.props ./
COPY src/Middleware.Core/Middleware.Core.csproj src/Middleware.Core/
COPY src/Middleware.Infrastructure/Middleware.Infrastructure.csproj src/Middleware.Infrastructure/
COPY src/Middleware.Api/Middleware.Api.csproj src/Middleware.Api/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/Middleware.Api/Middleware.Api.csproj

COPY src/ src/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Middleware.Api/Middleware.Api.csproj \
        --configuration Release --no-restore --output /app/publish

# ---------------------------------------------------------------------------
# Runtime stage: the ASP.NET Core runtime image with only the published output.
# (aspnet:10.0 once the plan's .NET 10 target is adopted — see Directory.Build.props.)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# Run unprivileged. The .NET 8 runtime images ship a non-root `app` user and
# publish its id as APP_UID, which is the supported way to select it.
USER $APP_UID

# The runtime image defaults ASPNETCORE_HTTP_PORTS to 8080; stated here so the
# port the container listens on is visible in this file rather than inherited.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "/app/Middleware.Api.dll"]
