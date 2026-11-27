// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>
/// Chapter 4: the control panel, the modes, the timer, night mode and the child lock.
/// </summary>
public static class DailyUse
{
    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Daily use");
        w.P("""
            In daily use the AL-5 looks after itself. It measures the air, chooses a fan speed and
            reports its state with one lamp. This chapter explains the controls for the times you
            want to take over.
            """);

        w.Section("The control panel");
        w.P("""
            The control panel is on the top cover, to the left of the outlet. It has four buttons and
            four lamps. The buttons respond to a short press, and some have a second function on a long
            press of three seconds.
            """, keepWithNext: true);
        w.Table(
            [80, 90, 289],
            ["Control", "Press", "Function"],
            [
                ["**Power**", "Short", "Switches the unit on, or puts it into standby."],
                ["**Power**", "Long", "Restarts the unit. The restart keeps all settings."],
                ["**Mode**", "Short", "Steps through Auto, Low, Medium, High and Night."],
                ["**Mode**", "Long", "Switches the unit between Auto and the last manual speed."],
                ["**Timer**", "Short", "Steps through 1, 2, 4 and 8 hours, then off."],
                ["**Timer**", "Long", "Sets the language of the voice prompts."],
                ["**Lock**", "Short", "Does nothing while the lock is off. Shows the lock state."],
                ["**Lock**", "Long", "Switches the child lock on or off."],
            ]);
        w.Sub("The lamps");
        w.Table(
            [100, 110, 249],
            ["Lamp", "Colour", "Meaning"],
            [
                ["**Air quality**", "Blue", "The air is clean. Particles stay below 12 µg/m³."],
                ["**Air quality**", "Yellow", "The air is moderate. The fan speeds up."],
                ["**Air quality**", "Red", "The air is poor. The fan runs at its highest Auto speed."],
                ["**Filter**", "White, steady", "A filter has less than 10 percent of its life left."],
                ["**Filter**", "White, blinking", "A filter is missing or not seated. The unit will not start."],
                ["**Network**", "Green", "The unit is connected and is reporting to the app."],
                ["**Network**", "Green, blinking", "The unit is looking for a network or has lost its connection."],
            ]);

        w.Section("Operating modes");
        w.P("""
            The AL-5 has five modes. You select them with the **Mode** button, from the app or from the
            remote control. The mode stays set after a power cut.
            """, keepWithNext: true);
        w.Table(
            [70, 110, 90, 189],
            ["Mode", "Fan speed", "Noise", "Use it when"],
            [
                ["**Auto**", "Follows the sensor", "20 to 52 dB(A)", "You want the unit to look after the room and do not think about it."],
                ["**Low**", "Fixed at 25 percent", "28 dB(A)", "You want quiet background cleaning."],
                ["**Medium**", "Fixed at 55 percent", "41 dB(A)", "You are cooking or have visitors."],
                ["**High**", "Fixed at 100 percent", "52 dB(A)", "You want to clear a room quickly, for example after painting."],
                ["**Night**", "At most 20 percent", "20 dB(A)", "You sleep in the room. The lamps dim to 10 percent."],
            ]);
        w.P("""
            In **Auto** mode the fan speed depends on the particle reading and the gas reading, whichever
            is worse. When both readings fall back to the blue band, the fan slows down and runs at a low
            speed for ten minutes before it pauses. It starts again as soon as a reading rises.
            """, index: ["Auto mode"]);

        w.Section("The timer", index: ["timer"]);
        w.P("""
            The timer switches the unit to standby after the chosen time. It works in every mode. Press
            **Timer** to step through one, two, four and eight hours. The lamp ring shows the choice by
            lighting a quarter, half, three quarters or the whole ring. The remaining time is shown in
            the app.
            """);
        w.P("""
            A timer is cancelled by a power cut. If you want a daily schedule that survives a cut, set it
            up in the app or with the console command `aerolith schedule`.
            """);

        w.Section("Night mode", index: ["Night mode"]);
        w.P("""
            Night mode runs the fan at its lowest useful speed, at most 20 percent, and dims every lamp.
            It keeps measuring. If the air quality becomes poor while the room is occupied, the unit raises
            the speed for as long as needed and returns to the night speed afterwards.
            """);
        w.Callout("NOTE", """
            Night mode does not make the unit silent. At 20 dB(A) the sound is comparable to a quiet
            whisper at one metre distance.
            """);

        w.Section("Child lock", index: ["child lock"]);
        w.P("""
            With the child lock on, the buttons on the unit do nothing. The remote control and the app keep
            working, so you stay in control. Hold **Lock** for three seconds to switch the lock on. The
            lamp above the button lights up. Hold it again for three seconds to switch the lock off.
            """);
        w.P("""
            The lock stays on after a power cut. If you cannot switch it off because the unit is in a
            state you do not recognise, restart the unit by unplugging it for ten seconds. The lock
            survives, but the unit accepts the **Lock** long press again.
            """);
    }
}
