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

namespace Cvoya.Graph.Model.Age.Querying.Cypher.Visitors;

using System.Collections.Generic;
using System.Linq.Expressions;
using Cvoya.Graph.Model.Age.Core.Entities;
using Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Visitors.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core.ExpressionTranslationHelper;

/// <summary>
/// Translates .NET LINQ expressions to Cypher expressions for AGE.
/// Simplified version focused on common query operations.
/// </summary>
internal sealed class AgeExpressionToCypherVisitor : ExpressionVisitor
{
    private readonly CypherQueryContext _context;
    private readonly QueryParameterStore _parameterStore;
    private readonly ILogger _logger;
    private readonly string _alias = null!;
    private readonly ParameterExpression? _pathSegmentParameter;
    private readonly string? _sourceAlias;
    private readonly string? _relationshipAlias;
    private readonly string? _targetAlias;
    private readonly StringMethodHandler _stringHandler;
    private readonly MathMethodHandler _mathHandler;
    private readonly DateTimeMethodHandler _dateTimeHandler;
    private readonly CollectionExpressionHandler _collectionHandler;
    private readonly ClosureCaptureHandler _closureCaptureHandler;
    private readonly MemberExpressionHandler _memberHandler;

    public AgeExpressionToCypherVisitor(
        CypherQueryContext context,
        ILogger? logger = null,
        string alias = "n",
        ParameterExpression? pathSegmentParameter = null,
        string? sourceAlias = null,
        string? relationshipAlias = null,
        string? targetAlias = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _parameterStore = context.ParameterStore;
        _logger = logger ?? NullLogger.Instance;
        _alias = alias;
        _pathSegmentParameter = pathSegmentParameter;
        _sourceAlias = sourceAlias;
        _relationshipAlias = relationshipAlias;
        _targetAlias = targetAlias;

        _stringHandler = new StringMethodHandler(
            visitAndReturnCypher: VisitAndReturnCypher,
            addParameter: AddParameter,
            logger: _logger);
        _mathHandler = new MathMethodHandler(visitAndReturnCypher: VisitAndReturnCypher);
        _dateTimeHandler = new DateTimeMethodHandler(
            visitAndReturnCypher: VisitAndReturnCypher,
            addParameter: AddParameter);
        _collectionHandler = new CollectionExpressionHandler(
            visitAndReturnCypher: VisitAndReturnCypher,
            visit: Visit,
            addParameter: AddParameter,
            logger: _logger);
        _closureCaptureHandler = new ClosureCaptureHandler(_logger, _sourceAlias ?? _alias ?? "src0");
        _memberHandler = new MemberExpressionHandler(
            _context, _logger, _alias ?? "n",
            _sourceAlias, _relationshipAlias, _targetAlias,
            isPathSegmentContext: _pathSegmentParameter != null,
            tryEvaluateStatic: (MemberExpression m, bool _) =>
            {
                var ok = TryEvaluateStaticMember(m, out var v);
                return (ok, v);
            },
            visitAndReturnCypher: VisitAndReturnCypher,
            addParameter: AddParameter);
    }

    private string AddParameter(object? value)
    {
        return _parameterStore.Add(value);
    }

    private static bool TryEvaluateStaticMember(MemberExpression node, out object? value)
    {
        try
        {
            var objectMember = Expression.Convert(node, typeof(object));
            var getterLambda = Expression.Lambda<Func<object>>(objectMember);
            var getter = getterLambda.Compile();
            value = getter();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Visits an expression and returns the resulting Cypher string.
    /// </summary>
    public string VisitAndReturnCypher(Expression expression)
    {
        _logger.LogDebug("Starting VisitAndReturnCypher with expression type: {ExpressionType}", expression.GetType().Name);
        var result = Visit(expression);
        _logger.LogDebug("VisitAndReturnCypher result type: {ResultType}, value: {Result}", result?.GetType().Name, result);
        return result switch
        {
            ConstantExpression { Value: string cypherString } => cypherString,
            _ => throw new InvalidOperationException($"Expected Cypher string result, got {result?.GetType()}")
        };
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        // Handle null comparisons: x = null → x IS NULL, x <> null → x IS NOT NULL
        // Using IS NULL/IS NOT NULL is required in Cypher/AGE for correct null handling
        var isRightNull = node.Right is ConstantExpression cr && cr.Value is null;
        var isLeftNull = node.Left is ConstantExpression cl && cl.Value is null;

        if (isRightNull || isLeftNull)
        {
            var nonNullSide = isRightNull ? VisitAndReturnCypher(node.Left) : VisitAndReturnCypher(node.Right);
            var cypher = node.NodeType switch
            {
                ExpressionType.Equal => $"{nonNullSide} IS NULL",
                ExpressionType.NotEqual => $"{nonNullSide} IS NOT NULL",
                _ => throw new NotSupportedException($"Null comparison with operator {node.NodeType} is not supported")
            };
            return Expression.Constant(cypher);
        }

        var left = VisitAndReturnCypher(node.Left);
        var right = VisitAndReturnCypher(node.Right);

        var cypherResult = node.NodeType switch
        {
            ExpressionType.Equal => $"{left} = {right}",
            ExpressionType.NotEqual => $"{left} <> {right}",
            ExpressionType.GreaterThan => $"{left} > {right}",
            ExpressionType.GreaterThanOrEqual => $"{left} >= {right}",
            ExpressionType.LessThan => $"{left} < {right}",
            ExpressionType.LessThanOrEqual => $"{left} <= {right}",
            ExpressionType.AndAlso => $"({left} AND {right})",
            ExpressionType.OrElse => $"({left} OR {right})",
            ExpressionType.Add => $"({left} + {right})",
            ExpressionType.Subtract => $"({left} - {right})",
            ExpressionType.Multiply => $"({left} * {right})",
            ExpressionType.Divide => $"({left} / {right})",
            ExpressionType.Modulo => $"({left} % {right})",
            _ => throw new NotSupportedException($"Binary operator {node.NodeType} is not supported")
        };

        return Expression.Constant(cypherResult);
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        return _memberHandler.VisitMember(node);
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        _logger.LogDebug("Visiting constant: {Value} (Type: {Type})", node.Value, node.Type);
        // Add constant value as a parameter
        var paramRef = AddParameter(node.Value);
        _logger.LogDebug("Created parameter reference: {ParamRef}", paramRef);
        return Expression.Constant(paramRef);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Handle implicit conversion operators (op_Implicit)
        // These are generated by C# compiler for implicit type conversions
        if (node.Method.Name == "op_Implicit" && node.Arguments.Count == 1)
        {
            // Just visit the argument being converted, ignore the conversion
            return Visit(node.Arguments[0]);
        }

        // Handle Math methods
        if (node.Method.DeclaringType == typeof(Math))
        {
            return _mathHandler.HandleMathMethod(node);
        }

        // Handle string methods
        if (node.Method.DeclaringType == typeof(string))
        {
            return _stringHandler.HandleStringMethod(node);
        }

        // Handle DateTime methods (both static and instance)
        if (node.Method.DeclaringType == typeof(DateTime))
        {
            return _dateTimeHandler.HandleDateTimeMethod(node);
        }

        // Handle closure-captured IEnumerable<IRelationship>.Count(lambda) patterns FIRST
        // Must be before the generic Count handler which would intercept all Count calls
        if (node.Method.Name == "Count")
        {
            if (_closureCaptureHandler.TryHandleClosureCountOnRelationship(node))
                return Expression.Constant(_closureCaptureHandler.HandleClosureCountOnRelationship(node));
            return _collectionHandler.HandleCountMethod(node);
        }

        // Handle IGrouping aggregation: group.Average/Max/Min/Sum(lambda)
        if ((node.Method.Name == "Average" || node.Method.Name == "Max" ||
             node.Method.Name == "Min" || node.Method.Name == "Sum")
            && node.Arguments.Count >= 1)
        {
            return _collectionHandler.HandleGroupingAggregation(node, node.Method.Name.ToLowerInvariant());
        }

        // Handle collection methods (Contains, Any, etc.) - but not string.Contains
        if (node.Method.Name == "Contains" && node.Arguments.Count >= 1 && node.Method.DeclaringType != typeof(string))
        {
            return _collectionHandler.HandleContainsMethod(node);
        }

        // Handle .ToList() on nested Select expressions from GroupBy results
        // Pattern: group.Select(p => new { ... }).ToList() → collect({...})
        if (node.Method.Name == "ToList" && node.Arguments.Count == 1)
        {
            try
            {
                return HandleToListOnNestedSelect(node);
            }
            catch
            {
                // Fall through to compile-time evaluation
            }
        }

        // For any other method call, try to evaluate it at compile time
        try
        {
            var objectMember = Expression.Convert(node, typeof(object));
            var getterLambda = Expression.Lambda<Func<object>>(objectMember);
            var getter = getterLambda.Compile();
            var value = getter();

            var paramRef = AddParameter(value);
            return Expression.Constant(paramRef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate method call: {Method}", node.Method.Name);
            throw new NotSupportedException($"Method {node.Method.Name} is not supported");
        }
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        // Handle NOT operator
        if (node.NodeType == ExpressionType.Not)
        {
            var operand = VisitAndReturnCypher(node.Operand);
            return Expression.Constant($"NOT ({operand})");
        }

        // Handle type conversions
        if (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
        {
            var operandCypher = VisitAndReturnCypher(node.Operand);
            
            // For conversions to double or float, use toFloat() in Cypher
            if (node.Type == typeof(double) || node.Type == typeof(float) || 
                node.Type == typeof(double?) || node.Type == typeof(float?))
            {
                return Expression.Constant($"toFloat({operandCypher})");
            }
            
            // For other conversions, just pass through
            return Visit(node.Operand);
        }

        return base.VisitUnary(node);
    }

    protected override Expression VisitConditional(ConditionalExpression node)
    {
        // Translate C# ternary operator (condition ? trueValue : falseValue) to Cypher CASE expression
        // Example: p.Age >= 30 ? "Adult" : "Young" => CASE WHEN p.Age >= 30 THEN "Adult" ELSE "Young" END
        var condition = VisitAndReturnCypher(node.Test);
        var ifTrue = VisitAndReturnCypher(node.IfTrue);
        var ifFalse = VisitAndReturnCypher(node.IfFalse);

        var caseExpression = $"CASE WHEN {condition} THEN {ifTrue} ELSE {ifFalse} END";
        return Expression.Constant(caseExpression);
    }

    protected override Expression VisitNew(NewExpression node)
    {
        // Handle anonymous type creation: new { Start = ps.StartNode.FirstName, End = ps.EndNode.FirstName }
        // Convert to comma-separated list of property expressions
        var memberNames = new List<string>();
        var expressions = new List<string>();
        
        // Track which Cypher variables (aliases) are already being returned to avoid duplicates
        // Key: Cypher variable (e.g., "src0", "r0", "tgt0"), Value: column name it's returned as
        var returnedVariables = new Dictionary<string, string>();

        // First pass: scan for non-PathSegment arguments to track what's already being returned
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            var argument = node.Arguments[i];
            
            // Skip PathSegment parameters in first pass
            if (argument is ParameterExpression paramExpr && IsPathSegmentType(paramExpr.Type))
                continue;
            
            // Visit the argument to get the Cypher expression
            var cypherExpression = VisitAndReturnCypher(argument);
            
            // Extract the base Cypher variable (e.g., "src0" from "src0.FirstName" or just "src0")
            var baseVariable = cypherExpression.Split('.')[0].Trim();
            var memberName = node.Members?[i]?.Name ?? $"Item{i + 1}";
            
            // Track this variable as being returned
            if (!string.IsNullOrEmpty(baseVariable))
            {
                returnedVariables[baseVariable] = $"c_{memberName}";
            }
        }

        // Second pass: process all arguments and build expressions
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            var argument = node.Arguments[i];
            var memberName = node.Members?[i]?.Name ?? $"Item{i + 1}";
            
            // Special handling for PathSegment parameter projections
            if (argument is ParameterExpression paramExpr && IsPathSegmentType(paramExpr.Type))
            {
                // PathSegment projections need to return all three components (source, relationship, target)
                // so they can be reconstructed into a PathSegment object by the result processor.
                // Always emit all 3 columns even if some are duplicates of other members —
                // BuildColumnDefinitions expands path segments into 3 columns so the counts must match.
                
                var pathSegmentParts = new List<string>();
                
                if (_sourceAlias != null)
                {
                    pathSegmentParts.Add($"{_sourceAlias} AS c_{memberName}_{_sourceAlias}");
                    returnedVariables[_sourceAlias] = $"c_{memberName}_{_sourceAlias}";
                }
                
                if (_relationshipAlias != null)
                {
                    pathSegmentParts.Add($"{_relationshipAlias} AS c_{memberName}_{_relationshipAlias}");
                    returnedVariables[_relationshipAlias] = $"c_{memberName}_{_relationshipAlias}";
                }
                
                if (_targetAlias != null)
                {
                    pathSegmentParts.Add($"{_targetAlias} AS c_{memberName}_{_targetAlias}");
                    returnedVariables[_targetAlias] = $"c_{memberName}_{_targetAlias}";
                }
                
                if (pathSegmentParts.Count > 0)
                {
                    var pathSegmentProjection = string.Join(", ", pathSegmentParts);
                    memberNames.Add(memberName);
                    expressions.Add(pathSegmentProjection);
                    _logger.LogDebug("Added PathSegment projection: {Projection}", pathSegmentProjection);
                }
                else
                {
                    // All components already returned - still need to track this member for result processing
                    memberNames.Add(memberName);
                    // Add placeholder to maintain member count (will be handled by result processor)
                    _logger.LogDebug("PathSegment {MemberName} has no new columns; components already returned", memberName);
                }
            }
            else
            {
                // Visit the argument to get the Cypher expression
                var cypherExpression = VisitAndReturnCypher(argument);
                
                memberNames.Add(memberName);
                expressions.Add($"{cypherExpression} AS c_{memberName}");
            }
        }

        // Return the combined expression for the SELECT clause
        var selectExpression = string.Join(", ", expressions);
        _logger.LogDebug("VisitNew returning select expression: {Expression}", selectExpression);
        return Expression.Constant(selectExpression);
    }

    // String methods moved to StringMethodHandler.
    // Math methods moved to MathMethodHandler.
    // DateTime methods moved to DateTimeMethodHandler.
    // Collection methods (Contains, Count, GroupingAggregation) moved to CollectionExpressionHandler.

    /// <summary>
    /// Handles .ToList() on nested Select expressions from GroupBy results.
    /// Pattern: group.Select(p => new { Foo = p.EndNode.FirstName }).ToList()
    /// Translates to: collect({Foo: tgt0.FirstName})
    /// Uses top-down expression tree walking to avoid the visitor's inside-out ConstantExpression issue.
    /// </summary>
    private Expression HandleToListOnNestedSelect(MethodCallExpression node)
    {
        var selectSource = node.Arguments[0];

        if (selectSource is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } ue)
            selectSource = ue.Operand;

        if (selectSource is not MethodCallExpression selectCall ||
            selectCall.Method.Name != "Select" ||
            selectCall.Arguments.Count < 2)
        {
            throw new InvalidOperationException("ToList source is not a Select call");
        }

        var groupSource = selectCall.Arguments[0];
        if (groupSource is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } gsConvert)
            groupSource = gsConvert.Operand;

        if (groupSource is ParameterExpression groupParam &&
            groupParam.Type.IsGenericType &&
            groupParam.Type.GetGenericTypeDefinition().Name.Contains("IGrouping"))
        {
            var lambdaArg = selectCall.Arguments[1];
            if (lambdaArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                lambdaArg = quote.Operand;
            var innerLambda = (LambdaExpression)lambdaArg;
            var innerParam = innerLambda.Parameters[0]; // e.g., "p" of type IGraphPathSegment

            // Use top-down walking — resolves p.EndNode.FirstName → tgt0.FirstName directly
            string collectExpr = TranslateForCollect(innerLambda.Body, innerParam);

            _logger.LogDebug("Translated group.Select().ToList() to collect({Expr})", collectExpr);
            return Expression.Constant($"collect({collectExpr})");
        }

        throw new InvalidOperationException("ToList source Select is not on an IGrouping");
    }

    /// <summary>
    /// Top-down translation of a Select lambda body to a Cypher expression for collect().
    /// Handles NewExpression (anonymous types), MemberExpression (property chains),
    /// BinaryExpression (arithmetic), MethodCallExpression (.Days, .Subtract), etc.
    /// Unlike VisitAndReturnCypher, this walks the tree top-down to correctly resolve
    /// path segment chains like p.EndNode.FirstName → tgt0.FirstName.
    /// </summary>
    private string TranslateForCollect(Expression body, ParameterExpression param)
    {
        return CollectExpressionTranslator.TranslateInnerSelectBody(
            body, param,
            _sourceAlias ?? "src0",
            _relationshipAlias ?? "r0",
            _targetAlias ?? "tgt0");
    }

    /// <summary>
    /// Top-down translation of a single expression within an inner Select lambda.
    /// </summary>
    private string TranslateExpressionForCollect(Expression expr, ParameterExpression param)
    {
        return CollectExpressionTranslator.TranslateInnerExpression(
            expr, param,
            _sourceAlias ?? "src0",
            _relationshipAlias ?? "r0",
            _targetAlias ?? "tgt0");
    }

    private string TranslateMethodForCollect(MethodCallExpression mc, ParameterExpression param)
    {
        return CollectExpressionTranslator.TranslateInnerMethodCall(
            mc, param,
            _sourceAlias ?? "src0",
            _relationshipAlias ?? "r0",
            _targetAlias ?? "tgt0");
    }

    /// <summary>
    /// Handles parameter expressions, particularly for PathSegment parameters.
    /// </summary>
    protected override Expression VisitParameter(ParameterExpression node)
    {
        _logger.LogDebug("VisitParameter called with parameter: {Name}, Type: {Type}", node.Name, node.Type.Name);
        
        // Check if this is the path segment parameter we're tracking
        if (_pathSegmentParameter != null && node == _pathSegmentParameter)
        {
            _logger.LogDebug("Parameter matches tracked PathSegment parameter, returning {Alias}", _alias);
            return Expression.Constant(_alias);
        }
        
        // For scalar projections (e.g., OrderBy(name => name) after Select(p => p.FirstName)),
        // the parameter represents the projected value, so return the current alias
        if (!typeof(INode).IsAssignableFrom(node.Type) && 
            !typeof(IRelationship).IsAssignableFrom(node.Type) &&
            !typeof(IGraphPathSegment).IsAssignableFrom(node.Type))
        {
            _logger.LogDebug("Parameter is scalar projection, returning alias: {Alias}", _alias);
            return Expression.Constant(_alias);
        }
        
        // For node/relationship parameters, return the alias
        _logger.LogDebug("Parameter is node/relationship, returning alias: {Alias}", _alias);
        return Expression.Constant(_alias);
    }

    private static bool IsPathSegmentType(Type type)
        => ExpressionTranslationHelper.IsPathSegmentType(type);

    // MapPropertyName and TryCompileEval are now imported via
    // `using static ExpressionTranslationHelper` at the top of the file.
}
