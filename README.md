# EQD2 Viewer

ESAPI-skripti Varian Eclipseen EQD2 jakaumien tarkasteluun ja uudelleensäteilytyksen kumulatiivisen annosjakauman arviointiin.

## Projektin tila

**Alpha** Perustoiminnallisuus on kasassa mutta testausta kliinisillä potilailla ei ole vielä tehty riittävästi. Käytä omalla vastuulla ja tarkista aina laskelmat käsin.

## Vaatimukset

- Eclipse + ESAPI v15.6 tai uudempi
- .NET Framework 4.8

## Kääntäminen

Avaa `ESAPI_EQD2Viewer.sln` Visual Studiossa. Käännä **Release|x64**. Costura.Fody pakkaa kaikki riippuvuudet yhteen DLL:ään.

## Käyttö

1. Avaa potilas ja hoitosuunnitelma Eclipsessä
2. Aja skripti

## Versiohistoria
 
| Versio | Päivämäärä | Kuvaus |
|--------|------------|--------|
| 0.2.0-alpha | 2026-03 | Yksikkötestit (107 kpl), ESAPI-stub-kirjasto CI-kääntämiseen, GitHub Actions -pipeline. |
| 0.1.0-alpha | 2026-03 | Ensimmäinen alpha. CT/annos-näyttö, isodoosit, EQD2-muunnos, summaatio, DVH, rakennekohtainen α/β. | |

## Tekijät

Risto Hirvilammi & Juho Ala-Myllymäki, OVPH

## Lisenssi

MIT — ks. LICENSE.txt
