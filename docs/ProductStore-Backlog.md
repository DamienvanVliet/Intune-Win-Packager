# Product Store Backlog

## Goal
Expand the package store beyond the current baseline while keeping Intune preparation deterministic, maintainable, and production-safe.

## Current Status
- Phase 1 started.
- Implemented:
  - Dynamic WinGet source discovery (`winget source list`) with search across configured non-explicit sources.
  - New optional source: GitHub Releases.
  - Store readiness badge model (`Ready`, `Needs review`, `Blocked`).
  - Removed automatic EXE silent-switch auto-verification (now evidence-first).

## Phase 1 - Source and Reliability Foundation

### 1. Multi-source discovery (in progress)
- Add WinGet configured source support:
  - Include non-explicit sources (for example `winget`, `msstore`) automatically.
  - Keep explicit sources excluded by default.
- Add GitHub Releases search + details + download flow.
- File scope:
  - `IntuneWinPackager.Infrastructure/Services/PackageCatalogService.cs`
  - `IntuneWinPackager.Models/Entities/PackageCatalogQuery.cs`
  - `IntuneWinPackager.Models/Enums/PackageCatalogSource.cs`
  - `IntuneWinPackager.App/ViewModels/MainViewModel.cs`
  - `IntuneWinPackager.App/MainWindow.xaml`
  - `IntuneWinPackager.App/Localization/Strings.en.xaml`
  - `IntuneWinPackager.App/Localization/Strings.nl.xaml`

### 2. Deterministic store readiness (in progress)
- Readiness model:
  - `Ready`: verified prepared profile with deterministic detection and no placeholders.
  - `Needs review`: usable but not verified as fully production-ready.
  - `Blocked`: prepared profile is not packagable as-is.
- File scope:
  - `IntuneWinPackager.Models/Enums/CatalogReadinessState.cs`
  - `IntuneWinPackager.Models/Entities/PackageCatalogEntry.cs`
  - `IntuneWinPackager.App/ViewModels/MainViewModel.cs`
  - `IntuneWinPackager.App/MainWindow.xaml`
  - localization files

### 3. Evidence-first switch verification (done)
- Stop setting EXE silent switches to verified automatically without evidence.
- File scope:
  - `IntuneWinPackager.App/ViewModels/MainViewModel.cs`

### 4. Phase 1 test expansion (pending)
- Add tests for:
  - WinGet multi-source parsing and search behavior.
  - GitHub releases source search/details/download happy path and failure path.
  - Readiness classification mapping from prepared profile state.
- File scope:
  - `IntuneWinPackager.Tests/Services/PackageCatalogServiceTests.cs`
  - new VM/store classification tests

## Phase 2 - Normalization and Mapping

### 1. Canonical package identity
- Add normalization key strategy (publisher + product + channel).
- Merge duplicates across sources into one logical package with source variants.

### 2. Structured installer variants
- Support multiple installer variants per package:
  - architecture, scope, installer type, hash, signature, URL, version.

### 3. Deterministic Intune mapping
- Keep one authoritative detection strategy per variant:
  - MSI: ProductCode-first.
  - EXE: strict registry value equality.
  - APPX/MSIX: exact identity and version.

## Phase 3 - Scale and Enterprise

### 1. Additional sources
- Scoop buckets.
- NuGet v3 feeds (including private/internal feeds).
- Chocolatey configured sources (not only community endpoint).

### 2. Storage and performance
- Move catalog/profile persistence from flat JSON-only pattern to indexed store (SQLite).
- Add stale-cache policy and background refresh.

### 3. Operational quality
- Provider health telemetry (timeouts, source failure reasons).
- Retry/backoff policy and user-facing source diagnostics.

## Definition of Done (for each source provider)
- Search works and returns normalized entries.
- Details enriches metadata deterministically.
- Download resolves to supported installer artifact.
- Intune prep guidance uses deterministic rules only.
- Unit tests cover success and failure paths.
