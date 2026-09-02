// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="PdfReaderDiagnostic.ToString"/> (#385) pins the exact wording a caller sees from
/// logging or printing one diagnostic — the three shapes its own doc comment describes: no object
/// number, an object number with no generation, and an object number with a generation.
/// </summary>
public sealed class PdfReaderDiagnosticTests
{
    private static PdfReaderDiagnostic Report(
        PdfReaderDiagnosticCode code, string message, int? objectNumber = null, int? generation = null)
    {
        var sink = new DiagnosticSink(cap: 10);
        sink.Report(code, message, objectNumber, generation);
        return Assert.Single(sink.Diagnostics);
    }

    [Fact]
    public void ToString_noObjectNumber_omitsTheObjPart()
    {
        var d = Report(PdfReaderDiagnosticCode.FilterNull, "explicitly null");

        Assert.Equal("Info FilterNull: explicitly null", d.ToString());
    }

    [Fact]
    public void ToString_objectNumberWithoutGeneration_omitsTheGeneration()
    {
        var d = Report(
            PdfReaderDiagnosticCode.ObjectStreamContainerUnreadable, "could not be decoded", objectNumber: 9);

        Assert.Equal("Warning ObjectStreamContainerUnreadable obj 9: could not be decoded", d.ToString());
    }

    [Fact]
    public void ToString_objectNumberWithGeneration_includesBoth()
    {
        var d = Report(
            PdfReaderDiagnosticCode.ObjectGenerationMismatch, "generation mismatch",
            objectNumber: 10, generation: 2);

        Assert.Equal("Warning ObjectGenerationMismatch obj 10 2: generation mismatch", d.ToString());
    }
}
