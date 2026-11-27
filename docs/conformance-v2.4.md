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

## G6 Forms
