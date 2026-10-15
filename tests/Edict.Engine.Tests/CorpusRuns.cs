// Copyright (C) 2026 Andre Kaufmann
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Edict.Engine.Tests;

/// <summary>
/// The runs of a corpus for a theory, or, when the corpus is not there (the CI generates only the fast corpora:
/// traces, captures, multi, listen), one skipped row that names it: an absent corpus is reported, not a failure.
/// </summary>
internal static class CorpusRuns
{
    public static IEnumerable<TheoryDataRow<string>> OrSkip(IEnumerable<TheoryDataRow<string>> runs, string dir, string generator)
    {
        List<TheoryDataRow<string>> list = [.. runs];
        if (list.Count > 0)
            return list;
        return [new TheoryDataRow<string>("(none)") { Skip = $"no corpus in {dir} ({generator}, docs/TESTING.md)" }];
    }
}
