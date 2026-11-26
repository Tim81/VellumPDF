// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>Chapter 3: choosing a place, fitting the filters, connecting power, first start.</summary>
public static class Installation
{
    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Installation");
        w.P("""
            Setting up the AL-5 takes about ten minutes and needs no tools. This chapter walks through
            the place you choose, the filters, the power connection and the first start.
            """);

        w.Section("Choosing a location");
        w.P("""
            An air purifier works best when the air in the room can move freely past it. The best spot
            is a few steps away from where you sit or sleep, with open space around the unit. A corner
            or a gap behind a sofa makes the device less effective and louder, because the fan has to
            work harder to draw air in.
            """);
        w.P("""
            The AL-5 is rated for rooms of up to 40 m². In a larger room it still cleans the air, but
            it needs more time to reach the target. For an open-plan space, place the unit near the
            part of the room you use most.
            """);
        w.Bullets(
            "Leave at least 20 cm free behind and beside the unit, and 100 cm above it. [[2.4]] has the table.",
            "Use a flat, dry and stable surface. A floor or a low table is better than a shelf.",
            "Keep the unit away from radiators, stoves and direct sunlight. Heat disturbs the gas sensor.",
            "Do not place it in a bathroom or any room where it can get wet.");
        w.Callout("CAUTION", """
            Do not stand the unit on a soft surface such as a bed or a thick carpet. The base needs a
            firm surface so that the air can enter through the openings under the rim.
            """);

        w.Section("Fitting the filters");
        w.P("""
            The pre-filter and the carbon filter are fitted at the factory. You only have to add the
            HEPA filter. The unit will not start without it: a switch in the filter bay checks that all
            three filters are present.
            """);
        w.StepsWithNested(
            [
                "Unplug the unit if it is connected. Lift the top cover straight up. It is held by magnets.",
                "Take the HEPA filter out of its bag. Remove all packaging, including the foam strip on the rim.",
                "Slide the HEPA filter into the middle bay with the arrows on the frame pointing up.",
                "Check the other two filters. They must sit flat in their bays.",
                "Put the top cover back. It clicks into place when it is aligned.",
            ],
            nestedUnder: 0,
            "The cover has two pins at the back. When you lift it, tilt it forward slightly.",
            "Put the cover on a clean surface with the inside facing up.");
        w.P("""
            If the filter lamp stays lit after you close the cover, one of the filters is not seated
            correctly. Open the cover again and press each filter down until it is level with the frame.
            """);

        w.Section("Connecting the power");
        w.P("""
            Connect the mains cable to the inlet in the base, then plug it into a wall socket. The ring
            around the power button glows white for two seconds and the fan makes a short test run. The
            unit then waits in standby.
            """);
        w.P("""
            The AL-5 uses less than 1 W in standby. You can leave it plugged in. To switch it off
            completely, remove the plug from the socket.
            """);

        w.Section("First start");
        w.P("""
            The first start sets the language of the voice prompts and sets up the sensor. Press the
            **Power** button once. The unit begins in **Auto** mode and warms up the sensors for two
            minutes. During this time the **Air quality** lamp blinks.
            """);
        w.Steps(
            "Press **Power** to start the unit. Wait for the **Air quality** lamp to stop blinking.",
            "Press **Mode** once to leave Auto mode and look at the fan speeds. Press it again to return.",
            "Hold **Timer** for three seconds to set the language of the prompts. The lamp shows the choice.",
            "Join a network if you want to use the app. See chapter 5.");
        w.P("""
            The sensor needs about a day to adapt to the room. During that time its readings can be
            slightly off, and Auto mode may run a little faster than it will later.
            """);
        w.Callout("NOTE", """
            The first start also checks the filter life counters. A new HEPA filter starts at 100 percent.
            If you fit a used filter on purpose, the counter shows the wrong value. Reset it as explained
            in [[6.3]].
            """);
    }
}
