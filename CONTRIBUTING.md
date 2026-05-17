# Contributing to RayTree

Thank you for your interest in contributing to RayTree!

## Getting Started

1. Fork the repository on GitHub
2. Clone your fork locally:
   ```bash
   git clone https://github.com/<your-username>/RayTree.git
   cd RayTree
   ```
3. Add the upstream remote:
   ```bash
   git remote add upstream https://github.com/bitc0der/RayTree.git
   ```
4. Create a feature branch from `main`:
   ```bash
   git checkout -b feature/your-feature-name
   ```

## Prerequisites

- **.NET 10 SDK**
- **Docker** — required for integration tests (PostgreSQL, RabbitMQ, Kafka)

## Build, Test, and Style

See [AGENTS.md](AGENTS.md) for the full list of build commands, test targets, and coding standards (naming, async/await, exception handling, DI, design principles, testing conventions, nullability).

Quick summary:

```bash
dotnet build RayTree.slnx
dotnet test tests/RayTree.Core.Tests        # run relevant unit tests
dotnet format --verify-no-changes           # verify style compliance
```

## Pull Request Process

1. **Before submitting:**
   - Ensure the solution builds: `dotnet build RayTree.slnx`
   - Run relevant unit tests and ensure they pass
   - Run `dotnet format --verify-no-changes`
   - Update documentation if your change affects public APIs or behavior

2. **Commit guidelines:**
   - Write clear, concise commit messages
   - Keep commits focused — one logical change per commit
   - Do not commit secrets, connection strings, or credentials

3. **PR requirements:**
   - Reference any related issues in the PR description
   - Describe what changed and why
   - Note any breaking changes
   - Include test coverage for new functionality

4. **CI gate:**
   - All PRs must pass the CI pipeline (build + unit tests + integration tests for relevant plugins)
   - The CI workflow builds once and shares the artifact across test jobs

## Plugin Development

See [docs/plugin-development.md](docs/plugin-development.md) for the full plugin development guide, including interface contracts, pipeline flow, and testing patterns.

To add a new plugin:

1. Create a project under `src/RayTree.Plugins.<Name>/`
2. Implement the relevant interface(s)
3. Add builder extension methods for easy registration
4. Add a test project under `tests/RayTree.Plugins.<Name>.Tests/`
5. Register the project in `RayTree.slnx`
6. Add any new packages to `Directory.Packages.props` (see [AGENTS.md](AGENTS.md) for package management rules)

## Reporting Issues

### Bug Reports

Include:

- RayTree version
- .NET SDK version
- Steps to reproduce
- Expected vs. actual behavior
- Relevant code snippets or logs
- Whether the issue is reproducible with in-memory plugins or requires a specific broker

### Feature Requests

Include:

- A clear description of the problem you're trying to solve
- Why existing functionality doesn't meet your needs
- A proposed solution or API design (if you have one)
- Any alternative approaches you've considered

## Security Vulnerabilities

See [SECURITY.md](SECURITY.md). Do not open a public GitHub issue for security vulnerabilities — use [GitHub Security Advisories](https://github.com/bitc0der/RayTree/security/advisories/new) instead.

## Further Reading

| Document | Content |
|---|---|
| [README.md](README.md) | Overview, quick start, package list |
| [AGENTS.md](AGENTS.md) | Build commands, coding standards, testing conventions, repository layout |
| [docs/plugin-development.md](docs/plugin-development.md) | Plugin interfaces, pipeline flow, registration patterns |
| [docs/configuration.md](docs/configuration.md) | Configuration options and overrides |
| [docs/ef-core-integration.md](docs/ef-core-integration.md) | EF Core interceptor usage |
| [docs/database-migration.md](docs/database-migration.md) | PostgreSQL schema migration and type mappings |
| [docs/trigger-setup.md](docs/trigger-setup.md) | PostgreSQL trigger setup |
| [docs/serializers-compressors.md](docs/serializers-compressors.md) | Serializer and compressor options |
| [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md) | OTel metrics instrumentation |
| [SECURITY.md](SECURITY.md) | Security policy and vulnerability reporting |
| [CHANGELOG.md](CHANGELOG.md) | Release history and breaking changes |
