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

namespace Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core;

using System;
using System.Collections.Immutable;
using System.Linq.Expressions;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Visitors.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles LINQ Join operations for AGE Cypher queries.
/// </summary>
internal sealed class JoinHandler
{
    private readonly CypherQueryContext _context;
    private readonly ILogger _logger;
    private readonly Func<Expression, Expression> _visit;
    private readonly Func<Expression, LambdaExpression?> _extractLambda;

    public JoinHandler(
        CypherQueryContext context,
        ILogger logger,
        Func<Expression, Expression> visit,
        Func<Expression, LambdaExpression?> extractLambda)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _visit = visit ?? throw new ArgumentNullException(nameof(visit));
        _extractLambda = extractLambda ?? throw new ArgumentNullException(nameof(extractLambda));
    }

    public Expression HandleJoin(MethodCallExpression node)
    {
        if (node.Arguments.Count != 5)
            throw new ArgumentException("Join method must have exactly 5 arguments");

        _logger.LogDebug("Processing JOIN operation");

        var outer = node.Arguments[0];
        var inner = node.Arguments[1];
        var outerKeySelector = _extractLambda(node.Arguments[2]);
        var innerKeySelector = _extractLambda(node.Arguments[3]);
        var resultSelector = _extractLambda(node.Arguments[4]);

        if (outerKeySelector == null || innerKeySelector == null || resultSelector == null)
            throw new ArgumentException("Join method requires lambda expressions for all selectors");

        _visit(outer);

        var resultBody = resultSelector.Body;
        if (resultBody is ParameterExpression paramExpr)
        {
            var parameterIndex = -1;
            for (int i = 0; i < resultSelector.Parameters.Count; i++)
            {
                if (resultSelector.Parameters[i].Name == paramExpr.Name)
                {
                    parameterIndex = i;
                    break;
                }
            }

            if (parameterIndex == 0)
            {
                var relationshipAlias = _context.Scope.GetNumberedAlias("r");
                _context.Scope.CurrentAlias = relationshipAlias;
                var returns = ImmutableArray.Create(relationshipAlias);
                var projectionFragment = new ProjectionFragment(returns, relationshipAlias);
                _context.AddFragment(projectionFragment);
                _logger.LogDebug("JOIN: Emitted ProjectionFragment for relationship alias {Alias}", relationshipAlias);
            }
            else if (parameterIndex == 1)
            {
                var targetAlias = _context.Scope.GetNumberedAlias("tgt");
                _context.Scope.CurrentAlias = targetAlias;
                var returns = ImmutableArray.Create(targetAlias);
                var projectionFragment = new ProjectionFragment(returns, targetAlias);
                _context.AddFragment(projectionFragment);
                _logger.LogDebug("JOIN: Emitted ProjectionFragment for node alias {Alias}", targetAlias);
            }
            else
            {
                throw new ArgumentException($"Invalid parameter reference in Join result selector: {paramExpr.Name}");
            }
        }
        else
        {
            throw new NotSupportedException("Complex projections in Join result selector are not yet supported");
        }

        return outer;
    }
}
