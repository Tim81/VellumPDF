// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Samples.Manual;

/// <summary>The appendices: the console command reference, the glossary and the index.</summary>
public static class Appendices
{
    /// <summary>The anchor of the command reference table.</summary>
    public const string CommandTableAnchor = "command-reference";

    /// <summary>Adds the appendices.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        Commands(w);
        Glossary(w);
        Index(w);
    }

    private static void Commands(ManualWriter w)
    {
        w.Appendix("Console command reference");
        w.P("""
            This appendix describes every command of the service console. Commands are lower case.
            Arguments in angle brackets are required and arguments in square brackets are optional. A
            command that changes a setting prints the new value and, in brackets, the old one.
            """);

        w.Section("Status and settings");
        w.Table(
            [150, 309],
            ["Command", "Effect"],
            [
                ["aerolith status", "Prints the model, the firmware, the mode, the fan speed, the\nreadings, the filter life and the network state."],
                ["aerolith set mode <name>", "Selects a mode: auto, low, medium, high or night."],
                ["aerolith set timer <hours>", "Sets the timer to 0, 1, 2, 4 or 8 hours. Zero cancels it."],
                ["aerolith set report-interval <s>", "Sets how often the unit reports to the app, from 10 to 900 seconds."],
                ["aerolith set lock <on|off>", "Switches the child lock on or off."],
                ["aerolith set led <0-100>", "Sets the brightness of the lamps as a percentage."],
                ["aerolith schedule list", "Lists the stored switching times."],
                ["aerolith schedule add <spec>", "Adds a switching time, for example `mon-fri 07:30 medium`."],
                ["aerolith schedule delete <n>", "Deletes the switching time with the number n."],
            ],
            codeColumn: 0,
            anchor: Appendices.CommandTableAnchor);

        w.Section("Filters and history");
        w.Table(
            [150, 309],
            ["Command", "Effect"],
            [
                ["aerolith filter status", "Prints the remaining life of each filter in percent and in hours."],
                ["aerolith filter reset <name>", "Resets the counter of a filter: hepa, carbon or pre."],
                ["aerolith history show [days]", "Prints the stored readings of the last days, one line per hour."],
                ["aerolith history clear", "Deletes the stored readings and frees the storage."],
            ],
            codeColumn: 0);

        w.Section("Network and system");
        w.Table(
            [150, 309],
            ["Command", "Effect"],
            [
                ["aerolith reset-network", "Clears the Wi-Fi network and the app pairing. Other settings stay."],
                ["aerolith reset-all", "Returns the unit to its factory state. Asks for confirmation."],
                ["firmware load xmodem", "Waits for a firmware file sent with XMODEM-CRC."],
                ["firmware apply", "Installs a loaded file and restarts the unit."],
                ["help [command]", "Lists the commands, or describes one command."],
                ["exit", "Closes the console session."],
            ],
            codeColumn: 0);

        w.Section("Example: a weekday schedule");
        w.P("""
            The session below sets the unit to run at medium speed on weekday mornings and to switch to
            night mode at 22:30 every day. The columns in the listing are separated by tabs.
            """, keepWithNext: true);
        w.CommandBlock(
            "al5> aerolith schedule add mon-fri 07:30 medium\n" +
            "added\t#1\tmon-fri\t07:30\tmedium\n" +
            "al5> aerolith schedule add daily 22:30 night\n" +
            "added\t#2\tdaily\t22:30\tnight\n" +
            "al5> aerolith schedule list\n" +
            "#\tdays\ttime\tmode\n" +
            "1\tmon-fri\t07:30\tmedium\n" +
            "2\tdaily\t22:30\tnight");
    }

    private static void Glossary(ManualWriter w)
    {
        w.Appendix("Glossary");
        w.P("""
            These terms are used in the manual. Each is explained the way it is meant here.
            """, keepWithNext: true);
        w.Table(
            [110, 349],
            ["Term", "Meaning"],
            [
                ["CADR", "Clean air delivery rate. The volume of purified air a unit delivers per hour."],
                ["HEPA", "High-efficiency particulate air filter. An H13 filter holds back at least 99.95 percent of particles of 0.3 µm."],
                ["PM2.5", "Particles smaller than 2.5 µm. They go deep into the lungs. The unit reports them in µg/m³."],
                ["VOC", "Volatile organic compounds. Gases that evaporate from paint, cleaners and furniture."],
                ["Carbon filter", "A filter with activated carbon. It takes gases and smells from the air by adsorption."],
                ["Pre-filter", "A coarse mesh in front of the other filters. It is the only filter you can wash."],
                ["Standby", "The state in which the unit is plugged in and the fan is off, but the sensors and the network stay on."],
                ["Service port", "A serial connection in the base. It gives access to the console."],
                ["Console", "A text interface on the service port, with commands for settings the app lacks."],
                ["Firmware", "The program that runs inside the unit. Updates fix faults and add functions."],
            ]);
    }

    // The index is a placeholder: the Layout API collects no terms and reports no page numbers.
    private static void Index(ManualWriter w)
    {
        w.Appendix("Index", PageBreakMode.None);
        w.P("""
            Placeholder: an index needs the page of every term, which this edition cannot supply. The
            entries below are listed in alphabetical order with the page reference unknown.
            """, keepWithNext: true);
        foreach (var term in new[]
        {
            "app", "Auto mode", "carbon filter", "child lock", "console", "error codes", "factory reset",
            "firmware", "HEPA filter", "Night mode", "pre-filter", "privacy", "service port", "timer",
            "Wi-Fi",
        })
        {
            w.LeaderLine(term, ManualWriter.PagePlaceholder, w.Styles.Body);
        }
    }
}
