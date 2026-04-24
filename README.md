# EQD2 Viewer — Research Prototype

> ## ⚠️ Research prototype / Not a medical device
>
> **This repository contains a research prototype for studying EQD2 dose transformations,
> deformable image registration (DIR), and dose-volume histogram (DVH) analysis on
> radiotherapy data.**
>
> - **This software is not a medical device** within the meaning of Regulation (EU) 2017/745 (MDR) or 21 CFR 820.
> - **This software has not been CE-marked, FDA-cleared, or otherwise certified for any medical purpose.**
> - **It is not validated for clinical use** and must not be used to make or support decisions about individual patient treatment.
> - **No warranty is given, express or implied**, regarding the correctness, safety, or fitness for any particular purpose of the outputs.
> - **The authors do not provide clinical deployment support**, do not recommend any specific build or configuration for clinical use, and do not represent that the code is ready for such use.
>
> Anyone wishing to reproduce or extend this work is responsible for any regulatory, ethical,
> and institutional obligations in their own jurisdiction. Independent verification of every
> numerical result against a validated reference is required for any use that could affect a human.
>
> ### Tutkimusprototyyppi / Ei lääkinnällinen laite
>
> **Tämä repository sisältää tutkimusprototyypin EQD2-annosmuunnosten, deformable image
> registrationin ja DVH-analyysin tutkimiseen säteilyhoitodatalla.**
>
> - **Tämä ohjelmisto ei ole lääkinnällinen laite** EU:n MDR 2017/745 tarkoittamassa mielessä.
> - **Ohjelmaa ei ole CE-merkitty** eikä muuten sertifioitu kliiniseen käyttöön.
> - **Sitä ei ole validoitu kliiniseen käyttöön** eikä sitä saa käyttää yksittäisten potilaiden hoitopäätösten tekemiseen tai tukemiseen.
> - **Tuloksille ei anneta mitään takuita** niiden oikeellisuudesta, turvallisuudesta tai soveltuvuudesta mihinkään tarkoitukseen.
> - **Tekijät eivät tarjoa kliinistä käyttöönottotukea.**
>
> Jokainen joka haluaa toistaa tai laajentaa tätä tutkimustyötä vastaa omasta sääntely-, eettisestä ja organisatorisesta velvoitteestaan omalla lainkäyttöalueellaan.

---

## What this repository is

A sandbox for exploring algorithms and QA techniques related to radiotherapy dose
accumulation:

- EQD2 (Equivalent Dose in 2 Gy fractions) transformation with per-structure α/β
- Dose-volume histogram (DVH) computation
- B-spline deformable image registration via SimpleITK
- TG-132 / Bosma 2024 style DIR quality metrics (Jacobian determinant, curl, bending
  energy, FOV overlap)
- Clean-Architecture split between domain logic, rendering, and data adapters so the
  algorithms can be exercised offline from JSON fixtures without any clinical system

## What this repository is *not*

- Not a replacement for validated clinical dose-summation or DIR software
- Not a recommended toolchain for any clinical or regulatory workflow
- Not a turn-key plugin ready for deployment

## Architecture

The code is organised in a Clean Architecture layering so that the algorithmic core has
no dependency on the Varian ESAPI environment. This makes the algorithms easy to study
and test in isolation:

- `EQD2Viewer.Core` — domain types, EQD2 math, DVH, rendering math, colour maps
- `EQD2Viewer.Calculations` — image-processing primitives
- `EQD2Viewer.Services` — service layer (DVH service, summation service)
- `EQD2Viewer.Registration` — DIR interfaces + MHA deformation-field reader
- `EQD2Viewer.Registration.ITK` — optional SimpleITK-based B-spline DIR implementation
- `EQD2Viewer.App` — WPF UI for exploring the outputs
- `EQD2Viewer.DevRunner` — standalone WPF host for offline exploration via JSON fixtures
- `EQD2Viewer.Fixtures` — JSON fixture schema and loader
- `EQD2Viewer.Tests` — 300+ unit and integration tests against the core algorithms

The ESAPI-dependent adapter layer (`EQD2Viewer.Esapi`, `EQD2Viewer.FixtureGenerator`) exists
solely for generating offline JSON fixtures from consented/anonymised data so that the
algorithms can be studied outside the TPS environment. It is not an invitation to deploy the
code as an Eclipse plugin.

## Building from source (for research / study only)

Development environment:

- .NET Framework 4.8
- Visual Studio 2022 or MSBuild 17+
- x64 target

### Basic build (no DIR)

```
dotnet build EQD2Viewer.sln -c Debug
```

### Build with DIR algorithms (SimpleITK)

SimpleITK is not on NuGet and must be downloaded separately:

1. Download `SimpleITK-2.5.3-CSharp-win64-x64.zip` from
   `https://github.com/SimpleITK/SimpleITK/releases/tag/v2.5.3`
2. Extract DLLs to `lib/SimpleITK/` in the repo
3. Build with the `Release-WithITK` configuration

The DIR module is loaded at runtime by reflection; if the DLL is absent the application
runs without that feature.

## Running the research sandbox offline

The `DevRunner` is a self-contained WPF host that loads JSON fixtures from disk and
exposes the same UI used for algorithmic exploration, without requiring Eclipse or any
other TPS:

```
BuildOutput/Debug/EQD2Viewer.DevRunner.exe
```

Fixtures for offline use can be generated from data to which the user has lawful access;
this repository ships only synthetic / anonymised examples in `EQD2Viewer.Tests/TestFixtures/`.

## Tests

```
dotnet test EQD2Viewer.Tests/EQD2Viewer.Tests.csproj
```

The test suite covers the algorithmic core (EQD2 transform, DVH integration, rasterisation,
matrix math, marching squares, colour maps, rendering pipeline, serialization, DIR QA
metrics). It is **not** a clinical validation — passing tests indicate implementation matches
specification, not that outputs are clinically accurate.

## Third-party licences

| Library | Version | Licence | Used for |
|---|---|---|---|
| SimpleITK | 2.5.3 | Apache 2.0 | DIR (optional, `Release-WithITK`) |
| ITK | 5.4.5 | Apache 2.0 | SimpleITK backend |

See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for the full notices.

Varian ESAPI libraries are **not** redistributed with this repository. Reproducing the
ESAPI-dependent adapter projects requires a user to obtain those binaries under their own
Varian licence.

## Version history

| Version | Date | Notes |
|---|---|---|
| 0.9.3-beta | 2026-04 | DIR quality pass: SimpleITK BSpline3 canonical pattern (small initial mesh + scale factors + GradientDescentLineSearch). Body-mask preprocessing (HU threshold + largest CC + fillhole) cuts registration time ~9x and eliminates fold artifacts. TG-132 / Bosma 2024 style QA report (Jacobian, curl, bending energy, displacement, histograms). FOV overlap analyser with axis-aligned AABB. Per-level convergence logging including optimiser stop-condition. Monotonic progress mapping across pyramid levels. Research-prototype positioning in UI titles, startup log, and README. First draft of regulatory technical documentation under `docs/regulatory/`. |
| 0.9.2-beta | 2026-04 | SimpleITK-based B-spline DIR added as optional module. MHA/MHD deformation-field reader. |
| 0.9.1-beta | 2026-04 | Clean Architecture refactor. Offline DevRunner, centralised BuildOutput, dependency management. |
| 0.9.0-beta | 2026-03 | Feature and calculation stabilisation. |
| 0.3.0-alpha | 2026-03 | Automatic `.esapi.dll` naming via project metadata. |
| 0.2.0-alpha | 2026-03 | Unit test suite, ESAPI stubs for offline CI, GitHub Actions pipeline. |
| 0.1.0-alpha | 2026-03 | First alpha. CT/dose display, isodose, EQD2, summation, DVH, per-structure α/β. |

## Authors

Risto Hirvilammi & Juho Ala-Myllymäki.

Contributions are made in a personal / research capacity. Inclusion of any institutional
affiliation in commit history or older versions does not imply institutional endorsement,
validation, or clinical acceptance of this software.

## Licence

MIT — see [`LICENSE.txt`](LICENSE.txt).

The MIT "AS IS" warranty disclaimer is a copyright-licence term. It does not, and cannot,
discharge any regulatory obligations imposed on a subsequent user under MDR, FDA rules, or
national radiation-safety law. Those obligations rest entirely with whoever uses the code.
