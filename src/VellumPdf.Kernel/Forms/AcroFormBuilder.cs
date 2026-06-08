// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Forms;

/// <summary>
/// Builds all AcroForm indirect objects and wires them into the catalog and
/// page /Annots arrays. Called once per Save() invocation.
/// </summary>
internal static class AcroFormBuilder
{
    // ── Ff flag bit positions (ISO 32000-2 §12.7.4.2) ──────────────────────
    private const int FfReadOnly = 1;           // bit 1
    private const int FfRequired = 2;           // bit 2
    private const int FfMultiline = 1 << 12;    // bit 13
    private const int FfNoToggleToOff = 1 << 14; // bit 15 (radio: must always have a selection)
    private const int FfRadio = 1 << 15;         // bit 16 (Btn: radio buttons)
    private const int FfPushButton = 1 << 16;    // bit 17 (Btn: push button)
    private const int FfCombo = 1 << 17;         // bit 18 (choice field combo)

    // /F 4 = Print flag — widget is printable
    private const int AnnotFlagPrint = 4;

    /// <summary>
    /// Builds all field objects in <paramref name="registry"/>, wires each widget
    /// ref into its page's /Annots, and returns the /AcroForm dictionary to embed
    /// in the catalog (or null when no fields are registered).
    /// </summary>
    internal static PdfDictionary? Build(
        IReadOnlyList<PdfFormField> fields,
        PdfObjectRegistry registry,
        Dictionary<PdfPage, PdfIndirectReference> pageRefMap,
        PdfFontResource helveticaFont)
    {
        if (fields.Count == 0)
            return null;

        // Write the Helvetica font as an indirect object so /DR and /AP /Resources
        // can reference it by ref rather than inlining the same dict repeatedly.
        var helvFontRef = registry.Reserve();
        registry.SetValue(helvFontRef, helveticaFont.BuildDictionary());

        // ZapfDingbats for checkbox appearances
        var zadbFontResource = new PdfFontResource(Standard14.ZapfDingbats, "ZaDb");
        var zadbFontRef = registry.Reserve();
        registry.SetValue(zadbFontRef, zadbFontResource.BuildDictionary());

        // Accumulate field refs for /AcroForm /Fields
        var fieldRefs = new List<PdfIndirectReference>(fields.Count);

        foreach (var field in fields)
        {
            switch (field)
            {
                case PdfFormField.RadioGroupField rg:
                    {
                        // Radio group: one field ref (no widget annotation on its own page),
                        // kid widget refs go onto their respective pages.
                        var fieldRef = BuildRadioGroupField(rg, registry, zadbFontRef, pageRefMap);
                        fieldRefs.Add(fieldRef);
                        // Note: kid widgets are added to their pages inside BuildRadioGroupField.
                        break;
                    }

                default:
                    {
                        pageRefMap.TryGetValue(field.Page, out var pageRef);

                        PdfIndirectReference widgetRef = field switch
                        {
                            PdfFormField.TextField tf => BuildTextField(tf, registry, helvFontRef, pageRef),
                            PdfFormField.CheckBoxField cb => BuildCheckBoxField(cb, registry, helvFontRef, zadbFontRef, pageRef),
                            PdfFormField.ChoiceField ch => BuildChoiceField(ch, registry, helvFontRef, pageRef),
                            PdfFormField.PushButtonField pb => BuildPushButtonField(pb, registry, helvFontRef, pageRef),
                            _ => throw new InvalidOperationException("Unknown field type"),
                        };

                        fieldRefs.Add(widgetRef);
                        field.Page.AddAnnotation(widgetRef);
                        break;
                    }
            }
        }

        // Build /AcroForm /DR /Font dict
        var drFontDict = new PdfDictionary()
            .Set(new PdfName("Helv"), helvFontRef)
            .Set(new PdfName("ZaDb"), zadbFontRef);

        var drDict = new PdfDictionary()
            .Set(PdfName.Font, drFontDict);

        var fieldsArray = new PdfArray(fieldRefs.Cast<PdfObject>());

        return new PdfDictionary()
            .Set(new PdfName("Fields"), fieldsArray)
            .Set(new PdfName("NeedAppearances"), PdfBoolean.False)
            .Set(new PdfName("DR"), drDict)
            .Set(new PdfName("DA"), new PdfLiteralString("/Helv 0 Tf 0 g"u8.ToArray()));
    }

    // ── Text field ──────────────────────────────────────────────────────────

    private static PdfIndirectReference BuildTextField(
        PdfFormField.TextField field,
        PdfObjectRegistry registry,
        PdfIndirectReference helvFontRef,
        PdfIndirectReference? pageRef)
    {
        var opts = field.Options;
        var rect = field.Rect;
        double w = rect.Width;
        double h = rect.Height;
        double sz = opts.FontSize;

        // Build appearance stream
        var apContent = BuildTextAppearanceContent(field.Value, sz, w, h);
        var apStream = BuildFormXObjectStream(apContent, w, h, helvFontRef);
        var apRef = registry.Reserve();
        registry.SetValue(apRef, apStream);

        // Compute /Ff flags
        var ff = 0;
        if (opts.ReadOnly) ff |= FfReadOnly;
        if (opts.Required) ff |= FfRequired;
        if (opts.Multiline) ff |= FfMultiline;

        var da = BuildDa(sz);

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Widget"))
            .Set(new PdfName("FT"), new PdfName("Tx"))
            .Set(new PdfName("T"), LiteralFromLatin1(field.Name))
            .Set(new PdfName("Rect"), rect.ToArray())
            .Set(new PdfName("V"), LiteralFromLatin1(field.Value))
            .Set(new PdfName("DA"), da)
            .Set(new PdfName("F"), new PdfInteger(AnnotFlagPrint))
            .Set(new PdfName("MK"), BuildMk())
            .Set(new PdfName("AP"), BuildApN(apRef))
            .Set(new PdfName("Ff"), new PdfInteger(ff));

        if (pageRef is not null)
            dict.Set(new PdfName("P"), pageRef);

        var widgetRef = registry.Reserve();
        registry.SetValue(widgetRef, dict);
        return widgetRef;
    }

    // ── Checkbox field ──────────────────────────────────────────────────────

    private static PdfIndirectReference BuildCheckBoxField(
        PdfFormField.CheckBoxField field,
        PdfObjectRegistry registry,
        PdfIndirectReference helvFontRef,
        PdfIndirectReference zadbFontRef,
        PdfIndirectReference? pageRef)
    {
        var opts = field.Options;
        var rect = field.Rect;
        double w = rect.Width;
        double h = rect.Height;
        double sz = opts.FontSize;

        // /Yes appearance: ZapfDingbats char 0x34 (decimal 52) = '4' which renders as ✔
        var yesContent = BuildCheckAppearanceContent(sz, w, h);
        var yesStream = BuildFormXObjectStreamWithZaDb(yesContent, w, h, zadbFontRef);
        var yesRef = registry.Reserve();
        registry.SetValue(yesRef, yesStream);

        // /Off appearance: empty stream
        var offContent = Array.Empty<byte>();
        var offStream = BuildFormXObjectStreamNoFont(offContent, w, h);
        var offRef = registry.Reserve();
        registry.SetValue(offRef, offStream);

        var apNDict = new PdfDictionary()
            .Set(new PdfName("Yes"), yesRef)
            .Set(new PdfName("Off"), offRef);

        var ap = new PdfDictionary()
            .Set(PdfName.N, apNDict);

        var ff = 0;
        if (opts.ReadOnly) ff |= FfReadOnly;
        if (opts.Required) ff |= FfRequired;

        var valueName = field.CheckedState ? "Yes" : "Off";

        var mk = new PdfDictionary()
            .Set(new PdfName("CA"), LiteralFromLatin1("4"));

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Widget"))
            .Set(new PdfName("FT"), new PdfName("Btn"))
            .Set(new PdfName("T"), LiteralFromLatin1(field.Name))
            .Set(new PdfName("Rect"), rect.ToArray())
            .Set(new PdfName("V"), new PdfName(valueName))
            .Set(new PdfName("AS"), new PdfName(valueName))
            .Set(new PdfName("MK"), mk)
            .Set(new PdfName("AP"), ap)
            .Set(new PdfName("F"), new PdfInteger(AnnotFlagPrint))
            .Set(new PdfName("Ff"), new PdfInteger(ff));

        if (pageRef is not null)
            dict.Set(new PdfName("P"), pageRef);

        var widgetRef = registry.Reserve();
        registry.SetValue(widgetRef, dict);
        return widgetRef;
    }

    // ── Choice (dropdown / listbox) field ───────────────────────────────────

    private static PdfIndirectReference BuildChoiceField(
        PdfFormField.ChoiceField field,
        PdfObjectRegistry registry,
        PdfIndirectReference helvFontRef,
        PdfIndirectReference? pageRef)
    {
        var opts = field.Options;
        var rect = field.Rect;
        double w = rect.Width;
        double h = rect.Height;
        double sz = opts.FontSize;

        var selectedValue = field.Selected ?? (field.ChoiceOptions.Count > 0 ? field.ChoiceOptions[0] : string.Empty);

        // Build appearance stream (same as text field — shows selected value)
        var apContent = BuildTextAppearanceContent(selectedValue, sz, w, h);
        var apStream = BuildFormXObjectStream(apContent, w, h, helvFontRef);
        var apRef = registry.Reserve();
        registry.SetValue(apRef, apStream);

        // /Opt array: each element is a literal string
        var optArray = new PdfArray();
        foreach (var opt in field.ChoiceOptions)
            optArray.Add(LiteralFromLatin1(opt));

        var ff = 0;
        if (opts.ReadOnly) ff |= FfReadOnly;
        if (opts.Required) ff |= FfRequired;
        if (field.Combo) ff |= FfCombo;

        var da = BuildDa(sz);

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Widget"))
            .Set(new PdfName("FT"), new PdfName("Ch"))
            .Set(new PdfName("T"), LiteralFromLatin1(field.Name))
            .Set(new PdfName("Rect"), rect.ToArray())
            .Set(new PdfName("Ff"), new PdfInteger(ff))
            .Set(new PdfName("Opt"), optArray)
            .Set(new PdfName("V"), LiteralFromLatin1(selectedValue))
            .Set(new PdfName("DA"), da)
            .Set(new PdfName("F"), new PdfInteger(AnnotFlagPrint))
            .Set(new PdfName("MK"), BuildMk())
            .Set(new PdfName("AP"), BuildApN(apRef));

        if (pageRef is not null)
            dict.Set(new PdfName("P"), pageRef);

        var widgetRef = registry.Reserve();
        registry.SetValue(widgetRef, dict);
        return widgetRef;
    }

    // ── Radio button group field ────────────────────────────────────────────

    private static PdfIndirectReference BuildRadioGroupField(
        PdfFormField.RadioGroupField field,
        PdfObjectRegistry registry,
        PdfIndirectReference zadbFontRef,
        Dictionary<PdfPage, PdfIndirectReference> pageRefMap)
    {
        var opts = field.Options;

        // Reserve the parent field ref so kids can reference it via /Parent.
        var fieldRef = registry.Reserve();

        var ff = FfRadio | FfNoToggleToOff;
        if (opts.ReadOnly) ff |= FfReadOnly;
        if (opts.Required) ff |= FfRequired;

        var valueName = field.SelectedExportValue ?? "Off";

        // Build each kid widget annotation.
        var kidRefs = new List<PdfIndirectReference>(field.RadioOptions.Count);
        foreach (var option in field.RadioOptions)
        {
            var rect = option.Rect;
            double w = rect.Width;
            double h = rect.Height;

            // On-state appearance: filled radio dot using ZapfDingbats 'l' (lowercase L = bullet).
            var onContent = BuildRadioOnAppearanceContent(opts.FontSize, w, h);
            var onStream = BuildFormXObjectStreamWithZaDb(onContent, w, h, zadbFontRef);
            var onApRef = registry.Reserve();
            registry.SetValue(onApRef, onStream);

            // Off-state appearance: empty stream.
            var offStream = BuildFormXObjectStreamNoFont([], w, h);
            var offApRef = registry.Reserve();
            registry.SetValue(offApRef, offStream);

            // /AP /N dict keyed by export value and /Off.
            var apNDict = new PdfDictionary()
                .Set(new PdfName(option.ExportValue), onApRef)
                .Set(new PdfName("Off"), offApRef);

            var ap = new PdfDictionary()
                .Set(PdfName.N, apNDict);

            // /AS is the export value if this kid is selected, otherwise /Off.
            var asName = string.Equals(option.ExportValue, field.SelectedExportValue, StringComparison.Ordinal)
                ? option.ExportValue
                : "Off";

            var mk = new PdfDictionary()
                .Set(new PdfName("CA"), LiteralFromLatin1("l"));

            pageRefMap.TryGetValue(option.Page, out var kidPageRef);

            var kidDict = new PdfDictionary()
                .Set(PdfName.Type, new PdfName("Annot"))
                .Set(PdfName.Subtype, new PdfName("Widget"))
                .Set(new PdfName("Parent"), fieldRef)
                .Set(new PdfName("Rect"), rect.ToArray())
                .Set(new PdfName("F"), new PdfInteger(AnnotFlagPrint))
                .Set(new PdfName("AP"), ap)
                .Set(new PdfName("AS"), new PdfName(asName))
                .Set(new PdfName("MK"), mk);

            if (kidPageRef is not null)
                kidDict.Set(new PdfName("P"), kidPageRef);

            var kidRef = registry.Reserve();
            registry.SetValue(kidRef, kidDict);
            kidRefs.Add(kidRef);

            // Each kid widget annotation goes into its own page's /Annots.
            option.Page.AddAnnotation(kidRef);
        }

        var kidsArray = new PdfArray(kidRefs.Cast<PdfObject>());

        // The parent field dict has no /Rect (it is a non-terminal field node).
        var fieldDict = new PdfDictionary()
            .Set(new PdfName("FT"), new PdfName("Btn"))
            .Set(new PdfName("T"), LiteralFromLatin1(field.Name))
            .Set(new PdfName("Ff"), new PdfInteger(ff))
            .Set(new PdfName("V"), new PdfName(valueName))
            .Set(new PdfName("DA"), new PdfLiteralString("/ZaDb 0 Tf 0 g"u8.ToArray()))
            .Set(new PdfName("Kids"), kidsArray);

        registry.SetValue(fieldRef, fieldDict);
        return fieldRef;
    }

    // ── Push button field ────────────────────────────────────────────────────

    private static PdfIndirectReference BuildPushButtonField(
        PdfFormField.PushButtonField field,
        PdfObjectRegistry registry,
        PdfIndirectReference helvFontRef,
        PdfIndirectReference? pageRef)
    {
        var opts = field.Options;
        var rect = field.Rect;
        double w = rect.Width;
        double h = rect.Height;
        double sz = opts.FontSize;

        // Appearance: grey background + border + centred caption text.
        var apContent = BuildPushButtonAppearanceContent(field.Caption, sz, w, h);
        var apStream = BuildFormXObjectStream(apContent, w, h, helvFontRef);
        var apRef = registry.Reserve();
        registry.SetValue(apRef, apStream);

        var ff = FfPushButton;
        if (opts.ReadOnly) ff |= FfReadOnly;

        var mk = new PdfDictionary()
            .Set(new PdfName("CA"), LiteralFromLatin1(field.Caption))
            .Set(new PdfName("BG"), new PdfArray([new PdfReal(0.8), new PdfReal(0.8), new PdfReal(0.8)]))
            .Set(new PdfName("BC"), new PdfArray([new PdfReal(0), new PdfReal(0), new PdfReal(0)]));

        var dict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Widget"))
            .Set(new PdfName("FT"), new PdfName("Btn"))
            .Set(new PdfName("Ff"), new PdfInteger(ff))
            .Set(new PdfName("T"), LiteralFromLatin1(field.Name))
            .Set(new PdfName("Rect"), rect.ToArray())
            .Set(new PdfName("F"), new PdfInteger(AnnotFlagPrint))
            .Set(new PdfName("MK"), mk)
            .Set(new PdfName("AP"), BuildApN(apRef));

        if (pageRef is not null)
            dict.Set(new PdfName("P"), pageRef);

        var widgetRef = registry.Reserve();
        registry.SetValue(widgetRef, dict);
        return widgetRef;
    }

    // ── Appearance-stream helpers ────────────────────────────────────────────

    /// <summary>
    /// Builds the on-state appearance content for a radio button kid widget.
    /// Uses ZapfDingbats 'l' (lowercase L = bullet/filled circle symbol).
    /// </summary>
    private static byte[] BuildRadioOnAppearanceContent(double fontSize, double w, double h)
    {
        var sz = fontSize > 0 ? fontSize : Math.Min(w, h) * 0.8;
        var baselineY = (h - sz) / 2.0;
        if (baselineY < 1.0) baselineY = 1.0;
        var x = (w - sz * 0.6) / 2.0;
        if (x < 1.0) x = 1.0;

        var sb = new StringBuilder();
        sb.Append("q\n");
        sb.Append("BT\n");
        sb.AppendFormat("/ZaDb {0:0.###} Tf\n", sz);
        sb.Append("0 g\n");
        sb.AppendFormat("{0:0.###} {1:0.###} Td\n", x, baselineY);
        sb.Append("(l) Tj\n");
        sb.Append("ET\n");
        sb.Append("Q\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds the normal-state appearance content for a push button widget.
    /// Draws a grey background, a black border, and the caption centred using Helvetica.
    /// </summary>
    private static byte[] BuildPushButtonAppearanceContent(string caption, double fontSize, double w, double h)
    {
        var baselineY = (h - fontSize) / 2.0;
        if (baselineY < 1.0) baselineY = 1.0;

        var sb = new StringBuilder();
        // Grey background
        sb.Append("q\n");
        sb.Append("0.8 0.8 0.8 rg\n");
        sb.AppendFormat("{0:0.###} {1:0.###} {2:0.###} {3:0.###} re f\n", 0, 0, w, h);
        // Black border
        sb.Append("0 0 0 RG\n");
        sb.Append("1 w\n");
        sb.AppendFormat("{0:0.###} {1:0.###} {2:0.###} {3:0.###} re S\n", 0.5, 0.5, w - 1, h - 1);
        // Caption text
        sb.Append("BT\n");
        sb.AppendFormat("/Helv {0:0.###} Tf\n", fontSize);
        sb.Append("0 g\n");
        sb.AppendFormat("{0:0.###} {1:0.###} Td\n", 4.0, baselineY);
        sb.Append('(');
        sb.Append(EscapePdfString(caption));
        sb.Append(") Tj\n");
        sb.Append("ET\n");
        sb.Append("Q\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds the content-stream bytes for a text or choice field appearance.
    /// Draws an optional background, border, then the value text.
    /// </summary>
    private static byte[] BuildTextAppearanceContent(string value, double fontSize, double w, double h)
    {
        // Baseline: roughly 1/5 from bottom inside the field
        var baselineY = (h - fontSize) / 2.0;
        if (baselineY < 1.0) baselineY = 1.0;

        var sb = new StringBuilder();
        // Background + border
        sb.Append("q\n");
        sb.Append("1 1 1 rg\n");                          // white fill
        sb.AppendFormat("{0:0.###} {1:0.###} {2:0.###} {3:0.###} re f\n", 0, 0, w, h);
        sb.Append("0 0 0 RG\n");                           // black stroke
        sb.Append("0.5 w\n");
        sb.AppendFormat("{0:0.###} {1:0.###} {2:0.###} {3:0.###} re S\n", 0, 0, w, h);
        // Text
        sb.Append("/Tx BMC\n");
        sb.Append("q\n");
        sb.Append("BT\n");
        sb.AppendFormat("/Helv {0:0.###} Tf\n", fontSize);
        sb.Append("0 g\n");
        sb.AppendFormat("{0:0.###} {1:0.###} Td\n", 2.0, baselineY);
        sb.Append('(');
        sb.Append(EscapePdfString(value));
        sb.Append(") Tj\n");
        sb.Append("ET\n");
        sb.Append("Q\n");
        sb.Append("EMC\n");
        sb.Append("Q\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds the /Yes checkbox appearance content using ZapfDingbats char '4' (✔).
    /// </summary>
    private static byte[] BuildCheckAppearanceContent(double fontSize, double w, double h)
    {
        var baselineY = (h - fontSize) / 2.0;
        if (baselineY < 1.0) baselineY = 1.0;
        var x = (w - fontSize * 0.6) / 2.0;
        if (x < 1.0) x = 1.0;

        var sb = new StringBuilder();
        sb.Append("q\n");
        sb.Append("BT\n");
        sb.AppendFormat("/ZaDb {0:0.###} Tf\n", fontSize);
        sb.Append("0 g\n");
        sb.AppendFormat("{0:0.###} {1:0.###} Td\n", x, baselineY);
        sb.Append("(4) Tj\n");
        sb.Append("ET\n");
        sb.Append("Q\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a Form XObject stream with /Helv in its /Resources /Font.
    /// </summary>
    private static PdfStream BuildFormXObjectStream(byte[] content, double w, double h, PdfIndirectReference helvFontRef)
    {
        var stream = new PdfStream(content);
        var fontDict = new PdfDictionary()
            .Set(new PdfName("Helv"), helvFontRef);
        var resources = new PdfDictionary()
            .Set(PdfName.Font, fontDict);

        stream.Dictionary
            .Set(PdfName.Type, new PdfName("XObject"))
            .Set(PdfName.Subtype, new PdfName("Form"))
            .Set(new PdfName("BBox"), BBoxArray(w, h))
            .Set(new PdfName("Resources"), resources);

        return stream;
    }

    /// <summary>
    /// Builds a Form XObject stream with /ZaDb in its /Resources /Font (checkbox /Yes).
    /// </summary>
    private static PdfStream BuildFormXObjectStreamWithZaDb(byte[] content, double w, double h, PdfIndirectReference zadbFontRef)
    {
        var stream = new PdfStream(content);
        var fontDict = new PdfDictionary()
            .Set(new PdfName("ZaDb"), zadbFontRef);
        var resources = new PdfDictionary()
            .Set(PdfName.Font, fontDict);

        stream.Dictionary
            .Set(PdfName.Type, new PdfName("XObject"))
            .Set(PdfName.Subtype, new PdfName("Form"))
            .Set(new PdfName("BBox"), BBoxArray(w, h))
            .Set(new PdfName("Resources"), resources);

        return stream;
    }

    /// <summary>
    /// Builds a Form XObject stream with no font resources (checkbox /Off — empty).
    /// </summary>
    private static PdfStream BuildFormXObjectStreamNoFont(byte[] content, double w, double h)
    {
        var stream = new PdfStream(content);
        var resources = new PdfDictionary();

        stream.Dictionary
            .Set(PdfName.Type, new PdfName("XObject"))
            .Set(PdfName.Subtype, new PdfName("Form"))
            .Set(new PdfName("BBox"), BBoxArray(w, h))
            .Set(new PdfName("Resources"), resources);

        return stream;
    }

    // ── Small helpers ────────────────────────────────────────────────────────

    private static PdfArray BBoxArray(double w, double h) =>
        new([new PdfReal(0), new PdfReal(0), new PdfReal(w), new PdfReal(h)]);

    private static PdfDictionary BuildMk() =>
        new PdfDictionary()
            .Set(new PdfName("BG"), new PdfArray([new PdfReal(1), new PdfReal(1), new PdfReal(1)]))
            .Set(new PdfName("BC"), new PdfArray([new PdfReal(0), new PdfReal(0), new PdfReal(0)]));

    private static PdfDictionary BuildApN(PdfIndirectReference nRef) =>
        new PdfDictionary().Set(PdfName.N, nRef);

    private static PdfLiteralString BuildDa(double fontSize) =>
        new(Encoding.Latin1.GetBytes($"/Helv {fontSize:0.###} Tf 0 g"));

    private static PdfLiteralString LiteralFromLatin1(string value) =>
        new(Encoding.Latin1.GetBytes(value));

    /// <summary>
    /// Escapes a string value for inclusion between ( ) in a PDF content stream.
    /// Escapes (, ), \, \n, \r.
    /// </summary>
    private static string EscapePdfString(string value)
    {
        if (value.Length == 0)
            return string.Empty;

        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            switch (c)
            {
                case '(': sb.Append(@"\("); break;
                case ')': sb.Append(@"\)"); break;
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                default:
                    if (c < 0x80)
                        sb.Append(c);
                    else
                        sb.Append('?'); // non-Latin-1 fallback; fields are Latin-1
                    break;
            }
        }
        return sb.ToString();
    }
}
