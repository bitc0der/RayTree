# Build context: repository root (../../ from this file). The compose file sets this explicitly
# so that ProjectReference paths from NotificationService.csproj into ../../../src/RayTree.* resolve.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/ ./src/
COPY examples/RabbitMQ.Microservices/ ./examples/RabbitMQ.Microservices/

WORKDIR /src/examples/RabbitMQ.Microservices/NotificationService
RUN dotnet restore
RUN dotnet publish -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "RabbitMqMicroservices.NotificationService.dll"]
