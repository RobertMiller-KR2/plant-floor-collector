# Plant Floor Collector - production Docker build
# Build from the repository/package root:
#   docker compose up -d --build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG APP_VERSION=0.0.0
ARG APP_COMMIT=unknown
ARG APP_BUILD_DATE=unknown
ARG APP_IMAGE=plant-floor-collector:local
WORKDIR /src

COPY src/PlantFloorCollector/PlantFloorCollector.csproj ./src/PlantFloorCollector/
RUN dotnet restore ./src/PlantFloorCollector/PlantFloorCollector.csproj

COPY src/PlantFloorCollector ./src/PlantFloorCollector
RUN dotnet publish ./src/PlantFloorCollector/PlantFloorCollector.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:Version=${APP_VERSION} \
    "/p:InformationalVersion=${APP_VERSION}+${APP_COMMIT}"

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
ARG APP_VERSION=dev
ARG APP_COMMIT=unknown
ARG APP_BUILD_DATE=unknown
ARG APP_IMAGE=unknown
WORKDIR /app
RUN mkdir -p /app/config /app/data /app/logs /app/backups /app/drivers /app/certs /app/temp
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV APP_VERSION=$APP_VERSION
ENV APP_COMMIT=$APP_COMMIT
ENV APP_BUILD_DATE=$APP_BUILD_DATE
ENV APP_IMAGE=$APP_IMAGE
ENV Collector__DatabasePath=/app/data/plant_floor_collector.db
ENV Collector__ConfigPath=/app/config
ENV Collector__DataPath=/app/data
ENV Collector__LogsPath=/app/logs
ENV Collector__BackupsPath=/app/backups
ENV Collector__DriversPath=/app/drivers
ENV Collector__CertificatesPath=/app/certs
ENV Collector__TempPath=/app/temp
VOLUME ["/app/config", "/app/data", "/app/logs", "/app/backups", "/app/drivers", "/app/certs", "/app/temp"]
ENTRYPOINT ["dotnet", "PlantFloorCollector.dll"]
