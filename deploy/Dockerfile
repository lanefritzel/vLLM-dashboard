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

RUN groupadd -r dashboard \
    && useradd -r -g dashboard dashboard \
    && mkdir -p /data/dataprotection \
    && chown -R dashboard:dashboard /data

COPY --from=build /app/publish .

USER dashboard
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DATAPROTECTION_KEYS_DIR=/data/dataprotection

EXPOSE 8080

VOLUME ["/data"]

ENTRYPOINT ["dotnet", "Dashboard.Web.dll"]
