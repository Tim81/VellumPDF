// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>Chapter 7: quick checks, the error code table and resetting the unit.</summary>
public static class Troubleshooting
{
    /// <summary>The anchor of the error code table, which spans several pages.</summary>
    public const string ErrorTableAnchor = "error-codes";

    /// <summary>The number of rows in the error code table, excluding the header row.</summary>
    public const int ErrorCodeCount = 37;

    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Troubleshooting");
        w.P("""
            Most problems have a simple cause. Work through the quick checks first. If the unit shows an
            error, find the code in the table in [[7.2]] and follow the remedy.
            """);

        w.Section("Quick checks");
        w.Bullets(
            "**The unit does not start.** Check the plug and the socket. Check that the top cover is closed " +
            "and that all three filters are fitted. A blinking **Filter** lamp means a filter is missing.",
            "**The air quality lamp stays red.** Check the room for a source: cooking, a candle, a freshly " +
            "painted wall. Open a window briefly, then close it and let the unit run on **High**.",
            "**The unit is loud.** Check that nothing blocks the inlet or the outlet and that the unit " +
            "stands on a hard, flat surface. A very dirty pre-filter also adds noise.",
            "**The airflow is weak.** Clean the pre-filter. If it is clean, check the age of the HEPA " +
            "filter in the app.",
            "**A smell comes from the outlet.** Replace the carbon filter. A new unit can smell faintly " +
            "of plastic during the first hours.",
            "**The app cannot find the unit.** Check that the phone and the unit are on the same network " +
            "and that the network is a 2.4 GHz one.");

        w.Section("Error codes");
        w.P("""
            An error shows as a repeating pattern on the lamp ring, and the app and the console show
            the code. The first digit is the part that failed: 1 for power, 2 for the fan, 3 for the
            sensors, 4 for the filters, 5 for the network and 6 for the firmware. Each remedy lists what
            you can do yourself. If it does not help, contact the shop where you bought the unit.
            """, keepWithNext: true);
        w.Table(
            [48, 150, 261],
            ["Code", "Meaning", "What to do"],
            ErrorRows(),
            codeColumn: 0,
            anchor: ErrorTableAnchor);
        w.P("""
            The header row of this table repeats on every page it covers.
            """);

        w.Section("Resetting the unit");
        w.P("""
            There are three levels of reset. Each one keeps more than the next, so start with the first.
            """, keepWithNext: true);
        w.Table(
            [100, 170, 189],
            ["Reset", "How", "What it clears"],
            [
                ["Restart", "Hold **Power** for three seconds.", "Nothing. The unit starts again with its settings."],
                ["Network reset", "Hold **Power** and **Timer** for ten seconds, or run\n`aerolith reset-network`.", "The Wi-Fi network and the pairing with the app. The schedule stays."],
                ["Factory reset", "Hold **Power**, **Mode** and **Lock** for fifteen seconds, or run\n`aerolith reset-all`.", "Every setting, the schedule, the history and the filter counters."],
            ]);
        w.Callout("CAUTION", """
            A factory reset also clears the filter counters. After it, set the counters again by hand
            (see [[6.3]]) or the unit will treat a worn filter as new.
            """);
    }

    private static IEnumerable<string[]> ErrorRows()
    {
        // The text of a cell holds a line feed where a line must break. The column is too narrow to
        // wrap a command such as `aerolith filter reset` without help.
        string[][] rows =
        [
            ["E101", "No mains power detected", "Check the plug and the socket.\nTry another socket."],
            ["E102", "Mains voltage too low", "Plug the unit in directly, without an extension.\nContact an electrician if it persists."],
            ["E103", "Mains voltage too high", "Unplug the unit at once and contact the shop."],
            ["E104", "Internal supply fault", "Unplug the unit for ten seconds and start it again.\nIf the code returns, contact the shop."],
            ["E105", "Brown-out during an update", "The unit restarted with the old firmware.\nRun the update again."],
            ["E201", "Fan does not start", "Open the cover and check that nothing blocks the fan.\nRemove any loose packaging."],
            ["E202", "Fan too slow", "Clean the pre-filter and the inlet.\nCheck that the HEPA filter is not clogged."],
            ["E203", "Fan too fast", "Restart the unit. If the code returns, contact the shop."],
            ["E204", "Fan bearing noise", "Contact the shop. Do not use the unit for long periods."],
            ["E205", "Fan blocked", "Unplug the unit and remove the object.\nStart the unit again."],
            ["E206", "Fan speed sensor fault", "Restart the unit. The unit runs, but Auto mode is off."],
            ["E301", "Particle sensor fault", "Clean the sensor windows (see [[6.4]]).\nRestart the unit."],
            ["E302", "Particle sensor dirty", "Clean the sensor windows.\nThe reading is unreliable until then."],
            ["E303", "Gas sensor fault", "Restart the unit and let it warm up for two minutes."],
            ["E304", "Gas sensor saturated", "Ventilate the room for ten minutes.\nThe sensor recovers by itself."],
            ["E305", "Temperature sensor fault", "Restart the unit. The unit runs, but the app shows no temperature."],
            ["E306", "Humidity sensor fault", "Restart the unit. The unit runs, but the app shows no humidity."],
            ["E307", "Sensor module not found", "Open the cover and check the cable on the module.\nContact the shop if it is loose."],
            ["E308", "Sensor calibration lost", "Leave the unit running for 24 hours in clean air.\nIt calibrates itself."],
            ["E401", "HEPA filter missing", "Fit the HEPA filter (see [[3.2]])."],
            ["E402", "Carbon filter missing", "Fit the carbon filter (see [[3.2]])."],
            ["E403", "Pre-filter missing", "Fit the pre-filter."],
            ["E404", "HEPA filter life over", "Replace the HEPA filter (see [[6.3]])."],
            ["E405", "Carbon filter life over", "Replace the carbon filter (see [[6.3]])."],
            ["E406", "Unknown filter type", "Use filters made for the AL-5.\nA filter from another model is refused."],
            ["E407", "Cover not closed", "Close the cover. It clicks into place."],
            ["E501", "Wi-Fi network not found", "Check that the router is on and that the network is 2.4 GHz."],
            ["E502", "Wi-Fi password refused", "Run the network setup again (see [[5.1]])."],
            ["E503", "No internet access", "The unit works locally.\nCheck the router and your internet connection."],
            ["E504", "App pairing lost", "Add the unit to the app again."],
            ["E505", "Clock not set", "Connect the unit once. It sets its clock from the network."],
            ["E601", "Firmware update failed", "Try the update again.\nIf it fails twice, load the file from the console."],
            ["E602", "Firmware signature invalid", "The file is damaged or not for this model.\nDownload it again."],
            ["E603", "Settings damaged", "The unit restored its default settings.\nSet it up again."],
            ["E604", "Schedule damaged", "The unit deleted its schedule.\nSet it up again in the app."],
            ["E605", "Console locked", "Too many wrong commands. Wait five minutes."],
            ["E606", "Storage full", "Run `aerolith history clear` in the console."],
        ];
        return rows;
    }
}
