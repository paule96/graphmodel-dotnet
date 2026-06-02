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

using System.Collections.Immutable;
using System.Linq.Expressions;
using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core.Modular;
using Cvoya.Graph.Model.Age.Querying.Linq.Queryables;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Visitors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// AGE implementation of CypherQueryVisitor that translates LINQ expressions to Cypher queries.
/// Modeled after Neo4j's CypherQueryVisitor but adapted for AGE's specific Cypher dialect and infrastructure.
/// This visitor replaces the primitive type checking approach with comprehensive LINQ method routing.
/// </summary>
internal sealed class AgeCypherQueryVisitor : ExpressionVisitor
{
    private readonly CypherQueryContext _context;
    private readonly ILogger<AgeCypherQueryVisitor> _logger;
    
    // Modular specialized visitors for different query concerns
    private readonly TraversalFragmentVisitor _traversalVisitor;
    private readonly FilteringFragmentVisitor _filteringVisitor;
    private readonly ProjectionFragmentVisitor _projectionVisitor;
    private readonly AggregationFragmentVisitor _aggregationVisitor;
    private readonly JoinHandler _joinHandler;
    private readonly SearchHandler _searchHandler;
    private readonly MaterializationHandler _materializationHandler;
    private readonly QueryInitializationHandler _queryInitHandler;
    private readonly PathSegmentHandler _pathSegmentHandler;

    public AgeCypherQueryVisitor(CypherQueryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = context.LoggerFactory?.CreateLogger<AgeCypherQueryVisitor>() ?? NullLogger<AgeCypherQueryVisitor>.Instance;
        
        // Initialize modular specialized visitors
        _traversalVisitor = new TraversalFragmentVisitor(_context, _logger);
        _filteringVisitor = new FilteringFragmentVisitor(_context, _logger);
        _projectionVisitor = new ProjectionFragmentVisitor(_context, _logger);
        _aggregationVisitor = new AggregationFragmentVisitor(_context, _logger);
        _joinHandler = new JoinHandler(_context, _logger, Visit, ExtractLambda);
        _queryInitHandler = new QueryInitializationHandler(_context, _logger, GetContextualAlias, EmitWhereFragment);
        _searchHandler = new SearchHandler(_context, _logger, Visit, _queryInitHandler.SetupInitialMatch, EmitWhereFragment);
        _materializationHandler = new MaterializationHandler(
            _context, _logger, Visit, GetContextualAlias, EmitWhereFragment, ExtractLambda);
        _pathSegmentHandler = new PathSegmentHandler(_context, _logger, Visit, _traversalVisitor);
    }

    /// <summary>
    /// Main entry point - visits the root expression and builds the complete Cypher query.
    /// </summary>
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        _logger.LogDebug("Processing LINQ method: {Method}", node.Method.Name);

        // Route LINQ methods to specific handlers
        return node.Method.Name switch
        {
            // Core LINQ methods
            "Where" => HandleWhere(node),
            "Select" => HandleSelect(node),
            "GroupBy" => HandleGroupBy(node),
            "Join" => _joinHandler.HandleJoin(node),
            "OrderBy" => HandleOrderBy(node, descending: false),
            "OrderByDescending" => HandleOrderBy(node, descending: true),
            "ThenBy" => HandleThenBy(node, descending: false),
            "ThenByDescending" => HandleThenBy(node, descending: true),
            "Take" => HandleTake(node),
            "Skip" => HandleSkip(node),
            "Distinct" => HandleDistinct(node),

            // Graph traversal methods
            "PathSegments" => _pathSegmentHandler.HandlePathSegments(node),
            "WithDepth" => _pathSegmentHandler.HandleWithDepth(node),
            "Direction" => _pathSegmentHandler.HandleDirection(node),

            // Full-text search from LINQ chain (e.g., .Where().Search("text"))
            "Search" => _searchHandler.HandleSearch(node),

            // Aggregation methods
            "Count" or "CountAsync" or "CountAsyncMarker" => HandleCount(node),
            "LongCount" or "LongCountAsync" or "LongCountAsyncMarker" => HandleCount(node),
            "Any" or "AnyAsync" or "AnyAsyncMarker" => HandleAny(node),
            "All" or "AllAsync" or "AllAsyncMarker" => HandleAll(node),
            "Sum" or "SumAsync" or "SumAsyncMarker" => HandleSum(node),
            "Average" or "AverageAsync" or "AverageAsyncMarker" => HandleAverage(node),
            "Min" or "MinAsync" or "MinAsyncMarker" => HandleMin(node),
            "Max" or "MaxAsync" or "MaxAsyncMarker" => HandleMax(node),

            // Element access methods
            "First" or "FirstAsync" or "FirstAsyncMarker" => _materializationHandler.HandleFirst(node),
            "FirstOrDefault" or "FirstOrDefaultAsync" or "FirstOrDefaultAsyncMarker" => _materializationHandler.HandleFirst(node),
            "Last" or "LastAsync" or "LastAsyncMarker" => _materializationHandler.HandleLast(node),
            "LastOrDefault" or "LastOrDefaultAsync" or "LastOrDefaultAsyncMarker" => _materializationHandler.HandleLast(node),
            "Single" or "SingleAsync" or "SingleAsyncMarker" => _materializationHandler.HandleSingle(node),
            "SingleOrDefault" or "SingleOrDefaultAsync" or "SingleOrDefaultAsyncMarker" => _materializationHandler.HandleSingle(node),

            // Materialization methods
            "ToList" or "ToListAsync" or "ToListAsyncMarker" => _materializationHandler.HandleToList(node),
            "ToArray" or "ToArrayAsync" or "ToArrayAsyncMarker" => _materializationHandler.HandleToList(node),

            // If this is not a LINQ method, continue traversing
            _ => base.VisitMethodCall(node)
        };
    }

    private Expression HandleWhere(MethodCallExpression node)
    {
        // Visit the source expression FIRST to ensure full traversal (e.g., SetupInitialMatch for root queries)
        // This sets CurrentAlias which is needed by FilteringFragmentVisitor
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized filtering visitor which handles WHERE clauses and emits fragments
        _filteringVisitor.HandleWhere(node);
        
        return node;
    }

    private Expression HandleSelect(MethodCallExpression node)
    {
        // Visit the source expression FIRST to set up MATCH patterns and increment hop counter
        Visit(node.Arguments[0]);
        
        // Then delegate projection handling to specialized visitor which emits ProjectionFragment
        _projectionVisitor.HandleSelect(node);
        
    // For projections, disable complex property loading and emit toggle fragment
    var complexPropertyFragment = new ComplexPropertyLoadingFragment(false, _context.Scope.CurrentAlias);
    _context.AddFragment(complexPropertyFragment);
        _logger.LogDebug("Emitted ComplexPropertyLoadingFragment (disabled)");
        
        return node;
    }

    private Expression HandleGroupBy(MethodCallExpression node)
    {
        // Visit source FIRST to ensure the MATCH pattern is generated (proper visitor pattern: children before parent)
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized projection visitor which handles GroupBy and emits fragments
        _projectionVisitor.HandleGroupBy(node);
        
        return node;
    }

    // HandleJoin moved to JoinHandler. Dispatch: _joinHandler.HandleJoin(node)

    private Expression HandleOrderBy(MethodCallExpression node, bool descending)
    {
        // Visit source FIRST to ensure CurrentAlias is set (proper visitor pattern: children before parent)
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized filtering visitor which handles ordering and emits fragments
        _filteringVisitor.HandleOrderBy(node, descending, isThenBy: false);
        
        return node;
    }

    private Expression HandleThenBy(MethodCallExpression node, bool descending)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized filtering visitor (ThenBy is just additional ordering)
        _filteringVisitor.HandleOrderBy(node, descending, isThenBy: true);
        
        return node;
    }

    private Expression HandleTake(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized filtering visitor for pagination
        _filteringVisitor.HandleTake(node);
        
        return node;
    }

    private Expression HandleSkip(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized filtering visitor for pagination
        _filteringVisitor.HandleSkip(node);
        
        return node;
    }

    private Expression HandleDistinct(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized filtering visitor for distinctness
        _filteringVisitor.HandleDistinct(node);
        
        return node;
    }

    private Expression HandleCount(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleCount(node);
        
        return node;
    }

    private Expression HandleAny(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleAny(node);
        
        return node;
    }

    private Expression HandleAll(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleAll(node);
        
        return node;
    }

    private Expression HandleSum(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleAggregationFunction(node, "SUM");
        
        return node;
    }

    private Expression HandleAverage(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleAggregationFunction(node, "AVG");
        
        return node;
    }

    private Expression HandleMin(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleAggregationFunction(node, "MIN");
        
        return node;
    }

    private Expression HandleMax(MethodCallExpression node)
    {
        // Visit source FIRST to ensure CurrentAlias is set
        Visit(node.Arguments[0]);
        
        // Then delegate to specialized aggregation visitor
        _aggregationVisitor.HandleAggregationFunction(node, "MAX");
        
        return node;
    }



    private void EmitWhereFragment(string predicate, string? alias = null, ImmutableArray<string> consumedAliases = default)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return;
        }

        var normalizedConsumed = consumedAliases.IsDefault ? ImmutableArray<string>.Empty : consumedAliases;
        var currentAlias = alias ?? _context.Scope.CurrentAlias ?? "src0";
        var fragment = new WhereFragment(predicate, normalizedConsumed, currentAlias);
    _context.AddFragment(fragment);
        _logger.LogDebug("Emitted WhereFragment for alias {Alias}: {Predicate}", currentAlias, predicate);
    }



    /// <summary>
    /// This is called for the root queryable (e.g., context.Nodes&lt;Person&gt;())
    /// Sets up the initial MATCH clause based on the element type.
    /// </summary>
    protected override Expression VisitConstant(ConstantExpression node)
    {
        // Check if this is a queryable root
        if (node.Value != null && node.Type.IsGenericType)
        {
            var genericTypeDefinition = node.Type.GetGenericTypeDefinition();
            if (genericTypeDefinition.Name.Contains("Queryable"))
            {
                var elementType = node.Type.GetGenericArguments().FirstOrDefault();
                if (elementType != null)
                {
                    _queryInitHandler.SetupInitialMatch(elementType);
                }
            }
        }

        return base.VisitConstant(node);
    }

    protected override Expression VisitExtension(Expression node)
    {
        if (node is AgeFullTextSearchExpression searchExpr)
        {
            _logger.LogDebug("Handling AGE full text search expression for query: {Query}", searchExpr.SearchQuery);
            _searchHandler.HandleAgeFullTextSearch(searchExpr);
            return node;
        }

        return base.VisitExtension(node);
    }

    internal static T EvaluateConstantExpression<T>(Expression expression)
    {
        if (expression is ConstantExpression constant && constant.Value is T value)
        {
            return value;
        }

        // Try to evaluate the expression
        try
        {
            var lambda = Expression.Lambda<Func<T>>(Expression.Convert(expression, typeof(T)));
            var compiled = lambda.Compile();
            return compiled();
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Cannot evaluate expression to {typeof(T).Name}", ex);
        }
    }

    private static LambdaExpression? ExtractLambda(Expression expression)
    {
        // Handle quoted lambda expressions
        if (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }

        return expression as LambdaExpression;
    }

    /// <summary>
    /// Gets the appropriate alias for the current context.
    /// In path segment contexts, returns "src" (source node alias).
    /// In relationship contexts, returns "r" (relationship alias).
    /// In regular node contexts, returns "n".
    /// </summary>
    /// <returns>The contextual alias to use for queries.</returns>
    private string GetContextualAlias()
    {
        bool isInPathContext = _context.FragmentSequence.OfType<MatchSegmentFragment>().Any();
        if (isInPathContext)
        {
            return _context.Scope.GetNumberedAlias("src"); // Use numbered source node alias in path segment contexts
        }
        
        // Check if this is a relationship query
        if (typeof(IRelationship).IsAssignableFrom(_context.Scope.RootType))
        {
            return _context.Scope.GetNumberedAlias("r"); // Use numbered relationship alias for relationship queries
        }
        
        // Use src as the standard alias for source nodes (consistent with Neo4j provider)
        return _context.Scope.GetNumberedAlias("src");
    }

    /// <summary>
    /// Finalizes the query by adding any missing default projections.
    /// This should be called after Visit() completes to ensure path segment queries
    /// without explicit ToList/ToArray calls still get proper projections.
    /// </summary>
    public void FinalizeQuery(Type elementType)
    {
        _materializationHandler.FinalizeQuery(elementType);
    }
}
