# Contributing to Unswarm

Thanks for your interest in contributing! Unswarm is an open source project and contributions of all kinds are welcome — bug reports, feature requests, documentation improvements, and code.

## Getting Started

1. Fork the repository
2. Create a branch for your change (`git checkout -b my-feature`)
3. Make your changes
4. Test your changes (see below)
5. Submit a pull request

## Development Setup

See the [README](README.md#quick-start) for prerequisites and setup instructions for each component.

### Backend (.NET)

```bash
cd backend
dotnet build
dotnet test
```

### Frontend (React)

```bash
cd frontend
pnpm install
pnpm dev       # dev server
pnpm test      # unit tests
pnpm lint      # linting
```

### Agent (Go)

```bash
cd agent
go build ./cmd/agent
go test ./...
```

## Pull Request Guidelines

- Keep PRs focused — one change per PR when possible
- Include a clear description of what changed and why
- Add tests for new functionality when practical
- Make sure existing tests pass
- Follow the existing code style in each component

## Reporting Issues

Open an issue on GitHub. Include:
- What you expected to happen
- What actually happened
- Steps to reproduce
- Your environment (OS, Docker version, Go/Node/.NET versions)

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
