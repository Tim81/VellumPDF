// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>Chapter 5: Wi-Fi, the companion app, the service console and firmware updates.</summary>
public static class Network
{
    /// <summary>The anchor of the first console block, so a test can check it resolves.</summary>
    public const string ConsoleAnchor = "console-session";

    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Network and service console");
        w.P("""
            The AL-5 works without a network. A connection adds three things: control from the app, a
            history of the air quality in your home, and automatic firmware updates. This chapter shows
            how to connect the device and how to use the service console for settings that the app does
            not offer.
            """);

        w.Section("Joining a Wi-Fi network");
        w.P("""
            The unit supports 2.4 GHz networks with WPA2 or WPA3. It cannot join a 5 GHz network or a
            network that asks for a login on a web page, such as a hotel network.
            """);
        w.Steps(
            "Install the Aerolith app on your phone and make sure the phone is on your home network.",
            "Hold **Power** and **Timer** together for five seconds. The **Network** lamp blinks green.",
            "Open the app and choose **Add device**. The app finds the unit and asks for the network password.",
            "Wait up to one minute. The **Network** lamp turns steady green when the unit is connected.");
        w.Callout("NOTE", """
            If the lamp keeps blinking after two minutes, the password may be wrong or the signal too
            weak. Move the unit closer to the router for the setup. You can move it back afterwards.
            """);

        w.Section("The companion app");
        w.P("""
            The app shows the particle and gas readings as a graph, lets you change the mode and the timer,
            and warns you when a filter needs replacing. It also holds the daily schedule. It does not
            store your readings in a cloud account unless you switch that on.
            """);
        w.Bullets(
            "**Overview** shows the current reading, the mode and the filter life at a glance.",
            "**History** shows the last seven days at five-minute resolution and the last year by day.",
            "**Schedule** sets up to eight daily or weekly switching times.",
            "**Filters** shows the remaining life of each filter and links to the order page.");

        w.Section("The service console");
        w.P("""
            For settings that the app does not offer, the unit has a text console on its service port. The
            port is a small round socket under the mains inlet in the base. Connect the serial service
            adapter from the box to it and to a computer, and open a terminal program.
            """, keepWithNext: true);
        w.Table(
            [140, 319],
            ["Setting", "Value"],
            [
                ["Baud rate", "115200"],
                ["Data bits, parity, stop bits", "8, none, 1"],
                ["Flow control", "None"],
                ["Line end sent by the unit", "Carriage return and line feed"],
                ["Prompt", "`al5>`"],
                ["Session timeout", "300 seconds without input"],
            ],
            codeColumn: 1);
        w.P("""
            When the terminal connects, press **Enter**. The unit answers with its prompt. Type `help` to
            see the list of commands. Appendix A describes every command. The block below shows a
            typical first session: the unit is asked for its status and then told to use a shorter
            reporting interval.
            """, keepWithNext: true);
        w.CommandBlock(
            "al5> aerolith status\n" +
            "model\t\tAL-5\n" +
            "firmware\t3.2.1\n" +
            "uptime\t\t4d 02:17:44\n" +
            "mode\t\tauto\n" +
            "fan\t\t38%\n" +
            "pm2.5\t\t7 ug/m3\n" +
            "voc\t\tlow\n" +
            "filters\t\thepa 71%  carbon 44%  pre ok\n" +
            "network\t\tconnected (-58 dBm)\n" +
            "\n" +
            "al5> aerolith set report-interval 60\n" +
            "report-interval\t60 s\t(was 300 s)\n" +
            "ok\n" +
            "al5> _",
            Network.ConsoleAnchor);
        w.P("""
            Columns in the output are separated by tab characters, not by spaces. The block above keeps
            them exactly as the unit prints them, with tab stops at every eighth column, so the columns
            line up in the same way on your screen.
            """);
        w.Callout("CAUTION", """
            Changes made in the console take effect at once and stay set after a restart. A wrong value
            can make the unit unreachable from the app. The command `aerolith reset-network` returns the
            network settings to the state they had when the unit left the factory.
            """);

        w.Section("Firmware updates");
        w.P("""
            When the unit is connected, it checks once a day for a new firmware version. If one is
            available, the app asks you to confirm the update. The unit downloads the file, checks its
            signature and installs it while it is idle. The update takes about three minutes and the unit
            restarts twice.
            """);
        w.P("""
            Do not unplug the unit during an update. If the power fails, the unit starts again with the
            old firmware and tries the update on the next day.
            """);
        w.Sub("Updating from the console");
        w.P("""
            Without a network, you can load a firmware file over the service port. Download the file from
            the support page onto your computer, then use the commands in the block below. The transfer
            takes about eight minutes.
            """, keepWithNext: true);
        w.CommandBlock(
            "al5> firmware load xmodem\n" +
            "waiting for transfer ...\n" +
            "  (start the XMODEM-CRC send in your terminal program now)\n" +
            "received 1,048,576 bytes\n" +
            "signature\tvalid\n" +
            "version\t\t3.3.0\n" +
            "al5> firmware apply\n" +
            "the unit restarts in 5 s");

        w.Section("Privacy");
        w.P("""
            The unit sends readings to the app only over your own network, unless you switch on remote
            access in the app. With remote access on, the readings pass through a relay server. They are
            encrypted, and the relay stores nothing. You can switch remote access off at any time. The unit
            then keeps working locally.
            """);
        w.P("""
            The unit does not hold a microphone or a camera. It does not record where you are in the room.
            The only data it keeps are the air quality readings, the filter counters and its settings.
            """);
    }
}
