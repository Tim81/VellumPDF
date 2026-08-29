// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

// Neither assembly is strong-named (both live under tests/, and only the src/-side
// InternalsVisibleTo friends import StrongNameTestFriend.props), so no PublicKey= is needed here.
// Grants VellumPdf.TestSupport.Tests access to ExternalTool.ResetIdentityCacheForTests — a
// regression test for the identity-probe cache needs to clear one tool's cached verdict between
// runs, and nothing else has a reason to (#198 review, round 4).
[assembly: InternalsVisibleTo("VellumPdf.TestSupport.Tests")]
