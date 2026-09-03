// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

public sealed partial class PdfDocumentReader
{
    /// <summary>
    /// Creates a fresh <see cref="DiagnosticSink"/> scope (see <see cref="DiagnosticSink.CreateScope"/>)
    /// forwarding into this reader's own diagnostics. <c>ContentInterpreter.Run</c> is the first real
    /// caller of <see cref="DiagnosticSink.CreateScope"/> (see that method's own remarks), creating
    /// one scope per page interpreted so a caller who runs the interpreter over the same page more
    /// than once (text extraction, then image extraction, per #98) gets a fresh, page-scoped
    /// dedupe set each time rather than sharing one that silently goes quiet on a second pass's own
    /// first occurrence of a condition the first pass already reported into it. Reports made through
    /// the returned scope still land in <see cref="Diagnostics"/>, under this reader's own cap.
    /// </summary>
    internal DiagnosticSink CreateContentDiagnosticScope() => _diagnostics.CreateScope();
}
