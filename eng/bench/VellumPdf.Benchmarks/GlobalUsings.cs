// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

// VellumPdf.Layout's global-using propagates the VellumPdf.Document namespace,
// which would shadow VellumPdf.Layout.Document. Pin the type explicitly.
global using LayoutDocument = VellumPdf.Layout.Document;
