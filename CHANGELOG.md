# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Project skeleton: `Waybill`, `Waybill.EntityFrameworkCore.PostgreSql` and `Waybill.RabbitMQ` packages targeting .NET 10, with unit, integration and chaos test projects.
- CI on pull requests (PostgreSQL 15 and 18), scheduled workflow for long scenarios, and tag-based release pipeline.
- ADR 0001: row claim with `FOR UPDATE SKIP LOCKED`, per-row lease and fencing token, with the stage 0 spike results.
