# VellumPdf: poppler "Internal Error: xref num … not found" bij inline Standard-14 fontdicts

> **OPGELOST in VellumPdf.Kernel 1.8.2** (PR #149, 2026-07-05). `PdfDocument.Save` schrijft
> Standard-14 fontdicts nu als indirecte objecten: één object per uniek font per document,
> gedeeld door alle pagina's (via de bestaande `PdfPage.RegisterFontRef`-seam; het directe
> `RegisterFont`-pad is verwijderd). A/B geverifieerd met pdffonts op poppler 26.02.0:
> foutmelding weg, nette object-ID's. De ExtGState-suggestie hieronder is bewust niet
> meegenomen (poppler verwerkt die correct); dat blijft een optionele optimalisatie.

**Datum onderzoek:** 2026-07-05 · **Getroffen versies:** VellumPdf.Kernel 1.7.6 t/m 1.8.1 (identiek gedrag)
**Ernst:** laag (cosmetisch/interop) — de PDF is spec-conform en rendert overal correct; alleen
poppler-tools loggen een interne fout en vallen terug op xref-reconstructie.

## Symptoom

Elke door VellumPdf geproduceerde pagina die Standard-14-fonts gebruikt triggert in
poppler-gebaseerde tools (`pdffonts`, `pdftoppm`, Okular, Evince, GNOME-preview):

```
Internal Error: xref num 91013412 not found but needed, try to reconstruct
```

Het getal is per poppler-build constant (ongeïnitialiseerde/sentinel `Ref`), onafhankelijk
van de documentinhoud. PDFium, Acrobat en browsers renderen zonder klachten. In de
`pdffonts`-uitvoer staan de Standard-14-fonts met object-ID **`[none]`** — dat is de vingerafdruk.

## Diagnose (uitputtend geverifieerd op een echt exportbestand)

Achtereenvolgens uitgesloten met byte-level inspectie:

1. **Xref-tabel zelf**: alle entries exact 20 bytes (`nnnnnnnnnn ggggg n\r\n`), subsectie-header
   correct, `startxref` klopt — ✔ geldig.
2. **Alle 20 offsets gevalideerd**: elk offset wijst exact op zijn `N 0 obj`-header — ✔ geldig.
3. **`/Length` van elke stream** vs. werkelijke afstand tot `endstream`: overal diff = 1
   (de toegestane EOL vóór `endstream`) — ✔ geldig.
4. **Het spookgetal `91013412` komt nergens in het bestand voor** (ook geen misvormde
   indirecte referenties met ontbrekende spaties) — het wordt door poppler zelf gefabriceerd.

**Oorzaak:** VellumPdf schrijft Standard-14-fontdicts **inline (direct)** in de page-resources:

```
/Resources <<
  /Font <<
    /F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>   % ← direct dict
    /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>        % ← direct dict
    /TT1 14 0 R
    /TT2 19 0 R
  >>
>>
```

Dat is ISO 32000-conform (een fontdict hoeft geen indirect object te zijn), maar poppler's
`FontInfoScanner` administreert fonts op hun indirecte `Ref`; voor een direct dict is die
Ref ongedefinieerd en probeert poppler alsnog `xref->fetch()` op een garbage-objectnummer →
de "Internal Error"-regel + defensieve xref-reconstructie.

## Bewijs

Chirurgische patch op een echt exportbestand: uitsluitend de twee inline dicts vervangen
door `/F2 21 0 R` / `/F1 22 0 R` + dezelfde dicts als objecten 21/22 toegevoegd + xref
geregenereerd. Resultaat: **foutmelding volledig weg**, `pdffonts` toont nette object-ID's:

```
name            type    ...  object ID
Helvetica-Bold  Type 1  ...     21  0
Helvetica       Type 1  ...     22  0
```

(Zelfde document, zelfde content streams, zelfde embedded fonts — alleen de fontdicts indirect.)

## Aanbevolen fix in VellumPdf.Kernel

Schrijf fontdictionary's (in elk geval de Standard-14-resources uit `UseFont`) als
**indirecte objecten** en verwijs ernaar vanuit `/Resources /Font` — de conventie die alle
grote producers volgen en die door ISO 32000-2 wordt aangeraden voor gedeelde resources.
Bonus: bij meerdere pagina's scheelt het duplicatie (nu wordt de inline dict vermoedelijk
per pagina herhaald).

Overweeg hetzelfde voor de inline `/ExtGState`-dicts (GS1…GSn): poppler verwerkt die wel
correct, maar indirecte, gededupliceerde ExtGState-objecten zijn compacter bij
multi-page documenten.

## Repro binnen Celestium

```bash
# genereer een dashboard-export (Vellum) en inspecteer:
pdffonts test4_dashboard.pdf        # → Internal Error + [none]-object-IDs
```
