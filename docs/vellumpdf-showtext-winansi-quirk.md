# VellumPdf: ShowText verminkt geldige WinAnsi-tekens op Standard-14-fonts

> **OPGELOST in VellumPdf 1.9.0** (PR #150, 2026-07-06). Standard-14 fontdicts declareren nu
> `/Encoding /WinAnsiEncoding` (behalve Symbol/ZapfDingbats), `ShowText` codeert naar WinAnsi in
> plaats van Latin-1, en de Standard14-breedtetabellen zijn opnieuw gegenereerd uit de Adobe
> Core-14 AFM's op WinAnsi. AcroForm-tekstvelden renderen de 0x80–0x9F-interpunctie nu ook.
> Tekens buiten WinAnsi worden `?` en gemeld via `TextEncodingWarnings`.

**Datum onderzoek:** 2026-07-05 · **Getroffen versies:** VellumPdf.Kernel t/m 1.8.2 (opgelost in 1.9.0)
**Ernst:** middel (zichtbare tekstfouten in elk document dat Standard-14 gebruikt voor
niet-ASCII-tekens) — geen crash, PDF blijft geldig; de tekens zijn simpelweg fout of afwezig.

## Symptoom

Tekst die via `ShowText` op een Standard-14-font (Helvetica/Helvetica-Bold) wordt gezet,
verliest of vervormt tekens die **wel degelijk in WinAnsi/CP1252 bestaan**:

| Teken | Codepunt | CP1252-byte | Werkelijk gerenderd |
|---|---|---|---|
| `°` (gradenteken) | U+00B0 | 0xB0 | **volledig weggelaten** — "15°46'" wordt "1546'" |
| `•` (bullet) | U+2022 | 0x95 | **"."** — "Placidus • Geboortekaart" wordt "Placidus . Geboortekaart" |
| `–` (en-dash) | U+2013 | 0x96 | **kale ASCII-hyphen "-"** (gemeten via probe-project + pdftotext) |
| `—` (em-dash) | U+2014 | 0x97 | **kale ASCII-hyphen "-"** (idem) |

Tekens die écht buiten CP1252 vallen (℞ U+211E, ★ U+2605, ✕ U+2715, astrologische
symbolen, PUA-glyphs) renderen als `"?"` — dát is verdedigbaar gedrag; deze quirk gaat
uitsluitend over de tekens die WinAnsi wél dekt.

## Diagnose (empirisch, Celestium-portret-export)

1. **`°` afwezig bevestigd via `pdftotext` op de contentstream**: het teken staat niet in
   de PDF-bytes — het wordt door de writer weggelaten, niet door de viewer verkeerd
   getoond (dus geen encoding-declaratieprobleem in de viewer, maar een writer-probleem).
2. **`•` → "." bevestigd**: na omleiding van de voettekst naar een embedded font bevat de
   PDF wél echt U+2022 — de Standard-14-route substitueerde hem dus actief.
3. Het patroon (substitutie naar een ASCII-benadering waar mogelijk, weglaten waar geen
   benadering bestaat) wijst op een **ASCII-transliteratie-fallback** in de
   ShowText-encoder in plaats van een correcte CP1252-byte-mapping.

## Vermoedelijke oorzaak

`ShowText` encodeert de string vermoedelijk als ASCII (met transliteratie/drop voor
codepunten > 0x7E) in plaats van als CP1252-bytes. Voor Standard-14-fonts met
`/Encoding /WinAnsiEncoding` in de fontdict is de correcte aanpak: elk codepunt dat in
CP1252 bestaat naar zijn CP1252-byte mappen (incl. het 0x80–0x9F-venster waar •/–/—/…
wonen) en alléén voor tekens buiten CP1252 een fallback/warning gebruiken.

## Aanbevolen fix in VellumPdf.Kernel

1. ShowText-encoding voor Standard-14: volledige CP1252-mapping (System.Text
   `Encoding.GetEncoding(1252)` met een custom fallback is in .NET beschikbaar via
   `CodePagesEncodingProvider`).
2. Zorg dat de fontdict `/Encoding /WinAnsiEncoding` declareert (te controleren — als
   die ontbreekt is de default StandardEncoding, dat 0x80–0x9F níét dekt en 0xB0 anders
   invult; dat zou het °-gedrag zelfs kunnen verklaren).
3. Warning (zoals bij ontbrekende glyphs elders) voor tekens buiten CP1252 i.p.v. stil
   transliteren.

## Workarounds in Celestium (te verwijderen ná de upstream-fix)

Alle in `src/Astrologie.Desktop/VellumPdfExportService.cs`, patroon: cel leeg doorgeven
aan `TekenTabelRij` en apart tekenen via `TekenGemengdeTekst` (DejaVu-embedded keten):

- Gradencellen (Posities/Huizen/Aspecten-orb/Progressies/Halfsommen/Heerschap/
  Digniteiten) + `FormatGraadMinuutSeconde`-helper
- Metadata-regels (`TekenKlein`) en voettekst (bullet)
- H2-sectiekoppen: gesplitst in vet WinAnsi-veilig voorvoegsel (Helvetica-Bold
  `ShowText`) + rest via de gemengde route (regulier gewicht — bewuste concessie)
- Dash-dragende cellen (Ages of Man `Jaren`, Levenslijn `Label`, Aspectpatronen
  `PuntenTekst`, receptie-regels, sectietitels met "— {naam}")

De routing van échte niet-WinAnsi-glyphs (astro-symbolen, ℞, ★, PUA) via
`TekenGemengdeTekst` blijft ook ná de fix nodig.

## Repro

```csharp
// Minimale repro: Standard-14 Helvetica + ShowText met CP1252-tekens
canvas.BeginText().SetFont(helvetica, 10)
      .SetTextMatrix(1, 0, 0, 1, 50, 700)
      .ShowText("15°46' • en–dash em—dash")
      .EndText();
// pdftotext → "1546' . en dash em dash" (of hyphen-varianten): °/•/–/— niet intact
```

## Meting dash-faalmodus (2026-07-05)

Standalone probe-project tegen VellumPdf.Kernel/Fonts.Standard14 1.8.2: tekst via kale
`ShowText` + `Standard14.HelveticaBold`, teruggelezen met `pdftotext`. Resultaat:
**en-dash (U+2013) en em-dash (U+2014) renderen beide als kale ASCII-hyphen `-`** — een
derde faalmodus naast `°` (volledig weg) en `•` (wordt `.`). Drie verschillende
uitkomsten voor drie CP1252-tekens versterkt de transliteratie-hypothese in §Oorzaak.
Regressietest in Celestium: `GenereerBytes_TijdsherenAgesOfManJaren_BevatEchteEnDashInPdfTekst`
(pdftotext-gebaseerd; skipt zonder poppler).

## Openstaand

- Fontdict-inspectie is inmiddels gedaan: de `/Encoding`-entry ontbrak inderdaad, dus
  beide fixes uit §Aanbevolen waren nodig. Root cause bevestigd en opgelost in 1.9.0 —
  zie de OPGELOST-banner boven aan dit document.
