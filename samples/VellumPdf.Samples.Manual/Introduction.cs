// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Chapter 1: what the manual covers, its conventions and the parts of the purifier.
/// </summary>
public static class Introduction
{
    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Introduction");

        w.Section("About this manual");
        w.P("""
            This manual explains how to set up, use and look after the **Aerolith AL-5**, a room air
            purifier with a three-stage filter and a built-in air quality sensor. It is written for
            the person who owns the device. It is not a service manual.
            """);
        w.P("""
            Read chapter 2 before you plug the purifier in. It is written in Dutch because the device
            is sold with Dutch safety text in the Netherlands and Belgium. The English summary at the
            start of that chapter lists the same warnings in one place.
            """);
        w.P("""
            The manual follows the order in which you will use the device. Chapter 3 covers
            installation and the first start. Chapter 4 covers daily use. Chapter 5 explains how to
            join the device to a Wi-Fi network and how to reach the service console. Chapter 6 covers
            care and filter changes, chapter 7 lists what to do when something goes wrong, and
            chapter 8 gives the technical data and the warranty terms.
            """);
        w.Callout("NOTE", """
            Everything in this manual is invented. There is no Aerolith company, no AL-5 purifier and
            no support line. The document exists to exercise a PDF library.
            """);
        w.Placeholder("""
            Boxes like the NOTE above are one-cell tables, because the Layout API has no shaded
            paragraph. A screen reader announces a table, and the label cannot be bold.
            """);

        w.Section("Conventions");
        w.P("""
            The manual uses a few typographic conventions. They are applied the same way in every
            chapter, so a quick look at the page tells you what kind of text you are reading.
            """, keepWithNext: true);
        w.Bullets(
            "**Bold text** is the name of a button, a menu or a lamp on the device or in the app.",
            "*Italic text* marks a term the first time it is explained, or a title.",
            "`Typewriter text` is a command you type into the service console, or a value you read from it.",
            "A shaded **NOTE** box holds background information that is useful but not essential.",
            "A shaded **CAUTION** box is about something that can damage the device or its filters.",
            "A shaded **WARNING** box is about a risk of injury. Never skip these.",
            "A reference such as [[6.2]] points to another part of the manual. This edition cannot fill in " +
            "the page number, so it shows " + ManualWriter.PagePlaceholder + " in its place.");
        w.P("""
            Measurements are metric. Where a figure depends on the room, the manual says which room
            size it assumes. Times are given in 24-hour notation.
            """);

        w.Section("What is in the box");
        w.P("Check that the box holds the following parts. If something is missing, keep the packaging " +
            "and contact the shop where you bought the device.", keepWithNext: true);
        w.Bullets(
            "The Aerolith AL-5 main unit, with the pre-filter and the carbon filter already fitted.",
            "One H13 HEPA filter in a sealed bag. Take it out of the bag only at the moment you fit it.",
            "A mains cable of 1.8 m with a plug for your country.",
            "A remote control with two AAA batteries.",
            "This manual, and a one-page quick start guide.",
            "A serial service adapter. You need it only for the service console, [[5.3]], and only if you " +
            "want to use it.");

        w.Section("Product overview");
        w.P("""
            The AL-5 draws air in at the sides, pushes it through three filters from bottom to top and
            blows it out of the round outlet on the top cover. A fan and a sensor module sit between
            the filters and the outlet. The legend below names the layers.
            """);
        w.Sub("Legend to figure 1, on the next page");
        w.Table(
            [38, 120, 301],
            ["Layer", "Part", "What it does"],
            [
                ["1", "Top cover", "Holds the round outlet and the control panel. It lifts off for filter changes."],
                ["2", "Fan and sensor module", "Moves the air. The particle sensor and the gas sensor sit in the two windows."],
                ["3", "HEPA filter", "Holds back particles down to 0.3 µm. Replace it about every twelve months."],
                ["4", "Carbon filter", "Takes out smoke, cooking smells and some gases. Replace it every six months."],
                ["5", "Pre-filter", "A washable mesh that catches hair and coarse dust before it reaches the other filters."],
                ["6", "Base", "Carries the unit and holds the mains inlet, the service port and the rubber feet."],
            ]);
        w.Placeholder("""
            Figure 1 has no caption. The image is scaled to fill a whole page, so nothing fits under it,
            and the Layout API has no caption element that stays with a figure.
            """);
        w.P("""
            Figure 1, on the next page, shows the unit with its layers pulled apart. The picture is scaled
            so that it fills a page exactly, and the numbers above are the layers from the top down.
            """);
        w.FigurePage(
            DiagramImage.Png(),
            "Exploded view of the AL-5. From the top: the cover with the round air outlet, the fan and " +
            "sensor module, the HEPA filter, the carbon filter, the pre-filter and the base.",
            "fig-exploded-view");
    }
}
