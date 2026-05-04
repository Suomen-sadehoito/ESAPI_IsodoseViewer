# EQD2 Viewer

A WPF research viewer for EQD2 dose transforms, DVH analysis, and
affine multi-plan summation on a reference CT grid.

## Disclaimer

Research code, not a medical device. No CE mark, no FDA clearance, not
validated for clinical use under EU MDR 2017/745 or 21 CFR 820. Don't
use it to make or support patient treatment decisions. Double-check
anything important against a validated reference.

*Suomeksi: tämä on tutkimusprototyyppi, ei lääkinnällinen laite. Älä
käytä sitä potilaan hoitopäätöksiin. Käyttäjä vastaa oman maansa
sääntelyvelvoitteista.*

## Layout

The algorithm core (`EQD2Viewer.Core`, `EQD2Viewer.Services`) has no
Varian ESAPI dependency, so it builds and tests on plain .NET. The WPF
UI in `EQD2Viewer.App` doesn't touch ESAPI either — it just consumes a
`ClinicalSnapshot` POCO.

Two ways to feed the UI:

- `EQD2Viewer.Esapi` — the actual ESAPI script. Runs inside Eclipse,
  pulls a snapshot from the live TPS, and hands it to `AppLauncher`.
- `EQD2Viewer.DevRunner` — standalone WPF host. Loads a JSON snapshot
  from disk and runs the same UI without Eclipse.

`EQD2Viewer.FixtureGenerator` is a separate ESAPI script that dumps
those JSON snapshots — that's how DevRunner gets its input.

## Build & test

Targets .NET Framework 4.8, x64. Visual Studio 2022 or MSBuild 17+.

```
dotnet build EQD2Viewer.sln -c Debug
dotnet test EQD2Viewer.Tests/EQD2Viewer.Tests.csproj
```

The test suite covers most of the algorithmic core. Passing tests mean
the code matches what was specified — not that the numbers are
clinically correct.

## Offline runs

```
BuildOutput/Debug/EQD2Viewer.DevRunner.exe
```

Synthetic example fixtures sit under `EQD2Viewer.Tests/TestFixtures/`.
If you want to feed it real data, generate the fixtures yourself with
whatever access and consent your local rules require.

## Third-party libraries

Varian ESAPI binaries aren't redistributed — bring your own, under your
Varian licence.

## Status

Currently on **0.9.4-beta** (April 2026). 0.9.2 and 0.9.3 added a
SimpleITK-based deformable registration module; both were pulled and
removed in 0.9.4 when the project was narrowed back to affine-only
summation. Earlier 0.x betas were the project-layout pass and the
initial feature work.

## Authors

Risto Hirvilammi & Juho Ala-Myllymäki, in a personal / research
capacity.

## Licence

MIT — see [`LICENSE.txt`](LICENSE.txt).
