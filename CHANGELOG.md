# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-04-10

### Added

- Connection pooling with `IAdsConnectionPool` for managing multiple PLC connections
- Automatic reconnection with exponential backoff (2s–30s) and periodic health checks
- Embedded ADS TCP/IP router support via `AdsRouterService`
- Symbol read/write operations (single and batch) with configurable timeouts
- Device notification subscriptions for real-time PLC data
- Simulation mode with in-memory key-value store for offline development
- ASP.NET Core integration via `AddTwinCatAds()` and `AddTwinCatAdsSimulation()` extension methods
- Multi-target support for .NET 8.0, 9.0, and 10.0
- CI pipeline with build and test across all target frameworks
- NuGet release pipeline triggered by version tags
- Apache 2.0 license

[0.1.0]: https://github.com/patdhlk/Dahlke.TwinCAT.Ads/releases/tag/v0.1.0
