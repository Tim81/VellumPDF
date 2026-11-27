# Conformance of the v2.4 features, clause by clause

This page records, for each group of features that v2.4 added or changed, which clauses of the
governing standards the written file is held to, what the file does, and which test measures it. A
verdict is one of compliant, gap (with the issue that tracks it), not applicable (with what was
searched to decide that), or human-only (a check a person makes, with the Matterhorn checkpoint where
one exists).

The clause text is not reproduced. Requirements are paraphrased from ISO 32000-1, ISO 32000-2,
ISO 19005-2 and ISO 14289-1. ISO 19005-2 clause numbers come from notes taken from a licensed copy.
veraPDF has no rule for several of the requirements below, so the tests also assert the written keys
and operands directly.

Tests are named `Iso<standard>_<clause>_<what is required>`, and each group has its own test class.

## G1 Navigation

Tests: `ConformanceAuditG1Tests` in `tests/VellumPdf.Kernel.Tests`. The fixtures are generated in the
test with fonts from the tree, and are checked with veraPDF (2b, 2u, 2a and ua1, as each fixture
claims), qpdf and poppler.

| Standard and clause | Requirement | Feature | Verdict | Test |
|---|---|---|---|---|
| 32000-1/-2 7.9.6 | A name tree has one root holding Kids or Names, never both; a root with Names is the only node | Dests name tree (#622, #627, #651) | compliant | `Iso32000_1_7_9_6_NameTreeNodesFollowTable36` |
| 32000-1/-2 7.9.6, Table 36 | Every other node has Limits and Kids (intermediate) or Limits and Names (leaf); Kids are indirect references | Dests name tree | compliant | `Iso32000_1_7_9_6_NameTreeNodesFollowTable36` |
| 32000-2 Table 36; 32000-1 Table 36 | The root carries no Limits (2.0 forbids it; 1.7 requires it only below the root) | Dests name tree | compliant | `Iso32000_1_7_9_6_NameTreeNodesFollowTable36` |
| 32000-1/-2 7.9.6 | Keys sort ascending byte by byte, a shorter key before a longer one with the same prefix; any self-consistent encoding | Dests name tree | compliant | `Iso32000_1_7_9_6_KeysAreOrderedByteByByteAndAPrefixSortsFirst`, `Iso32000_1_7_9_6_KeysAreComparedAsUnsignedBytes` |
| 32000-1/-2 7.9.6 | Leaf key ranges do not overlap, and Limits are the least and greatest key below the node | Dests name tree | compliant | `Iso32000_1_7_9_6_LimitsAreTheLeastAndGreatestKeysWhenKeysUseTheHighBytes` |
| 32000-1/-2 7.9.6 | A stream value is indirect; array, dictionary and string values should be | Dests name tree | compliant | `Iso32000_1_7_9_6_ValuesOfTheDestsTreeAreIndirectAndAStreamMustBe` |
| 32000-1/-2 7.9.6 | A key appears once | named destinations | compliant | `Iso32000_1_7_9_6_AKeyAppearsOnce` |
| 32000-2 J.3.3 | Names compare as canonical string bytes | Dest in a link, key in the tree | compliant | `Iso32000_1_12_5_6_5_EveryLinkNamesOneTargetAndADestinationNameResolves` |
| 32000-1/-2 7.9.7 | Number-tree keys are integers in ascending order, entries in Nums, Limits are two integers, none on the root | PageLabels number tree | compliant | `Iso32000_1_7_9_7_NumberTreeKeysAscendAndLimitsAreIntegers` |
| 32000-1/-2 12.3.2.2 | A destination is [page /XYZ left top zoom]; page is an indirect reference to a page object; null keeps the value | named destinations, outline items | compliant for Layout; gap for an outline entry for a page the document lacks (#737) | `Iso32000_1_12_3_2_2_ADestinationIsAPageReferenceAndFiveOperands`, `Iso32000_1_12_3_2_3_EveryAnchorIsANamedDestinationOnItsPage`, `Iso32000_1_12_3_2_2_AnOutlineEntryForAPageOfAnotherDocumentIsRefused` (skipped) |
| 32000-1 12.3.2.3; 32000-2 12.3.2.4 | Named destinations are strings in the Dests tree of the name dictionary, not the catalog Dests dictionary | anchors (#676, #714) | compliant | `Iso32000_1_12_3_2_3_NamesAreAKeyedTreeInTheNameDictionaryNotACatalogDictionary` |
| 32000-1 12.3.2.3; 32000-2 12.3.2.4 | An anchor on a heading, list, list item, paragraph, table, note block, preformatted block or image is a destination on the page and at the height where the element is drawn (the page of each of the twelve is pinned; the height is measured from poppler word boxes and the image cm), also when the paragraph splits | anchors on all eight element kinds | compliant | `Iso32000_1_12_3_2_3_EveryAnchorIsANamedDestinationOnItsPage`, `Iso32000_1_12_3_2_3_EveryDestinationIsAtTheHeightWhereItsElementIsDrawn`, `Iso32000_1_12_3_2_3_AnAnchorOnAParagraphThatSplitsIsAtItsFirstLine`, `Iso32000_1_12_3_2_3_PopplerResolvesEveryDestinationToTheSamePage` |
| 32000-2 12.3.2.3 | A structure destination names a structure element | not written | not applicable (#600 is open; no /SD is written anywhere in src) | none |
| 32000-1 12.3.3, Tables 152-153; 32000-2 Tables 150-151 | Outline root and items: First and Last, Parent, Prev and Next as indirect references, in linked-list order | bookmarks (#637) | compliant | `Iso32000_1_12_3_3_TheOutlineIsALinkedListWhoseCountsFollowTable153`, `Iso32000_1_12_3_3_EachBookmarkPointsWhereItsHeadingIsDrawn` |
| 32000-1/-2 12.3.3 | Count of an open item is its visible descendants, of a closed item the negative of the descendants an opening would show; the root Count is the visible total | closed heading bookmarks (#637) | compliant | `Iso32000_1_12_3_3_TheOutlineIsALinkedListWhoseCountsFollowTable153`, `Iso32000_1_12_3_3_ASplitHeadingHasOneBookmarkAtItsFirstLine` |
| 32000-2 Table 150; 32000-1 Table 152 | The root Count is omitted if there are no open items; the table does not say whether a childless item is open | flat outline, and an outline whose only item with children is closed | human-only: the clause is ambiguous. The writer reads a childless item as visible and writes the root Count whenever there is a top-level item: a flat outline of three has root Count 3, and an outline whose one parent is closed has root Count 1 beside the parent's Count -1 | `Iso32000_1_12_3_3_TheOutlineIsALinkedListWhoseCountsFollowTable153` |
| 32000-1/-2 12.3.3 | Title is a text string; Dest and A are exclusive | bookmark titles | compliant; gap for an unpaired surrogate (#738) | `Iso32000_1_12_3_3_EachItemHasATextStringTitleAndAnExplicitDestinationNotAnAction`, `Iso32000_2_7_9_2_2_ABookmarkTitleIsWellFormedUtf16` (skipped) |
| 32000-1/-2 12.3.3 | C, F and SE are optional | outline | not applicable (BuildOutlineTree sets none of them) | none |
| 32000-1/-2 12.4.2 | PageLabels is a number tree keyed by the first page index of each range, and has a value for index 0 | page labels (#651, #676) | compliant | `Iso32000_1_12_4_2_TheTreeIncludesAValueForPageIndexZero`, `Iso32000_1_12_4_2_AGivenRangeAtPageIndexZeroIsKept` |
| 32000-1 Table 159; 32000-2 Table 161 | S is D, R, r, A or a; P is a text string; St is 1 or more; no other entry | page labels | compliant | `Iso32000_1_12_4_2_ALabelDictionaryHoldsOnlyTable159EntriesWithDefinedValues` |
| 32000-1/-2 12.4.2 | Pages number sequentially from the range start; letters repeat (Z, AA, BB) | page labels, Layout ranges | compliant | `Iso32000_1_12_4_2_LabelsCountFromEachRangesStartInTheDefinedNumberingStyles`, `Iso32000_1_12_4_2_LayoutRangesLabelThePagesTheyStartOn` |
| 32000-2 12.4.2 | Ranges do not overlap | page labels | compliant | `Iso32000_1_12_4_2_StartMustBeOneOrMoreAndAKeyIsUsedOnce`, `Iso32000_1_12_4_2_ARangeMayNotStartPastTheLastPage` |
| 32000-1/-2 12.4.2 | A page reference prints the label the file gives its page | page references (#714) | compliant | `Iso32000_1_12_4_2_APageReferencePrintsTheLabelThePageHasInTheFile`, `Iso32000_1_12_4_2_ALabelFollowsABlankRectoPageAndTheTreeAgreesWithThePrintedReferences` |
| 32000-1/-2 12.5.2 | An annotation dictionary is in the Annots of one page only; Subtype and Rect are present | links | compliant | `Iso32000_1_12_5_2_AnAnnotationDictionaryIsInTheAnnotsOfOnePage`, `Iso32000_1_12_5_2_ARectIsFourNumbersInsideThePageAndHasArea` |
| 32000-1/-2 12.5.3 | F bits not defined are 0 | links | compliant | `Iso32000_1_12_5_3_ALinkIsPrintedAndHasNoOtherFlag` |
| 32000-1/-2 12.5.4 | Border is three numbers; a width of 0 draws no border | links | compliant (Border [0 0 0]) | `Iso32000_1_12_5_3_ALinkIsPrintedAndHasNoOtherFlag` |
| 32000-2 Table 166 | A Link needs no appearance dictionary | links | compliant (none is written) | `Iso19005_2_6_3_ALinkAnnotationNeedsFlagsAndNoAppearance` |
| 32000-1/-2 12.5.6.5 | A Link has A or Dest, not both; Dest is an array, name or byte string | links by URI and by anchor (#714, #475) | compliant | `Iso32000_1_12_5_6_5_EveryLinkNamesOneTargetAndADestinationNameResolves`, `Iso32000_1_TheNavigationFixtureWithoutAClaimPassesQpdf` |
| 32000-1/-2 12.5.6.5 | QuadPoints, when present, lie inside Rect | a link that wraps | not applicable today (none is written; each line gets its own Rect). It becomes required once links are tagged: see the next row | `Iso32000_1_12_5_2_ALinkThatWrapsHasOneRectanglePerLineAndStaysInTheColumn` |
| 32000-1/-2 12.5.2 | The rectangle locates the link | justified embedded-font, cell and band links (#659, #475) | compliant (positions checked against the column and the margins) | `Iso32000_1_12_5_2_ALinkThatWrapsHasOneRectanglePerLineAndStaysInTheColumn`, `Iso32000_1_12_5_2_ACellAndABandLinkLieInsideTheirBoxes` |
| 32000-1 12.6.4.7, Table 206 | A URI action has S /URI and a URI of 7-bit ASCII (UTF-8 in 2.0) | LinkUri | compliant | `Iso32000_1_12_6_4_7_AUriIsWrittenAsSevenBitAsciiAndNonAsciiIsPercentEncoded` |
| 32000-1 14.8.4.4.2; 32000-2 14.8.4.7.3 | A tagged link whose text wraps is one annotation with QuadPoints for its areas, referenced by one OBJR | a wrapping link (#659) under 2a and UA-1 | not applicable today (no Link element is written, so the condition is not met); constrains the fix of #550, where the requirement is recorded | `Iso32000_1_12_5_2_ALinkThatWrapsHasOneRectanglePerLineAndStaysInTheColumn` |
| 32000-1 14.8.4.4.2, 14.7.4.4 | A tagged link is a Link element holding an OBJR, with a StructParent on the annotation | links under 2a and UA-1 | gap #550 | `Iso14289_1_7_18_5_LinksAreTaggedAsLinkElementsAndCarryContents` (skipped) |
| 19005-2 6.1.13 | Integers fit 32 bits | number tree, Count, Rect | compliant | `Iso32000_1_7_9_7_NumberTreeKeysFitAThirtyTwoBitInteger`, `Iso19005_2_TheNavigationFixtureIsAcceptedByVeraPdfAndQpdf` |
| 19005-2 6.1.13 | A string is at most 32,767 bytes; a name at most 127 | destination names, label prefixes, tree keys | compliant | `Iso19005_2_6_1_13_TheBoundsTheLibraryAppliesToNavigationStringsStayWithinTheLimits`, `Iso19005_2_6_1_13_EveryNavigationStringIsWithinTheLimits` |
| 19005-2 6.1.13 | A string is at most 32,767 bytes | a URI, a bookmark title (given, or taken from the heading text) | gap (#666) | `Iso19005_2_6_1_13_AUriOrABookmarkTitleLongerThan32767BytesIsRefusedOrNotWritten` (skipped) |
| 19005-2 6.3.1-6.3.3 | Only Link annotations here; F present with Print set to 1 and Hidden, Invisible, NoView, ToggleNoView 0; a Link needs no AP | links under 2b, 2u, 2a | compliant | `Iso19005_2_6_3_ALinkAnnotationNeedsFlagsAndNoAppearance` |
| 19005-2 6.5.1, 6.5.2 | Only allowed actions (URI here); no AA on a Widget, a Field, the Catalog or a Page | links | compliant | `Iso19005_2_6_5_TheOnlyActionsAreUriActionsAndNothingHasAdditionalActions` |
| 19005-2 6.7.6 | Contents on an annotation that shows no text (level A, should) | links under 2a | gap #550 | `Iso19005_2_6_7_6_AnnotationsThatShowNoTextCarryContents` (skipped) |
| 14289-1 7.18.1, 7.18.5 | Links are tagged as Link elements and carry Contents | LinkUri under UA-1 | gap #550 | `Iso14289_1_7_18_5_ALinkUnderUa1FailsExactlyTheRulesOfIssue550` |
| 14289-1 7.18.5 | A link to an anchor would be untagged | LinkAnchor under UA-1 | compliant for Layout (refused at the save); the Kernel does not refuse: see the next row | `Iso14289_1_7_18_5_ALinkToAnAnchorIsRefusedUnderUa1BecauseItWouldBeUntagged` |
| 14289-1 7.18.1, 7.18.5 | A Kernel link (DestName, Uri or DestPage) under a UA-1 claim is tagged and has Contents | Kernel link by name (#651) | gap #745 (v2.4: a refusal, as Layout has) | `Iso14289_1_7_18_5_AKernelLinkByNameUnderUa1FailsTheRulesOfIssue745`, `Iso14289_1_7_18_5_AKernelLinkUnderUa1IsRefusedOrTaggedWithContents` (skipped) |
| 14289-1 7.18.5 | A Layout LinkUri link under UA-1 is tagged and has Contents | LinkUri under UA-1 | gap #550, left in v2.5 by the maintainer's milestone split although the file is invalid under the claim; #745 is the v2.4 refusal | none |
| 14289-1 7.18.5 | IsMap is not true | LinkUri | compliant (not written) | `Iso32000_1_12_6_4_7_AUriIsWrittenAsSevenBitAsciiAndNonAsciiIsPercentEncoded` |
| 14289-1 7.18.3 | Every page with an annotation has Tabs S | links under UA-1 | compliant | `Iso14289_1_7_18_3_EveryPageWithAnAnnotationDeclaresTabsS` |
| 14289-1 7.17 | An outline should match the reading order and level of the targets | bookmarks | gap (#736) when a level is skipped | `Iso14289_1_7_17_TheOutlineListsHeadingsInTheOrderTheyOccur` (skipped), `Iso14289_1_7_17_AnOutlineWhoseLevelsSkipIsNestedUnderTheEarlierItemOfTheLevelAbove` |
| 14289-1 7.17 | PageLabels entries should be semantically appropriate | page labels | human-only (Matterhorn 27-001 requires no test) | none |
| 14289-1 7.2 | The natural language of bookmark text is determinable | bookmarks under UA-1 | compliant (catalog Lang; Matterhorn 11-003) | `Iso14289_1_7_17_And7_2_ADocumentWithAnOutlineAndLabelsPassesUa1AndDeclaresItsLanguage` |
| 14289-1 7.18.1 | Annotations are in the structure tree in reading order | links | gap #550; the reading order itself is human-only (Matterhorn 28-001) | none |

## G2 Text and fonts

## G3 Structure and artifacts

## G4 Graphics and images

## G5 File structure and metadata

Audited at origin/main bbf317b, 68 commits after v2.3.3. Fixtures are saved in the test with the fonts in the tree, under no claim and under PDF/A-2b, 2u, 2a and PDF/UA-1, through six paths: the Layout document, the Kernel document, object streams, linearization, the unsigned placeholder of the signing path, and the signed file. `AuditScanner` reads the saved bytes without the library's Reader. veraPDF 1.30.4 and qpdf 12.4.2 run on every claimed fixture, and veraPDF is not run on the unsigned placeholder, because its /ByteRange holds ten nines until the signature patches it.

108 rows: 77 compliant, 21 gap, 10 not applicable, 0 human-only.

Feature key: SAVE is Save and SaveAsync on the Kernel and Layout documents; OBJ is UseObjectStreams; LIN is Linearize; SIGN is PrepareForSigning and Sign, which share the catalog builder with Save; ID is Timestamp and DocumentId; NUM is reals outside int32; EMPTY is the empty zlib stream; STR is literal-string line ends, Lang and signature text strings; TREE is the name and number tree writers; RECOV is the Reader's recovery bounds.

| Standard and clause | Requirement | Feature | Verdict | Test |
|---|---|---|---|---|
| ISO 19005-2 5 | Level B leaves out the Unicode map and structure rules, U leaves out structure, A needs everything; the header version is not a criterion. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.1.1 | A reader ignores data the standards do not describe and never renders from it; the clause binds no writer beyond the rest of clause 6. | - | not applicable | none |
| ISO 19005-2 6.1.2 | The header is %PDF-1.n at byte 0, n from 0 to 7, then one EOL. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 19005-2 6.1.2 | A comment of at least four bytes above 127 follows the header line. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 19005-2 6.1.3 | The trailer has an /ID of file identifiers. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 19005-2 6.1.3 | The trailer has no /Encrypt; the save refuses encryption under every PDF/A level. | SAVE, SIGN | compliant | `Iso19005_2_6_1_3_EncryptIsRefusedAtSaveForEveryPdfALevelOnEverySavePath` |
| ISO 19005-2 6.1.3 | Nothing follows the last %%EOF except one optional EOL. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 19005-2 6.1.4 | One EOL separates the xref keyword from the subsection header. | SAVE, LIN, SIGN | compliant | `Iso19005_2_6_1_4_XrefKeywordAndSubsectionHeaderAreSeparatedByOneEol` |
| ISO 19005-2 6.1.5 | An Info dictionary may be present and is ignored by a reader. | SAVE | compliant | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 19005-2 6.1.6 | A hex string has an even number of digits and only hex digits. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_6_HexStringsHaveAnEvenNumberOfDigits` |
| ISO 19005-2 6.1.7.1 | stream is followed by LF or CRLF, endstream by an EOL, and /Length is the exact byte count. | SAVE, OBJ, LIN, SIGN, EMPTY | compliant | `Iso19005_2_6_1_7_1_StreamKeywordLengthAndEndstreamFollowTheSyntax` |
| ISO 19005-2 6.1.7.1 | A stream dictionary has no F, FFilter or FDecodeParms. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_7_1_StreamKeywordLengthAndEndstreamFollowTheSyntax` |
| ISO 19005-2 6.1.7.2 | Only the standard filters except LZWDecode; Crypt only as Identity; no other filter. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_7_2_OnlyFlateDecodeIsUsed` |
| ISO 19005-2 6.1.7.2 | Crypt only when its decode parameters name Identity; no filter outside Table 6. No writer path emits LZW, Crypt or an unlisted filter (searched Kernel, Layout, Signing, Barcodes and Fonts.Standard14 for the filter names; only the Reader and Conformance mention them). | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_7_2_OnlyFlateDecodeIsUsed` |
| ISO 19005-2 6.1.8 | Font names, colourant names and structure type names are valid UTF-8 after # expansion. | SAVE (font names), SIGN | gap #740 | `Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim` |
| ISO 19005-2 6.1.9 | Object, generation and obj are separated by one space, and obj and endobj are surrounded by EOLs. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_9_ObjectKeywordsAreSeparatedByOneSpaceAndSurroundedByEols` |
| ISO 19005-2 6.1.10 | An inline image's /F is not LZW, Crypt or outside Table 6. | - | not applicable | none |
| ISO 19005-2 6.1.11 | Linearization is allowed; a reader ignores it. | LIN | compliant | `Iso19005_2_6_1_11_LinearizedFileIsAllowedAndItsParameterDictionaryIsFirst` |
| ISO 19005-2 6.1.12 | A permissions dictionary holds only UR3 and DocMDP, with conditions on DocMDP. | - | not applicable | `Iso19005_2_6_1_12_And_6_11_CatalogHasNoPermsAndNoRequirements` |
| ISO 19005-2 6.1.13 | No integer outside -2147483648 to 2147483647. | SAVE, OBJ, LIN, SIGN, NUM | compliant | `Iso19005_2_6_1_13_IntegerTokensStayWithinInt32AndRealsCarryNoExponent` |
| ISO 19005-2 6.1.13 | No real outside +-3.403e38. | SAVE, NUM | gap #666 | `Iso19005_2_6_1_13_RealsAbove3_403e38AreNotWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | No string longer than 32767 bytes (a string of exactly 32767 is within it). | SAVE, SIGN | gap #666 | `Iso19005_2_6_1_13_StringsOver32767BytesAreNotWrittenUnderAPdfAClaim`, `Iso19005_2_6_1_13_StringsOfExactly32767BytesAndTitlesOfExactly32766AreWrittenInFull` |
| ISO 19005-2 6.1.13 | No name longer than 127 bytes (a name of exactly 127 is within it). | SAVE, SIGN | gap #740 | `Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim`, `Iso19005_2_6_1_13_NamesOfExactly127BytesAreWrittenInFullAndPassVeraPdf` |
| ISO 19005-2 6.1.13 | No nonzero real closer to zero than +-1.175e-38. | SAVE, NUM | compliant | `Iso19005_2_6_1_13_RealsAtTheBoundariesAreWrittenAsTheClauseAllows` |
| ISO 19005-2 6.1.13 | At most 8388607 indirect objects. | SAVE, OBJ, LIN, SIGN | gap #666 | `Iso19005_2_6_1_13_AtMost8388607IndirectObjectsAreWrittenUnderAPdfAClaim` |
| ISO 19005-2 6.1.13 | q/Q nesting at most 28 deep. | SAVE (Layout renderers; Kernel canvas) | gap #666 | `Iso19005_2_6_1_13_QqNestingOfTwentyNineIsNotWrittenUnderAPdfAClaim`, `Iso19005_2_6_1_13_LayoutOutputNestsQAtMostOneDeepWhereKernelCallerCanNestPastTwentyEight` |
| ISO 19005-2 6.1.13 | A DeviceN colour space has at most 32 colourants. | - | not applicable | none |
| ISO 19005-2 6.1.13 | No CID above 65535. | - | not applicable | none |
| ISO 19005-2 6.1.13 | A page boundary is between 3 and 14400 units in each direction. | SAVE (Kernel AddPage, Layout PageSize) | gap #666 | `Iso19005_2_6_1_13_PageBoundariesOutsideThreeToFourteenThousandFourHundredAreNotWrittenUnderAPdfAClaim`, `Iso19005_2_6_1_13_PageBoundariesOfThreeAndFourteenThousandFourHundredAreWritten` |
| ISO 19005-2 6.2.3 | A PDF/A output intent (S GTS_PDFA1) carries an ICC profile as /DestOutputProfile (written by the shared catalog builder). | SAVE, SIGN | compliant | `Iso19005_2_6_2_3_OutputIntentIsGtsPdfa1WithAnIccProfileOnEverySavePath` |
| ISO 19005-2 6.6.2.1 | The catalog /Metadata is an XMP stream with /Type /Metadata and /Subtype /XML. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute` |
| ISO 19005-2 6.6.2.1 | The packet header has no bytes and no encoding attribute; the packet is UTF-8. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute` |
| ISO 19005-2 6.6.2.1 | Every packet is well-formed XML 1.0 (and conforms to the XMP specification). | SAVE (Info, Language text) | gap #741 | `Iso19005_2_6_6_2_1_XmpStaysWellFormedForEveryStringTheWriterAccepts` |
| ISO 19005-2 6.6.2.1 | Entities and control characters in text are escaped or dropped so the packet parses. | SAVE | compliant | `Iso19005_2_6_6_2_1_XmpTextIsEscapedAndIllegalControlCharactersAreDropped` |
| ISO 19005-2 6.6.2.2 | Suggested prefixes dc, pdf, xmp, xmpMM. | SAVE | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.2.3.1 | Every property is in a predefined schema or a described extension schema. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.2.3.2-3 | Extension schemas use the container and carry every field of Tables 2 to 6. | - | not applicable | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.3 | A writer should keep Info consistent with the XMP per Table 7 (Title, Author, Subject, Keywords, Creator, Producer and the two dates); the library writes no Info date, and an unset Producer is absent from Info while the XMP names VellumPdf. | SAVE | compliant | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText`, `Iso19005_2_6_6_3_ProducerIsInInfoOnlyWhenSetAndTheXmpAlwaysNamesOne` |
| ISO 19005-2 6.6.3 | The Info and XMP text of one string match for every string the writer accepts; a carriage return and U+0001 differ. | SAVE | gap #747 | `Iso19005_2_6_6_3_InfoAndXmpCarryTheSameTextForEveryStringTheWriterAccepts` |
| ISO 19005-2 6.6.4 | pdfaid:part is 2 and pdfaid:conformance is A, B or U; the prefix is pdfaid; amd and corr only when used. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 19005-2 6.6.5 | Identifiers should be present; if an xmpMM:History entry is added the changing half of /ID shall change. No path writes History or any xmpMM property, and the two /ID strings are equal. | SAVE, OBJ, LIN, SIGN, ID | not applicable | `Iso19005_2_6_6_5_NoSavePathWritesXmpHistoryOrIdentifiers` |
| ISO 19005-2 6.6.6 | A History entry has action, parameters and when; none is written. | - | not applicable | `Iso19005_2_6_6_5_NoSavePathWritesXmpHistoryOrIdentifiers` |
| ISO 19005-2 6.7.4 | The catalog /Lang should hold the default language as a language identifier. | SAVE, STR | compliant | `Iso19005_2_6_7_4_And_Iso14289_1_7_2_CatalogLangIsALanguageTagMatchedByXmpAndByItsTrim`, `Iso19005_2_6_7_4_LanguageIsTrimmedWhereWrittenAndOmittedWhenBlank` |
| ISO 19005-2 6.7.4 | A /Lang that is not a language identifier is refused (the writer does not validate it); a shall under level A. | SAVE | gap #749 | `Iso19005_2_6_7_4_LanguageThatIsNotATagIsNotWrittenUnderPdfA2a`, `Iso14289_1_7_2_LanguageThatIsNotATagIsNotWrittenUnderPdfUA1` |
| ISO 19005-2 6.7.4 | A structure element /Lang is a language identifier too (Layout writes one from Paragraph.Language). | SAVE (Layout, tagged) | compliant | `Iso19005_2_6_7_4_StructureElementLangIsALanguageIdentifierUnderPdfA2a` |
| ISO 19005-2 6.11 | The catalog has no Requirements key. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_12_And_6_11_CatalogHasNoPermsAndNoRequirements` |
| ISO 32000-1 7.2 | Lexical conventions: white space, delimiters, regular characters, comments. | SAVE, OBJ, LIN, SIGN | compliant | `Oracles_QpdfAcceptsEveryFixtureUnderEveryClaim` |
| ISO 32000-1 7.3.2 | Boolean objects are the keywords true and false. | SAVE, SIGN | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 32000-1 7.3.3 | An integer is decimal digits with an optional sign. | SAVE, NUM | compliant | `Iso19005_2_6_1_13_IntegerTokensStayWithinInt32AndRealsCarryNoExponent` |
| ISO 32000-1 7.3.3 | A real is decimal digits with a point; no exponent, no radix (NOTE 1). | SAVE, NUM | compliant | `Iso19005_2_6_1_13_RealsAtTheBoundariesAreWrittenAsTheClauseAllows` |
| ISO 32000-1 7.3.4.1, 7.3.4.2 | A literal string escapes unbalanced parentheses and backslash; a line end is not left raw. | SAVE, STR | compliant | `Iso32000_1_7_3_4_2_LiteralStringsEscapeParenthesesBackslashAndLineEnds` |
| ISO 32000-1 7.3.4.3 | A hex string is hex digits in angle brackets. | SAVE, SIGN | compliant | `Iso19005_2_6_1_6_HexStringsHaveAnEvenNumberOfDigits` |
| ISO 32000-1 7.3.5 | A name begins with a solidus; #, white space, delimiters and bytes outside ! to ~ are written as # and two hex digits. | SAVE | compliant | `Iso32000_1_7_3_5_NamesEscapeNumberSignSpaceAndDelimitersAndReadBackTheSame` |
| ISO 32000-1 7.3.5 | A name is bytes, and one that stands for text (a font or structure type name) should be read as UTF-8; the writer turns a character above U+00FF into '?'. | SAVE | gap #740 | `Iso19005_2_6_1_8_And_6_1_13_NamesAreUtf8AndAtMost127BytesUnderAPdfAClaim` |
| ISO 32000-1 7.3.5 | A name is a sequence of any 8-bit characters except null; the writer accepts the null character and writes it as #00. | SAVE | gap #740 | `Iso32000_1_7_3_5_ANameHoldsNoNullCharacter` |
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
| ISO 32000-1 7.5.6 | An incremental update's trailer holds every entry of the previous one except /Prev, and the second /ID string (the changing one, 14.4) is based on the file as last updated. Sign writes such updates at PadesLevel B_LT and B_LTA, also on a PDF/A document. | SIGN (B_LT, B_LTA) | gap #746 | `Iso32000_1_7_5_6_IncrementalTrailersKeepEveryEntryAndTheSecondIdChanges` |
| ISO 32000-1 7.5.7 | An object stream holds no stream, no encryption dictionary, nothing with a generation other than 0. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-1 7.5.7 | The N offset pairs of an object stream are in increasing order. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-1 7.5.7 | The first object is stored right after the last byte of the offset header. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-1 7.5.7 | No object in an object stream consists only of an object reference. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-1 7.5.7 | The keywords obj and endobj do not appear inside an object stream. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-1 7.5.8 | A cross-reference stream has direct /W and /Size, an indirect /Root, entries whose type-1 offset is the object's. | OBJ | compliant | `Iso32000_1_7_5_4_XrefEntriesAreTwentyBytesAndPointAtTheirObjects` |
| ISO 32000-1 7.9.2 | A text string is PDFDocEncoding or UTF-16BE with the mark FE FF; a language escape may be inside. | SAVE, SIGN, STR | compliant | `Iso32000_1_7_9_2_TextStringsAreUtf16BeWithTheMarkAndSignatureTextIsPdfDocEncodedWhenItCan` |
| ISO 32000-1 7.9.2.2 | A UTF-16BE text string is well-formed UTF-16 (an unpaired surrogate is not). | SAVE (Info, Lang, outline, page label) | gap #670 | `Iso32000_1_7_9_2_2_InfoStringsWithALoneSurrogateAreWellFormedUtf16` |
| ISO 32000-1 7.9.4 | A date has the form D:YYYYMMDDHHmmSSOHH'mm, which ends at the minutes, with every field in range; the signature /M date ends in an apostrophe in a file that names 1.7 as well as one that names 2.0. | SIGN | gap #743 | `Iso32000_1_7_9_4_SignatureDateEndsAtTheMinutesInAPdfAFile`, `Iso32000_2_7_9_4_SignatureDateHasEveryFieldInRangeAndNamesUtc` |
| ISO 32000-1 7.9.6, 7.9.7 | Name and number trees: keys sorted, /Limits on every node but the root, none on the root. | TREE | compliant | `Iso32000_1_7_9_6_NameTreeKeysAreSortedEveryNodeHasItsLimitsAndTheRootHasNone`, `Iso32000_1_7_9_7_NumberTreeKeysAreAscendingIntegersFromTheFirstPage` |
| ISO 32000-1 14.3.2 | A metadata stream dictionary has /Type /Metadata and /Subtype /XML, and holds XML. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_6_2_1_MetadataStreamIsUncompressedXmlWithNoBytesOrEncodingAttribute` |
| ISO 32000-1 14.3.3 | /Info is optional and its values are text strings. | SAVE | compliant | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 32000-1 14.4 | /ID is an array of two byte strings, both the same when a file is first written. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 32000-1 14.4 | A pinned DocumentId is exactly 16 bytes; any other length is refused at the setter. | ID | compliant | `Iso32000_1_14_4_DocumentIdOfAnyOtherLengthThanSixteenIsRefusedAtTheSetter` |
| ISO 32000-1 Annex C.2 | A classic table holds offsets of ten digits, so a file is limited to 10^10 bytes. | SAVE | compliant | `Iso32000_1_C_2_ClassicXrefRefusesAnOffsetOfElevenDigitsAndAcceptsTenNines` |
| ISO 32000-1 Annex C, Table C.1 | The string limit of 32767 applies to strings in content streams only; PDF/A applies it to every string. | SAVE | gap #666 | `Iso19005_2_6_1_13_StringsOver32767BytesAreNotWrittenUnderAPdfAClaim` |
| ISO 32000-2 7.5.2 | A file under no claim names its version as 2.0 in the header (or the catalog /Version). | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 32000-2 7.5.2 | A file with binary data has a comment of four bytes above 127 right after the header. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 32000-2 Table 15 | /ID is required in a 2.0 file; each string is at least 16 bytes; /Size is a direct integer and /Root an indirect reference. | SAVE, OBJ, LIN, SIGN, ID | compliant | `Iso19005_2_6_1_3_TrailerHoldsIdAndNoEncryptAndNothingFollowsEof` |
| ISO 32000-2 7.5.8 | A cross-reference stream's entries are direct and it has no Crypt filter. | OBJ | compliant | `Iso32000_1_7_5_7_ObjectStreamHoldsNoStreamNoEncryptAndItsLengthIsDirect` |
| ISO 32000-2 7.9.4 | A date ends at the minutes offset; NOTE 2 says a terminating apostrophe is the form up to 1.7 and readers should accept it, not that a writer produces it. | SIGN | gap #743 | `Iso32000_2_7_9_4_SignatureDateEndsAtTheMinutesInAPdf20File` |
| ISO 32000-2 14.3.3, Table 15 | Info keys other than the two dates are deprecated (a should); written values are text strings. | SAVE | gap #325 | `Iso32000_2_14_3_3_InfoOfAPdf20FileHoldsOnlyTheTwoDates` |
| ISO 32000-2 14.3.4 | When Info and the metadata stream both carry the creation date they are equivalent; the library writes no Info date. | SAVE | not applicable | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 32000-2 14.3.2 | The metadata XML follows ISO 16684-1. | SAVE | gap #742 | `Iso16684_1_7_3_2_XpacketBeginHoldsTheByteOrderMarkOrNothing` |
| ISO 16684-1 7.3.2 | The packet header is exactly the form with begin set to U+FEFF or empty, and a trailer end=r or w. | SAVE | gap #742 | `Iso16684_1_7_3_2_XpacketBeginHoldsTheByteOrderMarkOrNothing` |
| ISO 14289-1 5 | The catalog metadata holds pdfuaid:part with the part number (1). | SAVE | compliant | `Iso19005_2_6_6_2_3_1_And_6_6_4_PropertiesArePredefinedAndPdfaidNamesPartTwoAndTheLevel` |
| ISO 14289-1 6.1 | The file version is 1.0 to 1.7 and does not decide conformance. | SAVE, OBJ, LIN, SIGN | compliant | `Iso19005_2_6_1_2_HeaderIsPdfVersionAtByteZeroThenFourHighBytes` |
| ISO 14289-1 7.1 | The catalog's metadata stream has a dc:title. | SAVE | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 14289-1 7.1 | A claim without a title is refused or carries one (the save stamps the claim and writes no dc:title). | SAVE | gap #748 | `Iso14289_1_7_1_PdfUA1RefusesOrCarriesATitleAndALanguage` |
| ISO 14289-1 7.1 | ViewerPreferences is present with DisplayDocTitle true. | SAVE, SIGN | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects`, `Oracles_AcceptEveryClaimedFixture` |
| ISO 14289-1 7.1 | Suspects is false (the key is absent). | SAVE | compliant | `Iso14289_1_7_1_PdfUA1WritesDcTitleAndDisplayDocTitleAndNoSuspects` |
| ISO 14289-1 7.1 | An Info dictionary may be present and a reader ignores it. | SAVE | compliant | `Iso19005_2_6_6_3_InfoEntriesAndXmpPropertiesCarryTheSameText` |
| ISO 14289-1 7.2 | The catalog /Lang, when present, is a language identifier; natural language of the metadata is determined by it. | SAVE, STR | compliant | `Iso19005_2_6_7_4_And_Iso14289_1_7_2_CatalogLangIsALanguageTagMatchedByXmpAndByItsTrim`, `Iso19005_2_6_7_4_LanguageIsTrimmedWhereWrittenAndOmittedWhenBlank` |
| ISO 14289-1 7.16 | An encrypted PDF/UA-1 file keeps the permission to extract for accessibility. | SAVE | compliant | `Iso14289_1_7_16_PdfUA1MayBeEncryptedOnlyWithExtractAndThenTheTrailerHoldsAnId` |
| ISO 32000-1 7.7.2 | The catalog has /Type /Catalog, /Pages and /Metadata; the signing path writes the same entries as Save plus /AcroForm. | SIGN, SAVE (#635, #626) | compliant | `Iso32000_1_7_7_2_SaveAndSigningPathWriteTheSameCatalogEntriesBesidesAcroForm` |
| ISO 32000-1 14.3 | A pinned Timestamp is written in the XMP dates in the given offset, and the same pins give the same bytes. | ID | compliant | `Iso32000_1_14_3_PinnedTimestampAndDocumentIdGiveIdenticalBytesOnEverySavePath`, `Iso32000_1_14_3_TimestampSetsTheXmpDatesInThePinnedOffset` |
| ISO 32000-1 14.4 | A derived /ID follows the title and timestamp, 16 bytes, both strings equal. | ID | compliant | `Iso32000_1_14_4_DerivedIdIsSixteenBytesAndFollowsTheTimestampAndTitle` |
| ISO 19005-2 6.1.13 (signing path) | The placeholder's /ByteRange holds four integers above int32 until the signature patches them; the delivered file does not. | SIGN | compliant | `Iso19005_2_6_1_13_PrepareForSigningWritesTenNinesInByteRangeAndSigningReplacesThem` |
| ISO 19005-2 6.1.7.1 (empty page) | An empty page's content stream is the 8-byte empty zlib stream, /Length 8, accepted by qpdf and poppler for every claim, and by veraPDF for 2b and 2u. | EMPTY | compliant | `Iso19005_2_6_1_7_1_AnEmptyPageContentIsTheEmptyZlibStreamWithItsLengthAndPassesTheTools` |
| ISO 19005-2 6.1 (all paths) | veraPDF 2b, 2u, 2a and ua1 and qpdf accept the saved file of every path. | SAVE, OBJ, LIN, SIGN | compliant | `Oracles_AcceptEveryClaimedFixture` |
| Reader recovery bounds (#623) | Bounds what damaged-file recovery allocates; the Reader writes nothing in recovery. | RECOV | not applicable | none |

### Needs NEN

ISO 19005-2 clauses the maintainer's reading did not complete, and what each would decide:

None left in G5. The rows the first reading left open (6.1.1, 6.1.7.2, 6.6.3, 6.6.5, 6.6.6, 6.7.4) were read in full on 2026-11-27 and are rows of the table above.

## G6 Forms
