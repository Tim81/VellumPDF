// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Chapter 2, the safety instructions, in Dutch. Every heading, paragraph, list item and table
/// cell in it carries the language tag <c>nl-NL</c>, so a screen reader switches voice and a
/// text extractor can tell the passage apart.
/// </summary>
public static class Safety
{
    /// <summary>The language tag of the chapter.</summary>
    public const string Dutch = "nl-NL";

    /// <summary>
    /// The text of the first Dutch paragraph, which tests look for in the saved file.
    /// </summary>
    public const string FirstSentence =
        "Lees deze veiligheidsinstructies zorgvuldig voordat u de Aerolith AL-5 in gebruik neemt.";

    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Veiligheidsinstructies", Dutch);

        w.Callout("SUMMARY", """
            This chapter is in Dutch. In short: use the purifier indoors only, on a flat surface, with
            at least 20 cm of free space on every side. Unplug it before you open it. Do not use it with a
            damaged cable, do not let it get wet, and keep children away from the filters.
            """);

        w.Section("Lees dit eerst", Dutch);
        w.P($"""
            {Safety.FirstSentence} Bewaar de handleiding, zodat u er later iets in kunt opzoeken en zodat
            een volgende gebruiker haar ook kan lezen. Het apparaat is bedoeld voor gebruik binnenshuis in
            een woonomgeving. Gebruik het niet voor andere doeleinden dan in deze handleiding staan
            beschreven.
            """, Dutch);
        w.P("""
            Deze handleiding onderscheidt drie soorten aanwijzingen. Een **opmerking** geeft
            achtergrondinformatie. Een **let op** wijst op een handeling die het apparaat of de filters kan
            beschadigen. Een **waarschuwing** gaat over een risico op letsel. Sla een waarschuwing nooit
            over.
            """, Dutch);

        w.Section("Waarschuwingen", Dutch);
        w.P("Volg altijd de onderstaande regels. Ze gelden voor elke situatie waarin u het apparaat " +
            "gebruikt.", Dutch, keepWithNext: true);
        w.BulletsIn(Dutch,
            "Gebruik het apparaat niet met een beschadigd netsnoer of een beschadigde stekker.",
            "Steek geen voorwerpen in de luchtinlaat of in de uitlaat. Dat kan een elektrische schok " +
            "veroorzaken en de ventilator beschadigen.",
            "Zet geen vloeistoffen op het apparaat en gebruik het niet in een ruimte waar het nat kan " +
            "worden, zoals een badkamer of een wasruimte.",
            "Haal altijd de stekker uit het stopcontact voordat u een filter vervangt of het apparaat " +
            "reinigt.",
            "Laat kinderen niet met het apparaat spelen. Kinderen mogen het apparaat niet zonder " +
            "toezicht reinigen of onderhouden.",
            "Dek het apparaat nooit af terwijl het aan staat. Een afgedekt apparaat kan oververhit raken.",
            "Gebruik het apparaat niet in de buurt van ontvlambare gassen, spuitbussen of dampen van " +
            "oplosmiddelen.");
        w.Callout("WAARSCHUWING", """
            Probeer het apparaat nooit zelf te repareren. Als de behuizing, het netsnoer of de
            ventilator beschadigd is, haal dan de stekker uit het stopcontact en neem contact op met de
            winkel waar u het apparaat hebt gekocht.
            """, Dutch);

        w.Section("Elektrische veiligheid", Dutch);
        w.P("""
            De Aerolith AL-5 werkt op een netspanning van 220 tot 240 volt en een frequentie van 50 hertz.
            Sluit het apparaat alleen aan op een geaard stopcontact dat voor deze spanning geschikt is. Het
            vermogen is laag, maar het stopcontact moet wel tijdens het gebruik goed bereikbaar blijven,
            zodat u de stekker snel kunt verwijderen.
            """, Dutch);
        w.P("""
            Gebruik bij voorkeur geen verlengsnoer. Is dat niet te vermijden, kies dan een snoer dat
            minstens 6 ampère aankan en rol het volledig af, zodat het niet warm wordt. Leg het netsnoer
            zo neer dat niemand erover kan struikelen en dat het niet bekneld raakt onder meubels.
            """, Dutch);
        w.P("""
            Trek altijd aan de stekker en nooit aan het snoer als u het apparaat loskoppelt. Raak de
            stekker niet aan met natte handen.
            """, Dutch);

        w.Section("Plaatsing", Dutch);
        w.P("""
            Zet de purifier op een vlakke, stabiele en droge ondergrond. Het apparaat zuigt de lucht aan
            via de zijkanten en blaast haar naar boven weg. Houd daarom rondom voldoende ruimte vrij. De
            tabel geeft de minimale afstanden.
            """, Dutch, keepWithNext: true);
        w.Table(
            [120, 90, 249],
            ["Zijde", "Afstand", "Reden"],
            [
                ["Achterkant en zijkanten", "20 cm", "De luchtinlaat moet vrij blijven."],
                ["Bovenkant", "100 cm", "De uitgeblazen lucht moet zich kunnen verspreiden."],
                ["Gordijnen en kleding", "50 cm", "Stof en vezels kunnen worden aangezogen."],
                ["Warmtebronnen", "100 cm", "Radiatoren en kachels verstoren de sensor."],
            ],
            language: Dutch);
        w.P("""
            Plaats het apparaat niet in direct zonlicht en niet vlak naast een raam dat vaak openstaat.
            De luchtkwaliteitssensor reageert dan op de buitenlucht en niet op de lucht in de kamer.
            """, Dutch);

        w.Section("Kinderen en huisdieren", Dutch);
        w.P("""
            Het apparaat is geen speelgoed. De uitlaat is zo ontworpen dat vingers er niet
            gemakkelijk in kunnen, maar toezicht blijft nodig bij jonge kinderen. Schakel de
            kinderbeveiliging in als kinderen bij het bedieningspaneel kunnen komen. In hoofdstuk 4 staat
            hoe dat werkt, in [[nl:4.5]].
            """, Dutch);
        w.P("""
            Huisdieren veroorzaken haar en huidschilfers. Het voorfilter vangt het grootste deel op. Maak
            het voorfilter vaker schoon als er huisdieren in huis zijn, bijvoorbeeld elke week.
            """, Dutch);

        w.Section("Symbolen op het apparaat", Dutch);
        w.Table(
            [140, 319],
            ["Symbool", "Betekenis"],
            [
                ["Doorgestreepte afvalbak", "Gooi het apparaat niet bij het gewone huisvuil. Zie de paragraaf over afvoer."],
                ["Boek met een i", "Lees de handleiding voordat u het apparaat gebruikt."],
                ["Dubbel vierkant", "Beschermingsklasse II: het apparaat heeft dubbele isolatie en geen aardingsdraad."],
                ["CE", "Het apparaat voldoet aan de Europese richtlijnen die voor dit product gelden."],
            ],
            language: Dutch);

        w.Section("Afvoer", Dutch);
        w.P("""
            Gooi het apparaat aan het einde van zijn levensduur niet bij het gewone huisvuil. Lever het in
            bij een inzamelpunt voor elektrische en elektronische apparatuur, of breng het terug naar de
            winkel als u een nieuw apparaat koopt. Zo kunnen schadelijke stoffen worden verwijderd en
            kunnen de materialen worden hergebruikt.
            """, Dutch);
        w.P("""
            Gebruikte filters mogen bij het restafval. Het HEPA-filter en het koolfilter bevatten geen
            stoffen die apart ingezameld moeten worden. Laat de filters wel eerst droog worden en doe ze
            in een dichte zak, zodat het stof niet vrijkomt.
            """, Dutch);
    }
}
