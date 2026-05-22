# Build context: repository root (../../ from this file). The compose file sets this explicitly
# so that ProjectReference paths from NotificationService.csproj into ../../../src/RayTree.* resolve.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# ── Layer 1: central build infrastructure ────────────────────────────────────────────────────────
# Copied first because it changes rarely. All subsequent layers inherit TargetFramework, Nullable,
# TreatWarningsAsErrors, and centrally-pinned NuGet versions from these files.
COPY Directory.Build.props Directory.Packages.props NuGet.config ./

# ── Layer 2: .csproj files only (restore cache) ──────────────────────────────────────────────────
# Copy project files for NotificationService and every ProjectReference in its dependency graph
# (direct: Shared + five RayTree plugins; transitive: all plugins → RayTree.Core).
# NotificationService has no PostgreSQL dependency — only the RabbitMQ + serializer + compressor stack.
COPY src/RayTree.Core/RayTree.Core.csproj                                                        src/RayTree.Core/
COPY src/RayTree.Hosting/RayTree.Hosting.csproj                                                  src/RayTree.Hosting/
COPY src/RayTree.Plugins.RabbitMQ/RayTree.Plugins.RabbitMQ.csproj                               src/RayTree.Plugins.RabbitMQ/
COPY src/RayTree.Plugins.Serializers.MessagePack/RayTree.Plugins.Serializers.MessagePack.csproj  src/RayTree.Plugins.Serializers.MessagePack/
COPY src/RayTree.Plugins.Compressors.Gzip/RayTree.Plugins.Compressors.Gzip.csproj                src/RayTree.Plugins.Compressors.Gzip/
COPY examples/RabbitMQ.Microservices/Directory.Build.props     examples/RabbitMQ.Microservices/
COPY examples/RabbitMQ.Microservices/Directory.Packages.props  examples/RabbitMQ.Microservices/
COPY examples/RabbitMQ.Microservices/Shared/Shared.csproj                                        examples/RabbitMQ.Microservices/Shared/
COPY examples/RabbitMQ.Microservices/NotificationService/NotificationService.csproj              examples/RabbitMQ.Microservices/NotificationService/
RUN dotnet restore examples/RabbitMQ.Microservices/NotificationService/NotificationService.csproj

# ── Layer 3: source files → publish ──────────────────────────────────────────────────────────────
# Packages are already restored above; --no-restore skips the redundant network round-trip.
COPY src/ ./src/
COPY examples/RabbitMQ.Microservices/ ./examples/RabbitMQ.Microservices/
WORKDIR /src/examples/RabbitMQ.Microservices/NotificationService
RUN dotnet publish -c Release -o /app /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "RabbitMqMicroservices.NotificationService.dll"]
