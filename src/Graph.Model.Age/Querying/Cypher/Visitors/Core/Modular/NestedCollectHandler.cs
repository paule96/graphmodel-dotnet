// Copyright 2025 Savas Parastatidis
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core.Modular;

using System.Collections.Immutable;
using System.Linq.Expressions;
using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Visitors.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles nested collect() detection and translation for group projections.
/// Detects patterns like group.Select(p => new { ... }).ToList() inside projection expressions.
/// </summary>
internal sealed class NestedCollectHandler
{
    private readonly CypherQueryContext _context;
    private readonly ILogger _logger;

    public NestedCollectHandler(CypherQueryContext context, ILogger logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Detects nested .Select().ToList() chains on IGrouping parameters inside projection expressions.
    /// Pattern: group.Select(p => new { ... }).ToList()
    /// Emits a CollectFragment that generates collect({...}) in Cypher.
    /// </summary>
    public bool TryHandleNestedCollect(Expression propertyExpr, string propertyName, string cypherAlias, List<string> returns)
    {
        // Unwrap Convert expressions (e.g., boxing List&lt;T&gt; → object)
        if (propertyExpr is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convertExpr)
            propertyExpr = convertExpr.Operand;

        // Check for .ToList() call
        if (propertyExpr is not MethodCallExpression toListCall ||
            toListCall.Method.Name != "ToList" ||
            toListCall.Arguments.Count == 0)
            return false;

        var toListSource = toListCall.Arguments[0];

        // Unwrap Convert on the source too
        if (toListSource is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } srcConvert)
            toListSource = srcConvert.Operand;

        // Walk through Where/OrderBy chains to find the Select
        MethodCallExpression? selectCall = null;
        var wherePredicates = new List<LambdaExpression>();
        var current = toListSource;
        while (current is MethodCallExpression chainMc)
        {
            if (chainMc.Method.Name == "Select" && chainMc.Arguments.Count >= 2)
            {
                selectCall = chainMc;
                current = chainMc.Arguments.Count > 0 ? chainMc.Arguments[0] : null;
                if (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } sc)
                    current = sc.Operand;
                continue;
            }

            if (chainMc.Method.Name == "Where" && chainMc.Arguments.Count >= 2)
            {
                var whereArg = chainMc.Arguments[1];
                if (whereArg is UnaryExpression { NodeType: ExpressionType.Quote } wq)
                    whereArg = wq.Operand;
                if (whereArg is LambdaExpression whereLambda)
                    wherePredicates.Add(whereLambda);
            }

            current = chainMc.Arguments.Count > 0 ? chainMc.Arguments[0] : null;
            if (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } chainConv)
                current = chainConv.Operand;
        }

        if (selectCall == null)
            return false;

        // Check if the chain's starting source is an IGrouping parameter
        var chainStart = toListSource;
        while (chainStart is MethodCallExpression chainMc)
        {
            chainStart = chainMc.Arguments.Count > 0 ? chainMc.Arguments[0] : chainStart;
            if (chainStart is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } chainConv)
                chainStart = chainConv.Operand;
        }

        var isGroupingParam = chainStart is ParameterExpression paramExpr &&
                              paramExpr.Type.IsGenericType &&
                              paramExpr.Type.GetGenericTypeDefinition().Name.Contains("IGrouping");

        if (!isGroupingParam)
            return false;

        // Extract the inner lambda
        var lambdaArg = selectCall.Arguments[1];
        if (lambdaArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            lambdaArg = quote.Operand;
        if (lambdaArg is not LambdaExpression innerLambda)
            return false;

        // Get current hop aliases for path segment resolution inside the collect
        var hop = _context.Scope.LastPathSegmentHop >= 0
            ? _context.Scope.LastPathSegmentHop
            : (_context.Scope.CurrentHop > 0 ? _context.Scope.CurrentHop - 1 : 0);
        var hopAliases = _context.Scope.GetHopAliases(hop);

        string srcAlias = "src0", relAlias = "r0", tgtAlias = "tgt0";
        if (hopAliases.HasValue)
            (srcAlias, relAlias, tgtAlias) = hopAliases.Value;

        // Build collect expression
        var innerParam = innerLambda.Parameters[0];
        string collectExpr;
        try
        {
            collectExpr = CollectExpressionTranslator.TranslateInnerSelectBody(innerLambda.Body, innerParam, srcAlias, relAlias, tgtAlias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to translate nested collect for property {Property}", propertyName);
            return false;
        }

        // Inject collected Where predicates as additional WHERE clauses
        var alias = _context.Scope.CurrentAlias ?? "src0";
        foreach (var wherePred in wherePredicates)
        {
            try
            {
                var whereCypher = CollectExpressionTranslator.TranslateInnerExpression(
                    wherePred.Body, wherePred.Parameters[0], srcAlias, relAlias, tgtAlias);
                _context.AddFragment(new WhereFragment(
                    whereCypher, ImmutableArray<string>.Empty, alias));
                _logger.LogDebug("Injected inner Where filter for collect: {Where}", whereCypher);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to translate inner Where predicate for {Property}", propertyName);
            }
        }

        // Emit a CollectFragment
        _context.AddFragment(new CollectFragment(collectExpr, alias, cypherAlias));

        // Add to the RETURN clause
        returns.Add($"collect({collectExpr}) AS {cypherAlias}");

        _logger.LogDebug("Emitted CollectFragment for {Property}: collect({Expr}) AS {Alias}",
            propertyName, collectExpr, cypherAlias);
        return true;
    }
}
