# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Dashboard.Core/Dashboard.Core.csproj src/Dashboard.Core/
COPY src/Dashboard.Infrastructure/Dashboard.Infrastructure.csproj src/Dashboard.Infrastructure/
COPY src/Dashboard.Application/Dashboard.Application.csproj src/Dashboard.Application/
COPY src/Dashboard.Web/Dashboard.Web.csproj src/Dashboard.Web/
RUN dotnet restore src/Dashboard.Web/Dashboard.Web.csproj

COPY src/ src/
RUN dotnet publish src/Dashboard.Web/Dashboard.Web.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Base image already ships a non-root "app" user at a stable UID/GID (1654) --
# reuse it instead of creating a separate one, so a host-side cert copy can be
# chowned to a known owner across image rebuilds.
RUN mkdir -p /data/dataprotection \
    && chown -R app:app /data

COPY --from=build /app/publish .

USER app
ENV ASPNETCORE_ENVIRONMENT=Production \
    Kestrel__Endpoints__Http__Url=http://+:8080 \
    DATAPROTECTION_KEYS_DIR=/data/dataprotection

# HTTPS is opt-in: set Kestrel__Endpoints__Https__Url plus the matching
# Kestrel__Endpoints__Https__Certificate__* env vars (Path+KeyPath for a PEM
# cert/key pair, or Path+Password for a PFX) to enable it. Kestrel's own
# config binding picks the cert format from whichever keys are set — nothing
# in the app code is tied to one format.
EXPOSE 8080
EXPOSE 8445

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "Dashboard.Web.dll"]
