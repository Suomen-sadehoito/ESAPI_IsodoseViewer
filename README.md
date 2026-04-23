# EQD2 Viewer

ESAPI-skripti Varian Eclipseen EQD2-jakaumien tarkasteluun ja uudelleensäteilytyksen kumulatiivisen annosjakauman arviointiin.

## Projektin tila
**Alpha / Beta:** Perustoiminnallisuus on valmiina, mutta kattavaa testausta kliinisillä potilailla ei ole vielä tehty riittävästi. Käytä omalla vastuulla ja tarkista laskelmat aina manuaalisesti.

## Arkkitehtuuri ja ominaisuudet
Projekti on rakennettu Clean Architecture -mallilla, joka eristää Varianin ESAPI-riippuvuudet, käyttöliittymän ja liiketoimintalogiikan toisistaan. 

Tämä mahdollistaa sovelluksen joustavan kehittämisen ja testaamisen paikallisesti:
1. Kliininen data voidaan purkaa Eclipsestä lokaaleiksi JSON-tiedostoiksi `FixtureGenerator`-työkalun avulla.
2. Kehitystyötä ja testausta voidaan jatkaa `DevRunner.exe`-työpöytäsovelluksella täysin ilman Eclipse-ympäristöä.

## Vaatimukset
- Eclipse + ESAPI v15.6 tai uudempi
- .NET Framework 4.8
- Visual Studio 2022 (tai MSBuild 17+), x64-kohde

## Kääntäminen ja asennus

Varianin suljetun lähdekoodin ESAPI-kirjastoja ei jaeta versionhallinnassa tekijänoikeussyistä. Ennen ensimmäistä kääntämistä:

1. Kopioi tiedostot `VMS.TPS.Common.Model.API.dll` ja `VMS.TPS.Common.Model.Types.dll` Eclipsen asennuskansiosta (tai sairaalan työasemalta) projektin `lib\ESAPI\`-kansioon.
2. Avaa `EQD2Viewer.sln` Visual Studiossa.
3. Varmista yläpalkista, että valittuna on **Release** ja **x64**.
4. Valitse **Build -> Build Solution**.
5. Käännöksen jälkeen projektin juureen ilmestyy `BuildOutput`-kansio, josta löytyvät valmiit asennustiedostot:
   - **01_Eclipse_ESAPI_Plugins:** Kopioi täältä löytyvät `.esapi.dll`-tiedostot suoraan sairaalan Eclipsen skriptikansioon. Costura.Fody on pakannut kaikki tarvittavat riippuvuudet näiden tiedostojen sisään.
   - **02_Standalone_Runner:** Sisältää `DevRunner.exe`:n ja testidatan paikallista käyttöä ja kehitystä varten.

### Vaihtoehto: Release-WithITK (deformable-rekisteröinnillä)

Konfiguraatio `Release-WithITK` kääntää edellisten lisäksi `EQD2Viewer.Registration.ITK.dll`-moduulin, joka mahdollistaa B-spline-pohjaisen deformable image registration (DIR) -laskennan suoraan ohjelmasta käsin.

SimpleITK **ei** ole NuGet.orgissa — binäärit täytyy ladata erikseen, samaan tapaan kuin ESAPI-kirjastot:

1. Lataa **SimpleITK 2.5.3** Windows x64 C# -paketti GitHubista:  
   `https://github.com/SimpleITK/SimpleITK/releases/tag/v2.5.3`  
   Tiedosto: `SimpleITK-2.5.3-CSharp-win64-x64.zip` (16,8 MB)
2. Pura ZIP ja kopioi kaikki DLL-tiedostot projektin `lib\SimpleITK\`-kansioon (kansio on jo valmiina repossa). SimpleITK 2.5.3 sisältää:
   - `SimpleITKCSharpManaged.dll` (hallittu .NET-kokoonpano)
   - `SimpleITKCSharpNative.dll` (natiivi C++-wrapper)
3. Vaihda Visual Studion yläpalkin konfiguraatioksi **Release-WithITK** (x64).
4. Valitse **Build -> Build Solution**.
5. `BuildOutput\Release-WithITK\`-kansioon ilmestyy ylimääräinen hakemisto `03_ITK_Registration\` natiiveineen.
6. Rekisteröintimoduuli ladataan suoritusaikana reflektiolla — jos `EQD2Viewer.Registration.ITK.dll` puuttuu, ohjelma toimii normaalisti ilman DIR-ominaisuutta.

> **Huom.** Deformable-rekisteröinti on laskennallisesti raskas operaatio. Se sopii jälkikäteiseen arviointiin, ei reaaliaikaiseen kliiniseen käyttöön. Tulos on aina tarkistettava kliinisesti ennen hyödyntämistä hoitopäätöksissä.

## Käyttö

**Kliininen käyttö (Eclipse):**
1. Avaa potilas ja hoitosuunnitelma Eclipsessä.
2. Aja skripti `EQD2Viewer.App.esapi.dll`.

**Paikallinen kehitys ja testaus:**
1. Avaa kansio `02_Standalone_Runner`.
2. Käynnistä `EQD2Viewer.DevRunner.exe`.

**DIR-laskennan käyttö (vain Release-WithITK, Eclipse-ympäristössä):**
1. Avaa summaatio-dialogi ja rastita kaksi suunnitelmaa, merkitse toinen referenssiksi.
2. Klikkaa ei-referenssi-riville *Calculate DIR* — SimpleITK ajaa B-spline-rekisteröinnin (30 s – 3 min).
3. Tila muuttuu *"DIR calculated"* (vihreä) kun valmis. *"Compute summation"* käyttää DVF:ää annoksen mappaamisessa.
4. Isodoosi-paneelissa: **Auto from Dmax** -preset ja **Go to hotspot** -nappi auttavat löytämään summien kuumat pisteet.

## Kolmannen osapuolen lisenssit

Tämä ohjelmisto käyttää avoimen lähdekoodin kirjastoja. Yksityiskohtaiset tekijänoikeus- ja lisenssi-ilmoitukset löytyvät tiedostosta [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

| Kirjasto | Versio | Lisenssi | Käyttö |
|---|---|---|---|
| **SimpleITK** | 2.5.3 | Apache 2.0 | DIR-rekisteröinti (`Release-WithITK`) |
| **ITK (Insight Toolkit)** | 5.4.5 | Apache 2.0 | SimpleITK:n taustajärjestelmä |

SimpleITK ja ITK ovat valinnaisia ja ladataan vain `Release-WithITK`-konfiguraatiossa. Perus-`Release`-käännös ei sisällä eikä jaa kyseisiä kirjastoja.

## Versiohistoria

| Versio | Päivämäärä | Kuvaus |
|---|---|---|
| **0.9.3-beta** | 2026-04 | Isodose-UX uusiksi: Dmax-hotspot + "Go to hotspot" -navigointi, Auto-scale-preset Dmaxista, inline-muokattavat kynnysarvot, solo/delete-napit, per-rivin opacity, oikean-klikin värinvalinta. α/β-sliderin live-päivitys korjattu. DIR-napin diagnostiikka + logitus. Kattavat testit (+58) ja CI/CD-matriisi SimpleITK-cachella. |
| **0.9.2-beta** | 2026-04 | Deformable image registration SimpleITK:llä (valinnainen `Release-WithITK`-konfiguraatio). MHA/MHD DVF -tiedoston luku. B-spline DIR -summaatio. |
| **0.9.1-beta** | 2026-04 | Clean Architecture -uudistus. Erillinen DevRunner offline-kehitykseen, keskitetty BuildOutput-kansiointi ja paranneltu riippuvuuksien hallinta. |
| **0.9.0-beta** | 2026-03 | Beta-vaiheen julkaisu. Ominaisuuksien ja laskentalogiikan vakauttamista. |
| **0.3.0-alpha** | 2026-03 | Automaattinen `.esapi.dll`-päätteen lisääminen käännösvaiheessa (Assembly Name -päivitys projektitiedostoon). |
| **0.2.0-alpha** | 2026-03 | Yksikkötestit (107 kpl), ESAPI-stub-kirjasto CI-kääntämiseen, GitHub Actions -pipeline. |
| **0.1.0-alpha** | 2026-03 | Ensimmäinen alpha. CT/annos-näyttö, isodoosit, EQD2-muunnos, summaatio, DVH, rakennekohtainen α/β. |

## Tekijät
Risto Hirvilammi & Juho Ala-Myllymäki, ÖVPH

## Lisenssi
MIT — ks. `LICENSE.txt`
