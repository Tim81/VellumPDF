// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Samples.Manual;

/// <summary>Chapter 8: technical data, performance, warranty and disposal.</summary>
public static class Specifications
{
    /// <summary>Adds the chapter.</summary>
    /// <param name="w">The writer for the body.</param>
    public static void Write(ManualWriter w)
    {
        w.Chapter("Specifications and warranty");
        w.P("""
            This chapter gives the technical data of the AL-5 and the terms of the warranty. The figures
            were measured by the maker in a closed test room of 30 m³ and are rounded.
            """);

        w.Section("Technical data");
        w.Table(
            [180, 279],
            ["Item", "Value"],
            [
                ["Model", "AL-5 (grey) and AL-5W (white)"],
                ["Rated voltage and frequency", "220 to 240 V, 50 Hz"],
                ["Power consumption", "4 W at Night, 11 W at Low, 24 W at Medium, 46 W at High, under 1 W in standby"],
                ["Mains cable", "1.8 m, two-pole, with Europlug"],
                ["Protection class", "II"],
                ["Dimensions (width x depth x height)", "260 x 260 x 520 mm"],
                ["Weight", "5.8 kg with filters"],
                ["Recommended room size", "Up to 40 m²"],
                ["Filter stages", "Pre-filter, activated carbon, HEPA H13"],
                ["Sensors", "Laser particle sensor, VOC gas sensor, temperature, humidity"],
                ["Wireless", "Wi-Fi 2.4 GHz (IEEE 802.11 b/g/n), WPA2 and WPA3"],
                ["Service port", "3.3 V serial, 115200 baud, round 3.5 mm socket"],
                ["Operating conditions", "5 to 35 °C, 20 to 85 percent relative humidity, no condensation"],
                ["Storage conditions", "-10 to 50 °C, 10 to 90 percent relative humidity"],
                ["Noise level", "20 to 52 dB(A), measured at 1 m"],
            ],
            anchor: "tech-data");

        w.Section("Performance");
        w.P("""
            The clean air delivery rate, or CADR, is the volume of purified air that the unit produces per
            hour. It is measured with fine smoke particles, with pollen-sized dust and with a reference
            gas. A higher figure cleans a room faster.
            """, keepWithNext: true);
        w.Table(
            [100, 90, 90, 90, 89],
            ["Mode", "Smoke (m³/h)", "Dust (m³/h)", "Pollen (m³/h)", "Gas (m³/h)"],
            [
                ["Night", "38", "40", "42", "20"],
                ["Low", "95", "100", "105", "52"],
                ["Medium", "190", "200", "210", "105"],
                ["High", "330", "345", "360", "180"],
            ]);
        w.P("""
            At the High setting the unit exchanges the air of a room of 40 m² about five times per hour.
            In a room of 20 m², the unit reaches the same exchange at the Medium setting.
            """);

        w.Section("Filters and spare parts");
        w.Table(
            [90, 130, 120, 119],
            ["Part", "Order code", "Life", "Washable"],
            [
                ["HEPA filter", "AL5-H13", "12 months", "No"],
                ["Carbon filter", "AL5-CF", "6 months", "No"],
                ["Pre-filter", "AL5-PF", "3 years", "Yes"],
                ["Remote control", "AL5-RC", "Not applicable", "Not applicable"],
                ["Service adapter", "AL5-SA", "Not applicable", "Not applicable"],
            ]);
        w.P("""
            Use only filters with the order codes above. A filter from another model can fit and still not
            seal, which lets unfiltered air past the frame. The unit reports such a filter as error E406.
            """);

        w.Section("Warranty");
        w.P("""
            Aerolith Appliances gives a warranty of 24 months from the date of purchase. The warranty
            covers defects in material and workmanship. It does not cover filters, which are consumables,
            nor damage caused by wrong use, a fall, liquid in the unit, or repairs by a third party.
            """);
        w.P("""
            To claim under the warranty, keep the receipt and contact the shop where you bought the
            device. Describe the fault and give the serial number on the label in the base, and the
            error code if the unit shows one. Do not send the unit without agreeing it first.
            """);

        w.Section("Declaration of conformity");
        w.P("""
            Aerolith Appliances declares that the AL-5 meets the requirements of the directives that apply
            to it. The full declaration is available from the maker on request. Because the company is
            fictitious, so is the declaration.
            """);
    }
}
