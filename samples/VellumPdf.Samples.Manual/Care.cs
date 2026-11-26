// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Chapter 6: the maintenance schedule, filter changes, sensor cleaning and storage.
/// </summary>
public static class Care
{
    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Care and maintenance");
        w.P("""
            An air purifier is only as good as its filters. The AL-5 counts the running hours of each
            filter and tells you when one is due. This chapter lists the jobs, explains how to do them
            and says what happens if you skip them.
            """);

        w.Section("Maintenance schedule");
        w.P("""
            The list below shows how often each job comes round in a normal household. If you smoke
            indoors, keep pets or live near a busy road, expect to replace the filters sooner. The
            filter lamp and the app tell you when the time has come.
            """, keepWithNext: true);
        var style = w.Styles.Body;
        foreach (var (job, interval) in new[]
        {
            ("Shake out and vacuum the pre-filter", "every 2 weeks"),
            ("Wash the pre-filter and let it dry", "every 3 months"),
            ("Wipe the air quality sensor windows", "every 3 months"),
            ("Wipe the outside of the unit", "when needed"),
            ("Replace the carbon filter", "every 6 months"),
            ("Replace the HEPA filter", "every 12 months"),
            ("Replace the pre-filter", "every 3 years"),
        })
        {
            w.LeaderLine(job, interval, style);
        }

        w.P("""
            The intervals assume about twelve hours of use a day. The unit counts hours of fan time, not
            calendar time, so a unit that runs less lasts longer.
            """);

        w.Section("Cleaning the pre-filter");
        w.P("""
            The pre-filter is the grey mesh at the bottom of the unit. It catches hair and coarse dust
            and keeps the other filters clean for longer. Clean it every two weeks, or every week if you
            have pets.
            """);
        w.Steps(
            "Unplug the unit and tilt it back to reach the pre-filter latch.",
            "Pull the pre-filter out. Shake it over a bin.",
            "Remove what is left with a vacuum cleaner and a soft brush.",
            "Every third month, wash it in lukewarm water with a drop of mild soap. Rinse it well.",
            "Let it dry completely before you put it back. A damp filter grows mould.");
        w.Callout("CAUTION", """
            Never wash the HEPA filter or the carbon filter. Water ruins them. The pre-filter is the only
            filter that can be washed.
            """);

        w.Section("Replacing the HEPA and carbon filters");
        w.P("""
            When the **Filter** lamp turns steady white, one of the filters has less than ten percent of
            its life left. The app says which one. Order a replacement now, because the unit continues to
            work at reduced effect until the filter is spent.
            """);
        w.Steps(
            "Unplug the unit and lift off the top cover.",
            "Take out the filter that is due. Hold it by the frame and keep it upright to avoid spilling dust.",
            "Put the old filter straight into a bag and close the bag.",
            "Fit the new filter with the arrows on the frame pointing up.",
            "Close the cover and plug in the unit.",
            "Hold **Timer** and **Lock** together for five seconds to reset the counter of the new filter.");
        w.P("""
            The lamp ring shows a short animation when the counter has been reset. If you changed the HEPA
            and the carbon filter in the same visit, reset both: the first press resets the HEPA counter
            and the second press the carbon counter. In the console you can do the same with
            `aerolith filter reset hepa` and `aerolith filter reset carbon`.
            """);

        w.Section("Cleaning the sensors");
        w.P("""
            The air quality sensor has two windows on the side of the fan module. Dust that settles on
            them makes the readings drift upward, so the unit runs faster than it should. Clean the windows
            every three months.
            """);
        w.P("""
            Open the top cover, and use a cotton swab slightly dampened with clean water. Wipe the windows
            gently and dry them with a second swab. Do not use alcohol or solvents. Do not blow into the
            module, and do not use compressed air, which can damage the sensor.
            """);

        w.Section("Cleaning the housing");
        w.P("""
            Wipe the outside with a soft, dry cloth. For a stain, use a cloth that is barely damp. Do not
            spray liquid onto the unit and do not let water run into the openings.
            """);

        w.Section("Storing the unit");
        w.P("""
            If you do not use the unit for more than a month, take the filters out and put each in its own
            plastic bag. Store the unit upright in a dry place, in its box if you still have it. The filters
            lose their effect in damp air, so keep them in a dry place as well.
            """);
        w.P("""
            When you take the unit back into use, check that the filters are dry and have no musty smell.
            Replace a filter that smells of mould. Then follow the first start in [[3.4]].
            """);
    }
}
