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

Tests: `ConformanceAuditG2Tests` in `tests/VellumPdf.Kernel.Tests`. The tests run at origin/main 33d423a, which
includes the merge of #735. The font measurements were first taken at 8c39d8d, and the only Kernel change between the
two is an internal bounding-box property. Fixtures are saved in the test with the fonts in the tree (DejaVu Sans,
Source Sans 3 Extra Light and the twelve bundled Liberation programs), under no claim and under PDF/A-2b, 2u, 2a and
PDF/UA-1, through the Kernel document and the Layout document. Each test reads the saved file back through the Reader,
decodes every shown string through its font's own ToUnicode map, and compares glyph ids, widths, the descriptor box
and the program tables with the source font. The tests share the library's sfnt table parser (`SfntFont.Parse`), its
CFF parser in one test and `TextStyle.MeasureString` for tab positions; the table contents are decoded in the test,
with a cmap reader of its own. veraPDF 1.30.4 and qpdf 12.4.2 run on the claimed fixtures, and poppler reads the text
back. veraPDF does not see glyph 0 in an OpenType CFF font (its 6.2.11.8-1 passed a Source Sans 3 file that shows
glyph 0 once), so the tests assert the codes in the content stream and use the oracle as a second reading.

56 rows: 36 compliant, 14 gap, 6 not applicable, 0 human-only. A row that is compliant for most input and has a gap
for one construct is counted as a gap. Three gaps were new (#752, #753 and #754), and #752 is closed; the others cite the issue that
already tracks them. Behaviour that no clause requires is in the second table and is not counted.

Feature key: EMB is an embedded TrueType or OpenType font written as a Type 0 font; LIB is the bundled Liberation
program (#708); G0 is the glyph 0 and unembedded-font reports (#657, #592); C12 is cmap format 12 (#629); BBOX is
FontBBox from head (#656); TOU is the ToUnicode CMap and its line ends (#647); TAB is tab stops and leaders (#653);
PRE is Preformatted (#662); JOIN is joined runs and the space at a style change (#650, #590); SOFT is the space at a
soft line end or an explicit line break; Standard 14 is a font written without a program.

| Standard and clause | Requirement | Feature | Verdict | Test |
|---|---|---|---|---|
| ISO 32000-1 9.6.4; ISO 32000-2 9.9.2 | A subset's BaseFont and FontName are six capital letters, a plus sign and the source's PostScript name | EMB, LIB | compliant | `Iso32000_1_9_6_4_ASubsetNameIsSixCapitalsAPlusAndThePostScriptNameOfTheSource` |
| ISO 32000-2 9.9.2 | Different subsets of the same font in one file have different tags | EMB, LIB | compliant: two registrations of one program share one handle, so the file holds one subset | `Iso32000_2_9_9_2_TwoSubsetsOfOneFontInOneFileShareAnEmbeddedFontAndSoOneTag` |
| ISO 32000-1 9.6.4 | "Different subsets in the same PDF file" have different tags, read without the 32000-2 narrowing to one font | EMB, LIB | gap #754: the tag hashes the glyph ids alone, so Liberation Sans, Sans Bold, Serif and Mono Bold drawing "Hello" in a PDF/A-2b file share one | `Iso32000_1_9_6_4_TwoSubsetsOfDifferentFontsHaveDifferentTags` (skipped), `Iso32000_1_9_6_4_FourFacesDrawingTheSameTextShareOneTagUnderAPdfAClaim` |
| ISO 32000-1 9.7.4.1, Table 117; 9.8.1; Table 121 | The CIDFont's BaseFont equals the descriptor's FontName (shall). The Type0 BaseFont matching a Type 2 descendant's is a should (Table 121), and for a CFF descendant Table 121 recommends another form that is not asserted | EMB, LIB, C12 | compliant | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 32000-1 9.7.4.1 | For a CFF CIDFont the BaseFont is the CIDFontName of the program | EMB (CFF) | compliant | `Iso32000_1_9_7_4_1_ACffCidFontIsNamedLikeItsProgram` |
| ISO 32000-1 9.7.6, 9.7.5.2 | A composite font is a Type0 dictionary with one descendant and an Encoding of Identity-H, which is not used with an unembedded font | EMB, LIB | compliant | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 32000-1 9.7.3, Table 116 | CIDSystemInfo holds Registry, Ordering and Supplement | EMB, LIB | compliant (Adobe, Identity, 0) | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 32000-1 9.7.4.2, Table 117 | An embedded Type 2 CIDFont has a CIDToGIDMap; a CFF CIDFont has none (a Type 2 key) | EMB, C12 | compliant (Identity for glyf, absent for CFF) | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 32000-1 9.7.4.2 | Every CIDFont has a glyph for CID 0 | EMB | compliant | `Iso32000_1_9_7_4_2_ASubsetDefinesGlyphZeroAndEveryGlyphItShows`, `Iso19005_2_6_2_11_5_WidthsInTheWArrayAgreeWithTheProgramWithinOneThousandth` |
| ISO 32000-1 9.7.4.3; 19005-2 6.2.11.5; 14289-1 7.21.5 | W groups are "c [w1 ... wn]"; widths agree with the program within 1/1000 | EMB, LIB | compliant (the written width is the rounded hmtx advance) | `Iso19005_2_6_2_11_5_WidthsInTheWArrayAgreeWithTheProgramWithinOneThousandth` |
| ISO 32000-1 9.8.1, Table 122 | A descriptor has Type, FontName, Flags, FontBBox, ItalicAngle, Ascent, Descent, CapHeight and StemV; Descent is negative; at most one FontFile key; no descriptor on a Type0 font | EMB, LIB | compliant | `Iso32000_1_9_8_TheDescriptorHoldsEveryRequiredKeyAndTheSignsTheTextGives`, `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 32000-1 9.8.1 | FontBBox is a rectangle enclosing the glyphs | BBOX | compliant: the head box scaled to 1000, lower edges down and upper edges up | `Iso32000_1_9_8_FontBBoxIsTheHeadBoxScaledToAThousandAndRoundedOutward`, `Iso32000_1_9_8_FontBBoxOfALiberationProgramIsItsHeadBoxRoundedOutward` |
| ISO 32000-1 9.8.2, Table 123 | Undefined flag bits are 0, and Symbolic and Nonsymbolic are never both set or both clear | EMB, LIB | compliant | `Iso32000_1_9_8_2_FlagsHoldNoUndefinedBitAndExactlyOneOfSymbolicAndNonsymbolic` |
| ISO 32000-1 9.8.2, Table 123 | A font with a glyph outside the Adobe standard Latin set is Symbolic and not Nonsymbolic | EMB, C12 | gap #753: every descriptor is Nonsymbolic | `Iso32000_1_9_8_2_ASubsetWithGlyphsOutsideTheLatinSetIsMarkedSymbolic` (skipped) |
| ISO 32000-1 9.8.2 | FixedPitch and Italic describe the font | EMB, LIB | compliant (from the post table) | `Iso32000_1_9_8_2_FixedPitchAndItalicFollowThePostTableOfTheProgram` |
| ISO 32000-1 9.8.3.1; 19005-2 6.2.11.4.2; 14289-1 7.21.4.2 | A CIDSet, when present, identifies every CID in the program; a Type 1 CharSet lists every glyph | EMB | not applicable: no CIDSet and no CharSet is written (`git grep -n CIDSet` over `src` on this branch finds only Conformance and Reader code) | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` checks the absence |
| ISO 32000-1 9.9, Tables 126 and 127; ISO 32000-2 9.9.1, Tables 124 and 125 | A glyf program has head, hhea, hmtx, loca, maxp, glyf and the cvt, fpgm and prep tables the source has, no cmap for a CIDFont, and Length1 | EMB, LIB | compliant; every table checksum and head.checkSumAdjustment also verified | `Iso32000_1_9_9_AGlyfProgramHasTheRequiredTablesAndNoCmapAndACffProgramKeepsIt` |
| ISO 32000-1 9.9, Tables 126 and 127; ISO 32000-2 9.9.1, Tables 124 and 125 | An OpenType CFF program has CFF and cmap tables, Subtype OpenType, one font, and no Length1, 2 or 3 | EMB (CFF) | compliant | `Iso32000_1_9_9_AGlyfProgramHasTheRequiredTablesAndNoCmapAndACffProgramKeepsIt` |
| ISO 32000-1 9.9 | A reader never uses vhea and vmtx | EMB | not applicable: a reader duty | none |
| ISO 32000-1 9.9; ISO 19005-2 6.2.11.4.1; ISO 14289-1 7.21.4.1 | An embedded program is used only to view and print; only fonts legally embeddable without restriction are embedded | EMB, LIB | gap #267: fsType is parsed and not enforced (the package READMEs say the library "currently parses and ignores" it); Matterhorn 31-010 is a human check | `Iso14289_1_7_21_4_1_AFontWhoseFsTypeRestrictsEmbeddingIsRefusedUnderAClaim` (skipped), `Iso14289_1_7_21_4_1_AFontWhoseFsTypeRestrictsEmbeddingIsEmbeddedAnywayToday`, `Iso14289_1_7_21_4_1_EveryFontProgramInTheTreeAllowsEmbeddingAndThePermissionIsReadFromOs2` |
| ISO 32000-1 9.7.4.2; ISO 32000-2 9.9.2 | .notdef is defined in a subset, and every glyph the content shows is in it, composite components included | EMB | compliant | `Iso32000_1_9_7_4_2_ASubsetDefinesGlyphZeroAndEveryGlyphItShows` |
| ISO 32000-1 9.7.5.3, 9.10.3 | An Encoding CMap has no bfchar; a ToUnicode CMap may | EMB | compliant (Encoding is the predefined name Identity-H) | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 32000-1 9.10.2, 9.10.3 | ToUnicode has a codespace consistent with the font's encoding and maps every shown code with bfchar or bfrange to UTF-16BE; TN5014 limits a block to 100 entries | TOU, C12, EMB | compliant | `Iso32000_1_9_10_3_AToUnicodeCMapCoversEveryShownCodeAndMapsItBackToTheText`, `Iso32000_1_9_10_3_AToUnicodeCMapOfMoreThanOneHundredGlyphsIsWrittenInBlocksOfAtMostOneHundred`, `Iso32000_1_9_10_2_TheTextShownIsTheTextDrawnForEveryKind` |
| ISO 32000-1 9.7.4.2, 9.10.3 | A character outside the BMP is shown by the glyph of the font's own format 12 cmap and mapped back as a surrogate pair | C12 (#629) | compliant, for DejaVu Sans (U+1F600, U+1D538, U+1D7D8) and Source Sans 3 (U+1F4A9) | `Iso32000_1_9_7_4_2_ACharacterOutsideTheBmpIsShownByItsGlyphAndMapsBackAsASurrogatePair`, `Iso32000_1_9_7_4_2_EveryShownCodeIsTheGlyphTheFontsCmapGivesItsCharacter` |
| ISO 32000-1 Annex D | Standard 14 text is encoded through WinAnsiEncoding, with the codes of Table D.2 | EMB (Standard 14) | compliant | `Iso32000_1_AnnexD_Standard14TextIsEncodedWithWinAnsi` |
| ISO 32000-1 14.8.2.5 (Tagged PDF) | The spaces that separate words in plain text are in the Unicode stream, and a word break at the end of a show string still has its space | TAB | compliant: the text before a tab ends in a space | `Iso32000_1_14_8_2_5_ATabWritesTheSpaceThatSeparatesTheWordsBeforeIt` |
| ISO 32000-1 14.8.2.5 | The same, for a run boundary inside a word and one at a word break | JOIN (#650, #590) | compliant: no space is added inside a word, the one in the text is written, and a doubled one collapses | `Iso32000_1_14_8_2_5_ARunBoundaryInsideAWordWritesNoSpaceAndOneAtAWordBreakWritesIt`, `Iso19005_2_6_7_3_2_AWordEndingAtAStyleChangeKeepsItsSpaceInTheShowString` |
| ISO 32000-1 14.8.2.5 | The same, at a soft line end | SOFT | gap #588: the last show string of a wrapped line has no space, so the stream reads "alphabeta" across the break. | `Iso19005_2_6_7_3_2_AWordEndingAtASoftLineEndHasASpaceInItsShowString` (skipped), `Iso19005_2_6_7_3_2_TheLastShowStringOfAWrappedLineHasNoSpaceAndTheLinesJoinWithoutOne` |
| ISO 32000-1 14.8.2.5 | The same, at a Preformatted line end | PRE | gap #588: a block is one marked-content sequence and each line is a show string with no space after it, so the stream reads "betagamma" across the break; poppler's layout reading does not count, because 14.8.2.5 note 2 says a reader does not need to guess from glyph positions | `Iso19005_2_6_7_3_2_APreformattedLineEndHasASpaceInItsShowString` (skipped), `Iso19005_2_6_7_3_2_APreformattedLineEndHasNoSpaceInItsShowStringAsIssue588Describes` |
| ISO 32000-1 14.8.2.5 | The same, at a break inside a URL or path | URL breaks (#594, #733) | not applicable: the cuts are not on main | none |
| ISO 32000-1 14.9.4 | Replacement text is written as ActualText where the glyphs do not stand for the text | none | not applicable except for the private use area: nothing in `src/VellumPdf.Kernel` or `src/VellumPdf.Layout` writes /ActualText (`git grep -n ActualText` finds only Conformance and Reader code); see 6.2.11.7.3 | none |
| ISO 19005-2 6.2.11.2 | Font dictionaries have Type Font, a defined Subtype and a BaseFont; a FontFile3 Subtype is Type1C, CIDFontType0C or OpenType | EMB, LIB | compliant (veraPDF 6.2.11.2-1 to 6.2.11.2-3 and 6.2.11.2-7) | `Iso19005_2_TheKernelFontFixturesAreAcceptedByVeraPdfAndQpdf`, `Iso19005_2_6_2_11_4_1_TheTwelveLiberationProgramsEmbedAndPassTheOracleForEachClaim` |
| ISO 19005-2 6.2.11.2 | A simple font has FirstChar, LastChar and Widths unless it is a standard 14 font | Standard 14 | compliant (exempt), and see 6.2.11.4.1 | `Iso19005_2_6_2_11_4_1_EveryStandard14FaceIsWrittenWithoutAFontProgramAndTheKernelDoesNotRefuseIt` |
| ISO 19005-2 6.2.11.3.1 to 6.2.11.3.3; 14289-1 7.21.3 | CIDSystemInfo with Identity-H is free, a Type 2 CIDFont has CIDToGIDMap, and only predefined CMaps are used unembedded | EMB, LIB | compliant | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 19005-2 6.2.11.4.1; 14289-1 7.21.4.1 | Every font used for rendering is embedded, with no exemption for the standard 14 | LIB, Standard 14 | compliant for embedded and Liberation text; gap #711 and #474 for a Standard 14 face under a claim: the Kernel saves it silently and Layout saves it with an Error report. | `Iso19005_2_6_2_11_4_1_TheTwelveLiberationProgramsEmbedAndPassTheOracleForEachClaim`, `Iso19005_2_6_2_11_4_1_EveryStandard14FaceIsWrittenWithoutAFontProgramAndTheKernelDoesNotRefuseIt`, `Iso19005_2_6_2_11_4_1_AStandard14FontIsEmbeddedOrRefusedUnderAClaim` (skipped) |
| ISO 19005-2 6.2.11.4.1 (new API) | A Preformatted block refuses a font that is not embedded under a claim | PRE | compliant | `Iso19005_2_6_2_11_4_1_APreformattedBlockInAnUnembeddedFontIsRefusedUnderAClaimAndSavesWithout` |
| ISO 19005-2 6.2.11.4.1 (new API) | The bundled Liberation program exists for the Helvetica, Times and Courier faces and refuses Symbol, ZapfDingbats and undefined values | LIB (#708) | compliant | `Iso19005_2_6_2_11_4_1_ASymbolOrZapfDingbatsSubstituteIsNotBundledAndTheCallThrows` |
| ISO 19005-2 6.2.11.4.1-2 | An embedded font defines every glyph referenced | EMB, C12 | compliant for characters the font has; glyph 0 otherwise, see 6.2.11.8 | `Iso19005_2_6_2_11_8_NoGlyphZeroIsShownForACharacterTheFontHas` |
| ISO 19005-2 6.2.11.6; 14289-1 7.21.6 | TrueType encoding rules for non-symbolic and symbolic simple fonts | EMB | not applicable: every embedded font is a Type0 font, and no simple /TrueType dictionary is written (`git grep -n '"TrueType"'` over the Kernel and Layout writers finds none) | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` checks there is no simple font |
| ISO 19005-2 6.2.11.7.2 (U, A); 14289-1 7.21.7 | Every font has a ToUnicode CMap that maps every used code | EMB, LIB | gap #711, #474: embedded and Liberation text is compliant; the twelve WinAnsi Standard 14 faces are exempt from 7.21.7 by its first condition, and Symbol is by the second, read literally (its glyph names are in the Symbol set of Annex D.5). veraPDF's rule tests only for a ToUnicode entry, so it fails Symbol and ZapfDingbats, which have none: 6.2.11.7.2-1 (2u, 2a) and 7.21.7-1 (UA-1), 8 failed checks in the fixture, in the class of the embedding failure | `Iso32000_1_9_10_3_AToUnicodeCMapCoversEveryShownCodeAndMapsItBackToTheText`, `Iso19005_2_6_2_11_4_1_EveryStandard14FaceIsWrittenWithoutAFontProgramAndTheKernelDoesNotRefuseIt` |
| ISO 19005-2 6.2.11.7.2; 14289-1 7.21.7 | Every ToUnicode value is above zero and is neither U+FEFF nor U+FFFE | TOU, JOIN | compliant: under 2u, 2a and UA-1 a glyph that only U+FEFF reaches is named U+2060 (#752), through the Kernel and every Layout element; 2b and no claim keep the font's own mapping. Matterhorn 31-028 and 31-029 | `Iso19005_2_6_2_11_7_2_ToUnicodeValuesAreAboveZeroAndNeitherFeffNorFffe`, `Iso19005_2_6_2_11_7_2_ATextWithAZeroWidthNoBreakSpaceKeepsTheFontsOwnMappingWhereNoLevelNeedsToUnicode`, `Iso19005_2_6_2_11_7_2_ACharacterThatToUnicodeCannotNameIsNotWrittenUnderALevelThatNeedsToUnicode`, `Issue752_AZeroWidthNoBreakSpaceIsNamedAsAWordJoinerInToUnicodeAndStillDrawnAsItsOwnGlyph` |
| ISO 19005-2 6.2.11.7.2 (new API) | A Preformatted block refuses U+FEFF and U+FFFE under 2u, 2a and UA-1 | PRE | compliant | `Iso19005_2_6_2_11_8_APreformattedBlockRefusesACharacterToUnicodeCannotNameOrTheFontLacks` |
| ISO 19005-2 6.2.11.7.3 (A) | A character in the private use area has ActualText | EMB | gap #602. Under 2u the character maps to itself, which 14289-1 7.2 allows. veraPDF fails 6.2.11.7.3-1 under 2a only | `Iso19005_2_6_2_11_7_3_APrivateUseCharacterHasActualTextUnderLevelA` (skipped), `Iso19005_2_6_2_11_7_3_VeraPdfFailsAPrivateUseCharacterWithoutActualTextUnderLevelAOnly`, `Iso14289_1_7_2_APrivateUseCharacterMapsToItsOwnCodePoint` |
| ISO 19005-2 6.2.11.8; 14289-1 7.21.8 | No text operator references .notdef | EMB, G0 (#657, #592) | gap #474: a character the font lacks is shown as glyph 0 under every claim, listed by the Kernel and as an Error by Layout, and ToUnicode names it U+FFFD; veraPDF fails 6.2.11.8-1 on a TrueType font and does not see it in an OpenType CFF font | `Iso19005_2_6_2_11_8_ACharacterTheFontLacksIsWrittenAsGlyphZeroAndToUnicodeNamesItReplacement`, `Iso19005_2_6_2_11_8_VeraPdfDoesNotSeeGlyphZeroInAnOpenTypeCffFont`, `Iso19005_2_6_2_11_8_LayoutReportsEachCharacterWrittenAsGlyphZeroWithTheClauseAndStrictModeRefusesIt`, `Iso19005_2_6_2_11_8_WithoutAClaimLayoutStillReportsGlyphZeroAndSavesTheFile`, `Iso19005_2_6_2_11_8_ACharacterTheFontLacksIsRefusedUnderAClaim` (skipped) |
| ISO 19005-2 6.7.3.1 (A, should) | Leaders and the hook of a wrapped line are artifacts | TAB, PRE | gap #303 (a should): the leaders and the hook are path operators inside /Artifact, and the sequence is a bare /Artifact with no Type | `Issue653_ATabIsNeverShownAndALeaderIsAnArtifactPathNotText`, `Issue662_AWrappedPreformattedLineKeepsEveryCharacterAndDrawsItsHookAsAnArtifact` |
| ISO 19005-2 6.7.3.2 (A) | A word that ends a show string has its space in that string | TAB, JOIN, SOFT, PRE | gap #588: compliant for tabs, run boundaries and style changes; the show string has no trailing space at a soft line end, at a Preformatted line end, at a paragraph's explicit line break, or in a justified line, where each word and each space is its own show string (#588 and #613 item 39) | see the 14.8.2.5 rows |
| ISO 14289-1 7.21.2 | Fonts conform to 9.6 and 9.7 and the font specifications they cite | EMB, LIB | compliant | `Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont` |
| ISO 14289-1 7.21.4.1 (note 5) | The standard 14 are not exempt from embedding | Standard 14 | gap #711, #474; veraPDF 7.21.4.1-1 and 7.21.7-1 | `Iso19005_2_6_2_11_4_1_EveryStandard14FaceIsWrittenWithoutAFontProgramAndTheKernelDoesNotRefuseIt` |
| ISO 14289-1 7.2; ISO 32000-1 14.8.2.4.2 | Characters map to Unicode, with enough information to map every code; characters outside Unicode may use the private use area | TOU | compliant | `Iso14289_1_7_2_APrivateUseCharacterMapsToItsOwnCodePoint`, `Iso32000_1_9_10_2_PdftotextReadsBackTheCharactersThroughToUnicode` |
| ISO 14289-1 7.2 | Stretchable characters are tagged with ActualText | none | not applicable: no stretchable character is built from several glyphs | none |
| ISO 32000-2 9.7 to 9.10 | The same keys and rules when the file is written as PDF 2.0 | EMB, LIB | compliant: every font and text test also runs with no claim, which writes %PDF-2.0 | `Iso32000_2_9_TheFontObjectsOfAFileWithoutAClaimAreThoseOfAPdf20File`, `Iso32000_2_9_TheLayoutTextFixtureOfAFileWithoutAClaimPassesQpdf` |
| ISO 19005-2, ISO 14289-1 | The font and text fixtures pass veraPDF 1.30.4 under the claim they make, and qpdf --check | EMB, LIB, TAB, PRE, JOIN | compliant (the Layout text fixture fails 6.2.11.7.3-1 under 2a, from its private use character) | `Iso19005_2_TheKernelFontFixturesAreAcceptedByVeraPdfAndQpdf`, `Iso19005_2_TheLayoutTextFixtureIsAcceptedByVeraPdfAndQpdfUnderEachClaim` |
| ISO 32000-1 9.8.1, Table 122; ISO 32000-2 Table 120 | ItalicAngle is negative for a font that slopes to the right | EMB, LIB | compliant: the written angle is the post table's, negative for the six Liberation italic and oblique faces | `Iso32000_1_9_8_ItalicAngleIsNegativeForAFaceThatSlopesToTheRight` |
| ISO 32000-1 9.6.2, Table 111 | For a standard 14 font FirstChar, LastChar, Widths and FontDescriptor are all present or all absent | Standard 14 | compliant: none of the four is written | `Iso19005_2_6_2_11_4_1_EveryStandard14FaceIsWrittenWithoutAFontProgramAndTheKernelDoesNotRefuseIt` |
| ISO 32000-1 14.8.2.5 | A paragraph's explicit line break has the separator that plain text would have | SOFT | gap #588: "alpha beta" and "gamma delta" are two show strings, so the stream reads "betagamma"; #588's Scope sentence about explicit line breaks conflicts with its comment on Preformatted | `Iso19005_2_6_7_3_2_AParagraphLineBreakHasASpaceInItsShowString` (skipped), `Iso19005_2_6_7_3_2_AParagraphLineBreakHasNoSpaceInItsShowStringAsIssue588Describes` |
| ISO 14289-1 6.2; ISO 19005-2 5 | The file meets ISO 32000-1 as the part modifies it, including 9.8.2 | EMB | gap #753: the Nonsymbolic flag (see the 9.8.2 row) | `Iso32000_1_9_8_2_ASubsetWithGlyphsOutsideTheLatinSetIsMarkedSymbolic` (skipped) |

Product rules pinned without a clause (not counted above). The tests keep them from drifting, and each says what was
measured:

| Rule | Feature | Pinned behaviour | Test |
|---|---|---|---|
| #647's choice | TOU | Every line of a ToUnicode CMap ends with one LF and the stream holds no CR. The embedder's own documentation does not claim that CR LF is invalid; the ASCII-only check rests on TN5014 | `Issue647_EveryLineOfAToUnicodeCMapEndsWithOneLineFeed` |
| TN5014 code mappings may overlap, a later one superseding an earlier | TOU | Two characters that share a glyph (U+0020 and U+00A0 in Source Sans 3) give its code two entries in the order of first use; poppler 26.08 returned U+0020 for both orders, so readers disagree and no clause forbids it | `Iso32000_1_9_10_3_TwoCharactersThatShareAGlyphGiveItsCodeTwoEntriesInTheOrderOfFirstUse` |
| #653 | TAB | A tab is never shown, a right stop places the text so its right edge is at the stop, and a leader is a stroked path (inside /Artifact when the document is tagged) | `Issue653_ATabIsNeverShownAndALeaderIsAnArtifactPathNotText` |
| #662 | PRE | A Preformatted block expands a tab to the next multiple of eight columns and keeps every other space | `Issue662_APreformattedBlockExpandsTabsToSpacesAndKeepsEveryOtherSpace` |
| #662 | PRE | A wrapped Preformatted line loses no character and its hook is a path in an artifact | `Issue662_AWrappedPreformattedLineKeepsEveryCharacterAndDrawsItsHookAsAnArtifact` |
| #657 | G0 | A Standard 14 font under a claim is reported as an Error that strict mode refuses; the save still writes the file | `Iso19005_2_6_2_11_4_1_LayoutReportsAStandard14FontAsAnErrorAndSavesItUnlessStrict`, `Iso19005_2_6_2_11_4_1_WithoutAClaimAStandard14FontIsNotReported` |

Notes on the rows above:

- The glyph 0 entry of ToUnicode is U+FFFD, which meets the value rule of 6.2.11.7.2 and 7.21.7 while naming no
  character; the maintainer ruled it for #592.
- The measured subset-tag clash (#754) spans Liberation Sans, Sans Bold, Serif and Mono Bold drawing the same text in
  a file written under PDF/A-2b, which is the PDF 1.7 edition 32000-1 governs.

Not done in this audit:

- URL breaks (#594, PR #733) are not on main and are being reworked as an opt-in setting.
- The band text of #735 (band zones, logos and rules in Layout, in 33d423a) was not run through any fixture.
- No CID-keyed CFF font is in the tree, so the whole-font fallback of the CFF embedder is not exercised.
- A restricted fsType is tested on DejaVu Sans with the OS/2 field rewritten, not on a font that ships with one.
- Ascent excluding the height of accented glyphs (Table 122 of ISO 32000-1; the writer uses OS/2 sTypoAscender) was
  not measured, so it has no row.
- A variable font's box and widths (Table 122): no variable font is in the tree and none was downloaded, so the clause
  has no row. The synthetic variable fonts of `EmbeddedFontTableSelectionTests` cover the table selection only.
- The Barcodes human-readable text is written with a caller-supplied Standard 14 resource, which is G4's.
- VellumPdf.Conformance's own rule for 6.2.11.8 was not run on the Source Sans 3 file, so whether the in-process
  validator sees glyph 0 in a CFF font is not measured here.
- The Layout elements other than paragraph, heading, list item and table cell were not run through the U+FEFF fixture. Every one that draws embedded-font text reaches the same Kernel writer, but that is read from the source, not measured.

ISO 19005-2 rows that need the maintainer's NEN check before the test is acceptance (the notes record these as
paraphrased or read in part): the exact exemption in 6.7.3.2 for a word followed by punctuation, the wording of
6.2.11.4.1 on legally embeddable fonts, and the wording of 6.2.11.7.3 for a sequence that contains a private use
character. The rows for 6.7.3.2 and 6.2.11.7.3 rest on the reading recorded in issues #588 and #602.

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
