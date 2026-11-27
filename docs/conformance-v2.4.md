# Conformance audit of the v2.4 features

This file records, clause by clause, what the v2.4 changes write and whether it meets the standard the file
claims. One section per group of features. A row names the standard and clause, paraphrases the requirement,
names the feature that can produce the construct, gives a verdict, and names the test that checks it.

Verdicts:

- compliant: a test asserts the written bytes against the clause, and any validator that has a rule agrees.
- gap: the code writes something the clause forbids. The test exists and is skipped, with the gap's label (or
  issue number) in its reason, so it turns on when the gap is fixed.
- not applicable: no writer in the eight packages produces the construct. The row says what was searched.
- human-only: no validator rule and no mechanical check.

ISO 19005-2 is not held in the repository. Its clause numbers and paraphrases come from a reading on NEN Connect,
and the rows for it are only as good as that reading; clauses the reading did not complete are listed under
"Needs NEN" in the section. The other standards are the copies under docs/iso, read with pdftotext. No licensed
text is quoted here.

## G1 Navigation

No rows yet.

## G2 Text and fonts

No rows yet.

## G3 Structure and artifacts

No rows yet.

## G4 Graphics and images

No rows yet.

## G5 File structure and metadata

Audited at origin/main bbf317b, 68 commits after v2.3.3. Fixtures are saved in the test with the fonts in the tree, under no claim and under PDF/A-2b, 2u, 2a and PDF/UA-1, through six paths: the Layout document, the Kernel document, object streams, linearization, the unsigned placeholder of the signing path, and the signed file. `AuditScanner` reads the saved bytes without the library's Reader. veraPDF 1.30.4 and qpdf 12.4.2 run on every claimed fixture, and the unsigned placeholder is the one file no validator is run on, because its /ByteRange holds ten nines until the signature patches it.

101 rows: 71 compliant, 18 gap, 11 not applicable, 1 human-only.

Feature key: SAVE is Save and SaveAsync on the Kernel and Layout documents; OBJ is UseObjectStreams; LIN is Linearize; SIGN is PrepareForSigning and Sign, which share the catalog builder with Save; ID is Timestamp and DocumentId; NUM is reals outside int32; EMPTY is the empty zlib stream; STR is literal-string line ends, Lang and signature text strings; TREE is the name and number tree writers; RECOV is the Reader's recovery bounds.

| Standard and clause | Requirement | Feature | Verdict | Test |
|---|---|---|---|---|
| ISO 19005-2 5 | Level B leaves out the Unicode map and structure rules, U leaves out structure, A needs everything; the header version is not a criterion. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.1.1 | Data the standards do not describe is not used to render. | SAVE, OBJ, LIN, SIGN | human-only | none |
| ISO 19005-2 6.1.2 | The header is %PDF-1.n at byte 0, n from 0 to 7, then one EOL. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 19005-2 6.1.2 | A comment of at least four bytes above 127 follows the header line. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 19005-2 6.1.3 | The trailer has an /ID of file identifiers. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 19005-2 6.1.3 | The trailer has no /Encrypt; the save refuses encryption under every PDF/A level. | SAVE, SIGN | compliant | `Iso19005_2_6_1_3_EncryptIsRefusedAtSaveForEveryPdfALevelOnEverySavePath` |
| ISO 19005-2 6.1.3 | Nothing follows the last %%EOF except one optional EOL. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 19005-2 6.1.4 | One EOL separates the xref keyword from the subsection header. | SAVE, LIN, SIGN | compliant | `Iso19005_2_6_1_4_XrefKeywordAndSubsectionHeaderAreSeparatedByOneEol` |
| ISO 19005-2 6.1.5 | An Info dictionary may be present and is ignored by a reader. | SAVE, OBJ, LIN, SIGN | compliant | `Iso32000_2_14_3_3_InfoValuesAreTextStringsAndNoDeprecatedKeyIsWrittenUnset` |
| ISO 19005-2 6.1.6 | A hex string has an even number of digits and only hex digits. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_6_HexStringsHaveAnEvenNumberOfDigits` |
| ISO 19005-2 6.1.7.1 | stream is followed by LF or CRLF, endstream by an EOL, and /Length is the exact byte count. | SAVE, OBJ, LIN, SIGN, EMPTY | compliant | `Iso19005_2_6_1_7_1_StreamKeywordLengthAndEndstreamFollowTheSyntax` |
| ISO 19005-2 6.1.7.1 | A stream dictionary has no F, FFilter or FDecodeParms. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_7_1_StreamKeywordLengthAndEndstreamFollowTheSyntax` |
| ISO 19005-2 6.1.7.2 | Only the standard filters except LZWDecode; Crypt only as Identity; no other filter. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_7_2_OnlyFlateDecodeIsUsed` |
| ISO 19005-2 6.1.7.2 | The remaining conditions on Crypt filters (not in the notes). | - | not applicable | none |
| ISO 19005-2 6.1.8 | Font names, colourant names and structure type names are valid UTF-8 after # expansion. | SAVE (font names), SIGN | gap #740 | `Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim` |
| ISO 19005-2 6.1.9 | Object, generation and obj are separated by one space, and obj and endobj are surrounded by EOLs. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_9_ObjectKeywordsAreSeparatedByOneSpaceAndSurroundedByEols` |
| ISO 19005-2 6.1.10 | An inline image's /F is not LZW, Crypt or outside Table 6. | - | not applicable | none |
| ISO 19005-2 6.1.11 | Linearization is allowed; a reader ignores it. | LIN | compliant | `Iso19005_2_6_1_11_LinearizedFileIsAllowedAndItsParameterDictionaryIsFirst` |
| ISO 19005-2 6.1.12 | A permissions dictionary holds only UR3 and DocMDP, with conditions on DocMDP. | - | not applicable | `Iso19005_2_6_1_12_And_6_11_CatalogHasNoPermsAndNoRequirements` |
| ISO 19005-2 6.1.13 | No integer outside -2147483648 to 2147483647. | SAVE, OBJ, LIN, SIGN, NUM | compliant | `Iso19005_2_6_1_13_IntegerTokensStayWithinInt32AndRealsCarryNoExponent` |
| ISO 19005-2 6.1.13 | No real outside +-3.403e38. | SAVE, NUM | gap #666 | `Iso19005_2_6_1_13_RealsAbove3_403e38AreNotWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | No string longer than 32767 bytes (a string of exactly 32767 is within it). | SAVE, SIGN | gap #666 | `Iso19005_2_6_1_13_StringsOver32767BytesAreNotWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | No name longer than 127 bytes (a name of exactly 127 is within it). | SAVE, SIGN | gap #740 | `Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | No nonzero real closer to zero than +-1.175e-38. | SAVE, NUM | compliant | `Iso19005_2_6_1_13_RealsAtTheBoundariesAreWrittenAsTheClauseAllows` |
| ISO 19005-2 6.1.13 | At most 8388607 indirect objects. | SAVE, OBJ, LIN, SIGN | gap #666 | `Iso19005_2_6_1_13_AtMost8388607IndirectObjectsAreWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | q/Q nesting at most 28 deep. | SAVE (Layout renderers; Kernel canvas) | gap #666 | `Iso19005_2_6_1_13_QqNestingOfTwentyNineIsNotWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | A DeviceN colour space has at most 32 colourants. | - | not applicable | none |
| ISO 19005-2 6.1.13 | No CID above 65535. | - | not applicable | none |
| ISO 19005-2 6.1.13 | A page boundary is between 3 and 14400 units in each direction. | SAVE (Kernel AddPage, Layout PageSize) | gap #666 | `Iso19005_2_6_1_13_PageBoundariesOutsideThreeToFourteenThousandFourHundredAreNotWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.2.3 | A PDF/A output intent (S GTS_PDFA1) carries an ICC profile as /DestOutputProfile (written by the shared catalog builder). | SAVE, SIGN | compliant | `Iso19005_2_6_2_3_OutputIntentIsGtsPdfa1WithAnIccProfileOnEverySavePath` |
| ISO 19005-2 6.6.2.1 | The catalog /Metadata is an XMP stream with /Type /Metadata and /Subtype /XML. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute` |
| ISO 19005-2 6.6.2.1 | The packet header has no bytes and no encoding attribute; the packet is UTF-8. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute` |
| ISO 19005-2 6.6.2.1 | Every packet is well-formed XML 1.0 (and conforms to the XMP specification). | SAVE (Info, Language text) | gap #741 | `Iso19005_2_6_6_2_1_XmpStaysWellFormedForEveryStringTheWriterAccepts` |
| ISO 19005-2 6.6.2.1 | Entities and control characters in text are escaped or dropped so the packet parses. | SAVE | compliant | `Iso19005_2_6_6_2_1_XmpTextIsEscapedAndIllegalControlCharactersAreDropped` |
| ISO 19005-2 6.6.2.2 | Suggested prefixes dc, pdf, xmp, xmpMM. | SAVE | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.2.3.1 | Every property is in a predefined schema or a described extension schema. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.2.3.2-3 | Extension schemas use the container and carry every field of Tables 2 to 6. | - | not applicable | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.3 | Info entries agree with their XMP properties (Table 7); a writer should keep them consistent. | SAVE | compliant | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 19005-2 6.6.4 | pdfaid:part is 2 and pdfaid:conformance is A, B or U; the prefix is pdfaid; amd and corr only when used. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.5 | Identifying properties are recommended; adding xmpMM:History changes the changing half of /ID. | - | not applicable | none |
| ISO 19005-2 6.6.6 | A History entry has action, parameters and when. | - | not applicable | none |
| ISO 19005-2 6.7.4 | The catalog /Lang should hold the default language as a language identifier. | SAVE, STR | compliant | `Iso19005_2_6_7_4_And_Iso14289_1_7_2_CatalogLangIsALanguageTagMatchedByXmpAndByItsTrim` |
| ISO 19005-2 6.7.4 | A /Lang that is not a language identifier is refused (the writer does not validate it). | SAVE | gap #614 | `Iso14289_1_7_2_LanguageThatIsNotATagIsNotWrittenUnderPdfUA1` |
| ISO 19005-2 6.11 | The catalog has no Requirements key. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_12_And_6_11_CatalogHasNoPermsAndNoRequirements` |
| ISO 32000-1 7.2 | Lexical conventions: white space, delimiters, regular characters, comments. | SAVE, OBJ, LIN, SIGN | compliant | `Oracles_QpdfAcceptsEveryFixtureUnderEveryClaim` |
| ISO 32000-1 7.3.2 | Boolean objects are the keywords true and false. | SAVE, SIGN | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 32000-1 7.3.3 | An integer is decimal digits with an optional sign. | SAVE, NUM | compliant | `Iso19005_2_6_1_13_IntegerTokensStayWithinInt32AndRealsCarryNoExponent` |
| ISO 32000-1 7.3.3 | A real is decimal digits with a point; no exponent, no radix (NOTE 1). | SAVE, NUM | compliant | `Iso19005_2_6_1_13_RealsAtTheBoundariesAreWrittenAsTheClauseAllows` |
| ISO 32000-1 7.3.4.1, 7.3.4.2 | A literal string escapes unbalanced parentheses and backslash; a line end is not left raw. | SAVE, STR | compliant | `Iso32000_1_7_3_4_2_LiteralStringsEscapeParenthesesBackslashAndLineEnds` |
| ISO 32000-1 7.3.4.3 | A hex string is hex digits in angle brackets. | SAVE, SIGN | compliant | `Iso19005_2_6_1_6_HexStringsHaveAnEvenNumberOfDigits` |
| ISO 32000-1 7.3.5 | A name begins with a solidus; #, white space, delimiters and bytes outside ! to ~ are written as # and two hex digits. | SAVE | compliant | `Iso32000_1_7_3_5_NamesEscapeNumberSignSpaceAndDelimitersAndReadBackTheSame` |
| ISO 32000-1 7.3.5 | A name is a sequence of 8-bit characters; the writer keeps characters above U+00FF. | SAVE | gap #740 | `Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim` |
| ISO 32000-1 7.3.6 | An array is objects in square brackets. | SAVE | compliant | `Oracles_QpdfAcceptsEveryFixtureUnderEveryClaim` |
| ISO 32000-1 7.3.7 | Dictionary keys are names, no key repeats, a null value is left out. | SAVE, OBJ, LIN, SIGN | compliant | `Iso32000_1_7_3_7_NoDictionaryRepeatsAKey` |
| ISO 32000-1 7.3.8.1 | A stream is an indirect object with a direct dictionary; stream is followed by LF or CRLF, never CR alone; nothing but white space between endstream and endobj. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_7_1_StreamKeywordLengthAndEndstreamFollowTheSyntax` |
| ISO 32000-1 7.3.8.2 | Every stream has a /Length that equals the bytes of data. | SAVE, OBJ, LIN, SIGN, EMPTY | compliant | `Iso19005_2_6_1_7_1_AnEmptyPageContentIsTheEmptyZlibStreamWithItsLengthAndPassesTheTools` |
| ISO 32000-1 7.3.9 | There is one null object; a null entry equals none. | SAVE | compliant | `Iso32000_1_7_3_7_NoDictionaryRepeatsAKey` |
| ISO 32000-1 7.3.10 | An indirect object is its number and generation, obj, the value, endobj; a new file's generations are 0. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_9_ObjectKeywordsAreSeparatedByOneSpaceAndSurroundedByEols` |
| ISO 32000-1 7.4 | A stream's filters are named in /Filter; parameters in /DecodeParms. | SAVE, EMPTY | compliant | `Iso19005_2_6_1_7_2_OnlyFlateDecodeIsUsed` |
| ISO 32000-1 7.5.2 | The first line is %PDF- and a version 1.N, N from 0 to 7. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 32000-1 7.5.3 | The body is a sequence of indirect objects. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_9_ObjectKeywordsAreSeparatedByOneSpaceAndSurroundedByEols` |
| ISO 32000-1 7.5.4 | A table section begins with xref; a never-updated file has one subsection from 0; each entry is exactly 20 bytes; object 0 heads the free list. | SAVE, LIN, SIGN | compliant | `Iso32000_1_7_5_4_XrefEntriesAreTwentyBytesAndPointAtTheirObjects` |
| ISO 32000-1 7.5.5 | The trailer has /Size (one above the highest number), /Root, startxref naming the table, and %%EOF as the last line. | SAVE, OBJ, LIN, SIGN | compliant | `Iso32000_1_7_5_4_XrefEntriesAreTwentyBytesAndPointAtTheirObjects` |
| ISO 32000-1 7.5.6 | An incremental update appends a section with /Prev (written only by the Signing package's later builders). | - | not applicable | none |
| ISO 32000-1 7.5.7 | An object stream holds no stream, no encryption dictionary, nothing with a generation other than 0. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-1 7.5.8 | A cross-reference stream has direct /W and /Size, an indirect /Root, entries whose type-1 offset is the object's. | OBJ | compliant | `Iso32000_1_7_5_4_XrefEntriesAreTwentyBytesAndPointAtTheirObjects` |
| ISO 32000-1 7.9.2 | A text string is PDFDocEncoding or UTF-16BE with the mark FE FF; a language escape may be inside. | SAVE, SIGN, STR | compliant | `Iso32000_1_7_9_2_TextStringsAreUtf16BeWithTheMarkAndSignatureTextIsPdfDocEncodedWhenItCan` |
| ISO 32000-1 7.9.2.2 | A UTF-16BE text string is well-formed UTF-16 (an unpaired surrogate is not). | SAVE (Info, Lang, outline, page label) | gap #670 | `Iso32000_1_7_9_2_2_InfoStringsWithALoneSurrogateAreWellFormedUtf16` |
| ISO 32000-1 7.9.4 | A date has the form D:YYYYMMDDHHmmSSOHH'mm with every field in range. | SIGN | gap #743 | `Iso32000_2_7_9_4_SignatureDateEndsAtTheMinutesInAPdf20File` |
| ISO 32000-1 7.9.6, 7.9.7 | Name and number trees: keys sorted, /Limits on every node but the root, none on the root. | TREE | compliant | `Iso32000_1_7_9_6_NameTreeKeysAreSortedEveryNodeHasItsLimitsAndTheRootHasNone` |
| ISO 32000-1 14.3.2 | A metadata stream dictionary has /Type /Metadata and /Subtype /XML, and holds XML. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute` |
| ISO 32000-1 14.3.3 | /Info is optional and its values are text strings. | SAVE | compliant | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 32000-1 14.4 | /ID is an array of two byte strings, both the same when a file is first written. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 32000-1 14.4 | A pinned DocumentId is exactly 16 bytes; any other length is refused at the setter. | ID | compliant | `Iso32000_1_14_4_DocumentIdOfAnyOtherLengthThanSixteenIsRefusedAtTheSetter` |
| ISO 32000-1 Annex C.2 | A classic table holds offsets of ten digits, so a file is limited to 10^10 bytes. | SAVE | compliant | `Iso32000_1_C_2_ClassicXrefRefusesAnOffsetOfElevenDigitsAndAcceptsTenNines` |
| ISO 32000-1 Annex C, Table C.1 | The string limit of 32767 applies to strings in content streams only; PDF/A applies it to every string. | SAVE | gap #666 | `Iso19005_2_6_1_13_StringsOver32767BytesAreNotWrittenUnderAPdfAClaim` |
| ISO 32000-2 7.5.2 | A file under no claim names its version as 2.0 in the header (or the catalog /Version). | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 32000-2 7.5.2 | A file with binary data has a comment of four bytes above 127 right after the header. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 32000-2 Table 15 | /ID is required in a 2.0 file; each string is at least 16 bytes; /Size, /Root indirect. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 32000-2 7.5.8 | A cross-reference stream's entries are direct and it has no Crypt filter. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-2 7.9.4 | A date ends at the minutes offset; a terminating apostrophe is the form up to 1.7 and readers should accept it. | SIGN | gap #743 | `Iso32000_2_7_9_4_SignatureDateEndsAtTheMinutesInAPdf20File` |
| ISO 32000-2 14.3.3, Table 15 | Info keys other than the two dates are deprecated (a should); written values are text strings. | SAVE | gap #325 | `Iso32000_2_14_3_3_InfoOfAPdf20FileHoldsOnlyTheTwoDates` |
| ISO 32000-2 14.3.4 | When Info and the metadata stream both carry the creation date they are equivalent; the library writes no Info date. | SAVE | not applicable | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 32000-2 14.3.2 | The metadata XML follows ISO 16684-1. | SAVE | gap #742 | `Iso16684_1_7_3_2_XpacketBeginHoldsTheByteOrderMarkOrNothing` |
| ISO 16684-1 7.3.2 | The packet header is exactly the form with begin set to U+FEFF or empty, and a trailer end=r or w. | SAVE | gap #742 | `Iso16684_1_7_3_2_XpacketBeginHoldsTheByteOrderMarkOrNothing` |
| ISO 14289-1 5 | The catalog metadata holds pdfuaid:part with the part number (1). | SAVE | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 14289-1 6.1 | The file version is 1.0 to 1.7 and does not decide conformance. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 14289-1 7.1 | The catalog's metadata stream has a dc:title. | SAVE | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 14289-1 7.1 | A claim without a title is refused or carries one (the save stamps the claim and writes no dc:title). | SAVE | gap #474 | `Iso14289_1_7_1_PdfUA1RefusesOrCarriesATitleAndALanguage` |
| ISO 14289-1 7.1 | ViewerPreferences is present with DisplayDocTitle true. | SAVE, SIGN | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 14289-1 7.1 | Suspects is false (the key is absent). | SAVE | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 14289-1 7.1 | An Info dictionary may be present and a reader ignores it. | SAVE | compliant | `Iso32000_2_14_3_3_InfoValuesAreTextStringsAndNoDeprecatedKeyIsWrittenUnset` |
| ISO 14289-1 7.2 | The catalog /Lang, when present, is a language identifier; natural language of the metadata is determined by it. | SAVE, STR | compliant | `Iso19005_2_6_7_4_And_Iso14289_1_7_2_CatalogLangIsALanguageTagMatchedByXmpAndByItsTrim` |
| ISO 14289-1 7.16 | An encrypted PDF/UA-1 file keeps the permission to extract for accessibility. | SAVE | compliant | `Iso14289_1_7_16_PdfUA1MayBeEncryptedOnlyWithExtractAndThenTheTrailerHoldsAnId` |
| ISO 32000-1 7.7.2 | The catalog has /Type /Catalog, /Pages and /Metadata; the signing path writes the same entries as Save plus /AcroForm. | SIGN, SAVE (#635, #626) | compliant | `Iso32000_1_7_7_2_SaveAndSigningPathWriteTheSameCatalogEntriesBesidesAcroForm` |
| ISO 32000-1 14.3 | A pinned Timestamp is written in the XMP dates in the given offset, and the same pins give the same bytes. | ID | compliant | `Iso32000_1_14_3_PinnedTimestampAndDocumentIdGiveIdenticalBytesOnEverySavePath` |
| ISO 32000-1 14.4 | A derived /ID follows the title and timestamp, 16 bytes, both strings equal. | ID | compliant | `Iso32000_1_14_4_DerivedIdIsSixteenBytesAndFollowsTheTimestampAndTitle` |
| ISO 19005-2 6.1.13 (signing path) | The placeholder's /ByteRange holds four integers above int32 until the signature patches them; the delivered file does not. | SIGN | compliant | `Iso19005_2_6_1_13_PrepareForSigningWritesTenNinesInByteRangeAndSigningReplacesThem` |
| ISO 19005-2 6.1.7.1 (empty page) | An empty page's content stream is the 8-byte empty zlib stream, /Length 8, accepted by qpdf, poppler and veraPDF. | EMPTY | compliant | `Iso19005_2_6_1_7_1_AnEmptyPageContentIsTheEmptyZlibStreamWithItsLengthAndPassesTheTools` |
| ISO 19005-2 6.1 (all paths) | veraPDF 2b, 2u, 2a and ua1 and qpdf accept the saved file of every path. | SAVE, OBJ, LIN, SIGN | compliant | `Oracles_AcceptEveryClaimedFixture` |
| Reader recovery bounds (#623) | Bounds what damaged-file recovery allocates; the Reader writes nothing in recovery. | RECOV | not applicable | none |

### Needs NEN

ISO 19005-2 clauses the maintainer's reading did not complete, and what each would decide:

- 6.1.1: No oracle rule; the text says data not described may not be used to render. Needs the full sentence to decide whether anything the writer emits (for example the signature placeholder's sentinel comment) counts.
- 6.1.7.2: The notes say 'read the rest' for the Crypt filter conditions; the writer has no Crypt filter, so the row is not applicable here, but the exact conditions are not in the notes.
- 6.6.3: The notes call the Info-to-XMP crosswalk 'read Table', and one note gives it as 'should'. If the 2011 text makes any consistency a 'shall', the Producer row (Info without Producer against an XMP pdf:Producer of VellumPdf) needs a second look.
- 6.6.5: The notes say 'read' for how the changing half of /ID is handled when xmpMM:History is added; the writer adds no History, and the text is not in the notes.
- 6.7.4: The language-escape sentence for UTF-16 text strings is marked 'read' in the notes; the writer never writes an escape.

## G6 Forms

No rows yet.
