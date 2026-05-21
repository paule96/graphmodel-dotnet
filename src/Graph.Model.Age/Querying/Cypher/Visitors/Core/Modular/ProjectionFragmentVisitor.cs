// Copyright 2025 Savas Parastatidis

namespace Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core.Modular;

using System.Collections.Immutable;
using System.Linq.Expressions;
using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Visitors.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// Specialized visitor for handling projection operations (Select).
/// </summary>
internal sealed class ProjectionFragmentVisitor : FragmentEmittingVisitorBase
{
    public ProjectionFragmentVisitor(CypherQueryContext context, ILogger logger)
        : base(context, logger)
    {
    }

    public Expression HandleSelect(MethodCallExpression node)
    {
        Logger.LogDebug("Processing SELECT clause");
        var sourceExpression = node.Arguments[0];
        var lambda = ExtractLambda(node.Arguments[1]);
        if (lambda == null) return sourceExpression;

        // Simple parameter projection (x => x) — return entire entity
        if (lambda.Body is ParameterExpression)
        {
            var alias = Context.Scope.CurrentAlias ?? "src0";
            Context.AddFragment(new ProjectionFragment(ImmutableArray.Create(alias), alias));
            Logger.LogDebug("Simple parameter projection: {Alias}", alias);
            return sourceExpression;
        }

        // Anonymous type or member projection
        if (lambda.Body is NewExpression newExpr)
        {
            var returns = new List<string>();
            for (int i = 0; i < newExpr.Arguments.Count; i++)
            {
                var propertyExpr = newExpr.Arguments[i];
                var propertyName = newExpr.Members?[i].Name ?? $"Prop{i}";
                var cypherAlias = $"c_{propertyName}";

                if (propertyExpr is MemberExpression member && member.Expression is ParameterExpression)
                {
                    // Simple parameter member access (e.g., p.FirstName) — use ResolveMemberExpression
                    returns.Add($"{ResolveMemberExpression(member)} AS {cypherAlias}");
                }
                else if (propertyExpr is ParameterExpression pathParam &&
                         typeof(IGraphPathSegment).IsAssignableFrom(pathParam.Type))
                {
                    // PathSegment parameter projection (e.g., ps => new { PathSegment = ps })
                    // Need to expand to all 3 components: source, relationship, target
                    var hop = Context.Scope.LastPathSegmentHop >= 0
                        ? Context.Scope.LastPathSegmentHop
                        : 0;
                    var aliases = Context.Scope.GetHopAliases(hop);
                    if (aliases.HasValue)
                    {
                        var (src, rel, tgt) = aliases.Value;
                        returns.Add($"{src} AS {cypherAlias}_{src}");
                        returns.Add($"{rel} AS {cypherAlias}_{rel}");
                        returns.Add($"{tgt} AS {cypherAlias}_{tgt}");
                    }
                    else
                    {
                        // Fallback if no hop aliases available
                        returns.Add($"{Context.Scope.CurrentAlias ?? "src0"} AS {cypherAlias}");
                    }
                }
                else
                {
                    // Complex expression (e.g., p.FirstName.Length, p.LastName.Contains("mi"),
                    // DateTime.Now, Math.Abs(...)) — use the expression visitor which handles
                    // all the special translations
                    var visitor = CreateExpressionVisitor();
                    var expr = TryResolveExpression(propertyExpr, visitor);
                    returns.Add($"{expr} AS {cypherAlias}");
                }
            }

            var currentAlias = Context.Scope.CurrentAlias ?? "src0";
            Context.AddFragment(new ProjectionFragment(ImmutableArray.CreateRange(returns), currentAlias));
            Logger.LogDebug("Anonymous type projection: {Returns}", string.Join(", ", returns));
            return sourceExpression;
        }

        // Member expression (e.g., Select(p => p.Name))
        if (lambda.Body is MemberExpression memberExpr)
        {
            var visitor = CreateExpressionVisitor();
            var cypherExpr = visitor.VisitAndReturnCypher(lambda.Body);
            Context.Scope.LastProjectedExpression = cypherExpr;

            // Update the scope alias to match the projected member's alias.
            // For example, after .Select(s => s.StartNode) on a path segment where
            // s.StartNode resolves to "src0", set CurrentAlias to "src0".
            var alias = Context.Scope.CurrentAlias ?? "src0";
            var cypherAlias = cypherExpr.Split('.')[0].Trim();
            if (cypherAlias != alias)
            {
                Context.Scope.CurrentAlias = cypherAlias;
                Logger.LogDebug("Updated CurrentAlias from '{OldAlias}' to '{NewAlias}' after member projection",
                    alias, cypherAlias);
            }

            Context.AddFragment(new ProjectionFragment(ImmutableArray.Create(cypherExpr), cypherAlias));
            Logger.LogDebug("Member projection: {Expression} -> alias {Alias}", cypherExpr, cypherAlias);
            return sourceExpression;
        }

        // Fallback: use expression visitor
        var fallbackVisitor = CreateExpressionVisitor();
        var cypherResult = fallbackVisitor.VisitAndReturnCypher(lambda.Body);
        Context.Scope.LastProjectedExpression = cypherResult;
        var currentAlias2 = Context.Scope.CurrentAlias ?? "src0";
        Context.AddFragment(new ProjectionFragment(ImmutableArray.Create(cypherResult), currentAlias2));
        return sourceExpression;
    }

    public Expression HandleGroupBy(MethodCallExpression node)
    {
        Logger.LogDebug("Processing GROUP BY clause");
        var sourceExpression = node.Arguments[0];

        if (node.Arguments.Count >= 2)
        {
            var lambda = ExtractLambda(node.Arguments[1]);
            if (lambda != null)
            {
                var visitor = CreateExpressionVisitor();
                var groupExpr = visitor.VisitAndReturnCypher(lambda.Body);
                Context.AddFragment(new GroupByFragment(groupExpr, Context.Scope.CurrentAlias ?? "src0"));
                Logger.LogDebug("GROUP BY expression: {Expression}", groupExpr);
            }
        }

        return sourceExpression;
    }

    private string ResolveMemberExpression(MemberExpression member)
    {
        // Handle IGrouping.Key — map to the GROUP BY expression from the last GroupByFragment
        if (member.Member.Name == "Key" && member.Expression is ParameterExpression groupParam
            && groupParam.Type.IsGenericType
            && groupParam.Type.GetGenericTypeDefinition().Name.Contains("IGrouping"))
        {
            var groupByFragment = Context.FragmentSequence.OfType<GroupByFragment>().LastOrDefault();
            if (groupByFragment != null)
                return groupByFragment.Expression;
        }

        if (member.Expression is ParameterExpression && typeof(IGraphPathSegment).IsAssignableFrom(member.Expression.Type))
        {
            // Use the stored hop aliases from scope to resolve path segment members
            // For chained patterns: hop 0 = (src0, r0, tgt0), hop 1 = (tgt0, r1, tgt1), etc.
            var lastPathSegmentHop = Context.Scope.LastPathSegmentHop;
            var hopAliases = lastPathSegmentHop >= 0
                ? Context.Scope.GetHopAliases(lastPathSegmentHop)
                : null;

            if (hopAliases.HasValue)
            {
                var (srcAlias, relAlias, tgtAlias) = hopAliases.Value;
                return member.Member.Name switch
                {
                    nameof(IGraphPathSegment.StartNode) => srcAlias,
                    nameof(IGraphPathSegment.EndNode) => tgtAlias,
                    nameof(IGraphPathSegment.Relationship) => relAlias,
                    _ => $"{tgtAlias}.{member.Member.Name}"
                };
            }

            // Fallback: use CurrentHop-1 as the path segment hop if LastPathSegmentHop not set
            var hop = Context.Scope.CurrentHop > 0 ? Context.Scope.CurrentHop - 1 : 0;
            var fallbackAliases = Context.Scope.GetHopAliases(hop);
            if (fallbackAliases.HasValue)
            {
                var (fsrc, frel, ftgt) = fallbackAliases.Value;
                return member.Member.Name switch
                {
                    nameof(IGraphPathSegment.StartNode) => fsrc,
                    nameof(IGraphPathSegment.EndNode) => ftgt,
                    nameof(IGraphPathSegment.Relationship) => frel,
                    _ => $"{ftgt}.{member.Member.Name}"
                };
            }

            return member.Member.Name;
        }
        return $"{Context.Scope.CurrentAlias ?? "src0"}.{member.Member.Name}";
    }

    private static string TryResolveExpression(Expression expr, AgeExpressionToCypherVisitor visitor)
    {
        try { return visitor.VisitAndReturnCypher(expr); }
        catch { return expr.ToString() ?? "unknown"; }
    }
}
