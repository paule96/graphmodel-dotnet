// Copyright 2025 Savas Parastatidis

namespace Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core;

using System.Text;
using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Visitors.Core;

/// <summary>
/// Renders a Cypher query string from the fragment sequence produced by the fragment pipeline.
/// </summary>
internal static class AgeFragmentRenderer
{
    /// <summary>
    /// Groups all fragment types extracted from the fragment sequence into a single bundle,
    /// reducing the number of repetitive OfType/ToList calls in the Render method.
    /// </summary>
    private sealed record FragmentBundle(
        IReadOnlyList<ProjectionFragment> ProjectionFragments,
        IReadOnlyList<WhereFragment> WhereFragments,
        IReadOnlyList<OrderFragment> OrderFragments,
        IReadOnlyList<AggregationFragment> AggregationFragments,
        IReadOnlyList<GroupByFragment> GroupByFragments,
        IReadOnlyList<OptionalMatchFragment> OptionalMatchFragments,
        IReadOnlyList<ComplexPropertyLoadingFragment> ComplexPropertyToggles,
        SkipFragment? SkipFragment,
        LimitFragment? LimitFragment,
        bool HasDistinct,
        bool HasReverseOrder)
    {
        public static FragmentBundle Extract(IReadOnlyList<QueryFragment> fragments)
        {
            return new FragmentBundle(
                ProjectionFragments: fragments.OfType<ProjectionFragment>().ToList(),
                WhereFragments: fragments.OfType<WhereFragment>().ToList(),
                OrderFragments: fragments.OfType<OrderFragment>().ToList(),
                AggregationFragments: fragments.OfType<AggregationFragment>().ToList(),
                GroupByFragments: fragments.OfType<GroupByFragment>().ToList(),
                OptionalMatchFragments: fragments.OfType<OptionalMatchFragment>().ToList(),
                ComplexPropertyToggles: fragments.OfType<ComplexPropertyLoadingFragment>().ToList(),
                SkipFragment: fragments.OfType<SkipFragment>().LastOrDefault(),
                LimitFragment: fragments.OfType<LimitFragment>().LastOrDefault(),
                HasDistinct: fragments.OfType<DistinctFragment>().Any(),
                HasReverseOrder: fragments.OfType<ReverseOrderFragment>().Any()
            );
        }
    }

    public static string Render(IEnumerable<QueryFragment> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);

        var fragmentList = fragments.ToList();
        if (fragmentList.Count == 0) return string.Empty;

        var bundle = FragmentBundle.Extract(fragmentList);
        var matchClauses = BuildMatchClauses(fragmentList);
        var hasScalarAggregation = bundle.AggregationFragments.Count > 0 && bundle.GroupByFragments.Count == 0;
        var complexPropertyToggle = bundle.ComplexPropertyToggles.LastOrDefault();
        var isComplexPropertyLoadingEnabled = complexPropertyToggle?.IsEnabled ?? false;
        var returnClause = isComplexPropertyLoadingEnabled
            ? null
            : DetermineReturnClause(fragmentList, bundle.ProjectionFragments);
        var shouldReverseOrder = !hasScalarAggregation && bundle.HasReverseOrder;

        var builder = new StringBuilder();

        if (matchClauses.Count > 0)
            builder.AppendLine($"MATCH {string.Join(", ", matchClauses)}");

        foreach (var opt in bundle.OptionalMatchFragments)
        {
            if (!string.IsNullOrWhiteSpace(opt.Pattern))
                builder.AppendLine($"OPTIONAL MATCH {opt.Pattern}");
        }

        if (bundle.WhereFragments.Count > 0)
        {
            var whereClauses = bundle.WhereFragments.Select(f => f.PredicateText).ToList();
            builder.AppendLine($"WHERE {string.Join(" AND ", whereClauses)}");
        }

        if (isComplexPropertyLoadingEnabled)
        {
            var alias = DetermineComplexPropertyAlias(bundle.ComplexPropertyToggles, fragmentList);
            AppendComplexPropertyLoadingBlock(builder, alias);
        }
        else if (!string.IsNullOrWhiteSpace(returnClause))
        {
            var distinctPrefix = bundle.HasDistinct ? "DISTINCT " : string.Empty;
            builder.AppendLine($"RETURN {distinctPrefix}{returnClause}");
        }

        if (bundle.OrderFragments.Count > 0 && !hasScalarAggregation)
        {
            var orderByClauses = bundle.OrderFragments.Select(f =>
            {
                var desc = f.Descending;
                if (shouldReverseOrder) desc = !desc;
                return desc ? $"{f.Expression} DESC" : $"{f.Expression} ASC";
            }).ToList();
            builder.AppendLine($"ORDER BY {string.Join(", ", orderByClauses)}");
        }
        else if (!hasScalarAggregation && bundle.SkipFragment is not null && bundle.HasDistinct)
        {
            var primaryReturn = bundle.ProjectionFragments.SelectMany(f => f.Returns).FirstOrDefault()
                ?? returnClause?.Split(',', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(primaryReturn))
                builder.AppendLine($"ORDER BY {primaryReturn}{(shouldReverseOrder ? " DESC" : string.Empty)}");
        }

        if (bundle.SkipFragment is not null)
            builder.AppendLine($"SKIP {bundle.SkipFragment.Count}");
        if (bundle.LimitFragment is not null)
            builder.AppendLine($"LIMIT {bundle.LimitFragment.Count}");

        return builder.ToString().Trim();
    }

    private static List<string> BuildMatchClauses(IReadOnlyList<QueryFragment> fragments)
    {
        var matchClauses = new List<string>();
        foreach (var fragment in fragments)
        {
            if (fragment is MatchRootFragment root)
            {
                var pattern = root.Pattern;
                // If adding a standalone node pattern that's already covered by a preceding segment, skip it
                if (matchClauses.Count > 0 && IsRedundantStandaloneNode(pattern, matchClauses[^1]))
                    continue;
                // If adding a full segment pattern and the previous clause is just its starting node, replace it
                if (matchClauses.Count > 0 && IsCoveredByFullSegment(pattern, matchClauses[^1]))
                    matchClauses[^1] = pattern;
                else
                    matchClauses.Add(pattern);
            }
            else if (fragment is MatchSegmentFragment segment)
            {
                var pattern = segment.Pattern;
                if (matchClauses.Count > 0 && pattern.Length > 0 && (pattern[0] is '-' or '<'))
                    matchClauses[^1] = matchClauses[^1] + pattern;
                else if (matchClauses.Count > 0 && TryGetLeadingNodeAlias(pattern, out var leadingAlias, out var remainder) &&
                         TryGetTerminalNodeAlias(matchClauses[^1], out var terminalAlias) &&
                         leadingAlias == terminalAlias)
                    matchClauses[^1] += remainder;
                else
                    matchClauses.Add(pattern);
            }
        }
        return matchClauses;
    }

    private static bool IsRedundantStandaloneNode(string pattern, string lastClause)
    {
        // A standalone node pattern like "(src0:PersonNode)" is redundant if the last clause
        // is already a segment pattern starting with the same node
        var nodeMatch = System.Text.RegularExpressions.Regex.Match(pattern, @"^\((\w+):[^)]+\)$");
        if (!nodeMatch.Success)
            return false;

        var alias = nodeMatch.Groups[1].Value;
        return lastClause.StartsWith($"({alias}:") && lastClause.Contains("-[");
    }

    private static bool IsCoveredByFullSegment(string newPattern, string lastClause)
    {
        // If the new pattern is a full segment "(A:Label)-[r:Rel]->(B:Label)" and the last clause
        // is just the starting node "(A:Label)", replace the last clause with the full pattern
        var segmentMatch = System.Text.RegularExpressions.Regex.Match(newPattern, @"^\((\w+):[^)]+\)-");
        if (!segmentMatch.Success)
            return false;

        var alias = segmentMatch.Groups[1].Value;
        var standaloneNodeRegex = System.Text.RegularExpressions.Regex.Match(lastClause, @"^\((\w+):[^)]+\)$");
        return standaloneNodeRegex.Success && standaloneNodeRegex.Groups[1].Value == alias;
    }

    private static bool TryGetLeadingNodeAlias(string pattern, out string alias, out string remainder)
    {
        var match = System.Text.RegularExpressions.Regex.Match(pattern, @"^\((\w+):[^)]+\)([-<].*)$");
        if (match.Success)
        {
            alias = match.Groups[1].Value;
            remainder = match.Groups[2].Value;
            return true;
        }
        alias = string.Empty;
        remainder = string.Empty;
        return false;
    }

    private static bool TryGetTerminalNodeAlias(string pattern, out string alias)
    {
        var match = System.Text.RegularExpressions.Regex.Match(pattern, @"\((\w+):[^)]+\)\s*$");
        if (match.Success) { alias = match.Groups[1].Value; return true; }
        alias = string.Empty;
        return false;
    }

    private static string? DetermineReturnClause(IReadOnlyList<QueryFragment> fragments, IReadOnlyList<ProjectionFragment> projectionFragments)
    {
        var aggregationFragments = fragments.OfType<AggregationFragment>().ToList();
        var groupByFragments = fragments.OfType<GroupByFragment>().ToList();

        if (aggregationFragments.Count > 0)
        {
            var aggregationExprs = aggregationFragments.Select(FragmentFormatting.BuildAggregationExpression).ToList();

            // If there are group-by fragments, include projection (group key) expressions too
            if (groupByFragments.Count > 0 && projectionFragments.Count > 0)
            {
                var projectionExprs = projectionFragments
                    .SelectMany(f => f.Returns.Where(v => !string.IsNullOrWhiteSpace(v)))
                    .ToList();
                if (projectionExprs.Count > 0)
                    return string.Join(", ", projectionExprs.Concat(aggregationExprs));
            }

            return string.Join(", ", aggregationExprs);
        }

        if (projectionFragments.Count > 0)
        {
            // Use the LAST projection fragment (most recent Select in the chain)
            for (var i = projectionFragments.Count - 1; i >= 0; i--)
            {
                var values = projectionFragments[i].Returns.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (values.Count > 0) return string.Join(", ", values);
            }
        }

        // For path segment queries (MatchSegmentFragment), return ALL created aliases
        // so the SQL column definitions and Cypher RETURN columns match, and the
        // ResultMaterializer can reconstruct the full IGraphPathSegment.
        var segmentFragment = fragments.LastOrDefault(f => f is MatchSegmentFragment) as MatchSegmentFragment;
        if (segmentFragment != null && segmentFragment.CreatedAliases.Length >= 3)
        {
            return string.Join(", ", segmentFragment.CreatedAliases);
        }

        for (var i = fragments.Count - 1; i >= 0; i--)
        {
            var alias = fragments[i].CurrentAlias;
            if (!string.IsNullOrWhiteSpace(alias) && alias != "ps") return alias;
        }
        return null;
    }

    private static string DetermineComplexPropertyAlias(IReadOnlyList<ComplexPropertyLoadingFragment> toggles, IReadOnlyList<QueryFragment> fragments)
    {
        var toggleAlias = toggles.Where(t => t.IsEnabled && !string.IsNullOrWhiteSpace(t.TargetAlias))
            .Select(t => t.TargetAlias!.Trim()).LastOrDefault();
        if (!string.IsNullOrWhiteSpace(toggleAlias)) return toggleAlias;
        var fallback = fragments.LastOrDefault(f => !string.IsNullOrWhiteSpace(f.CurrentAlias))?.CurrentAlias;
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback!;
        return "src0";
    }

    private static void AppendComplexPropertyLoadingBlock(StringBuilder builder, string alias)
    {
        builder.AppendLine($"OPTIONAL MATCH ({alias})-[prop_rel]->(prop_node)");
        builder.AppendLine($"WHERE type(prop_rel) STARTS WITH '{GraphDataModel.PropertyRelationshipTypeNamePrefix}'");
        builder.AppendLine($"WITH {alias}, ");
        builder.AppendLine("     collect({");
        builder.AppendLine($"         ParentNode: {alias},");
        builder.AppendLine("         Relationship: prop_rel,");
        builder.AppendLine("         SequenceNumber: coalesce(prop_rel.SequenceNumber, 0),");
        builder.AppendLine("         Property: prop_node");
        builder.AppendLine("     }) AS complex_properties");
        builder.AppendLine("RETURN {");
        builder.AppendLine($"    Node: {alias},");
        builder.AppendLine("    ComplexProperties: complex_properties");
        builder.AppendLine("}");
    }
}
