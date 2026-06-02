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
        // Special handling when we have a path segment parameter context
        if (_pathSegmentParameter != null)
        {
            // Handle path segment property access (e.g., ps.EndNode.Age)
            if (node.Expression is MemberExpression pathSegmentMember &&
                pathSegmentMember.Expression is ParameterExpression pathParam &&
                pathParam == _pathSegmentParameter)
            {
                _logger.LogDebug("Processing path segment property access: {Parameter}.{SegmentProperty}.{NodeProperty}",
                    pathParam.Name, pathSegmentMember.Member.Name, node.Member.Name);

                var segmentProperty = pathSegmentMember.Member.Name;
                var nodeProperty = node.Member.Name;

                var alias = segmentProperty switch
                {
                    nameof(IGraphPathSegment.StartNode) => _sourceAlias ?? "src0",
                    nameof(IGraphPathSegment.EndNode) => _targetAlias ?? "tgt0",
                    nameof(IGraphPathSegment.Relationship) => _relationshipAlias ?? "r0",
                    _ => throw new NotSupportedException($"Path segment property '{segmentProperty}' is not supported")
                };

                // Map C# property names to AGE property names
                var propertyName = MapPropertyName(nodeProperty);
                var result = $"{alias}.{propertyName}";
                _logger.LogDebug("Mapped path segment property {SegmentProperty}.{NodeProperty} to {Result}",
                    segmentProperty, nodeProperty, result);
                return Expression.Constant(result);
            }
        }

        // Handle static DateTime properties (e.g., DateTime.Now, DateTime.Today, DateTime.UtcNow)
        // AGE's Cypher doesn't support localdatetime()/date()/datetime(). Evaluate at compile time
        // and pass as parameters instead.
        if (node.Member.DeclaringType == typeof(DateTime) && node.Expression == null)
        {
            switch (node.Member.Name)
            {
                case "Now":
                case "Today":
                case "UtcNow":
                case "MaxValue":
                case "MinValue":
                    if (TryEvaluateStaticMember(node, out var evaluated))
                    {
                        var paramRef = AddParameter(evaluated);
                        return Expression.Constant(paramRef);
                    }
                    throw new NotSupportedException($"DateTime static property {node.Member.Name} is not supported");
                default:
                    if (TryEvaluateStaticMember(node, out var val))
                    {
                        var paramRef = AddParameter(val);
                        return Expression.Constant(paramRef);
                    }
                    throw new NotSupportedException($"DateTime static property {node.Member.Name} is not supported");
            }
        }

        // If accessing a member of the parameter (e.g., p.FirstName)
        if (node.Expression is ParameterExpression param)
        {
            // Special handling for IGrouping parameters (e.g., g.Key in GroupBy)
            if (param.Type.IsGenericType && param.Type.GetGenericTypeDefinition().Name.Contains("IGrouping"))
            {
                _logger.LogDebug("Processing IGrouping parameter property: {Property}", node.Member.Name);
                
                // g.Key should map to the GROUP BY expression from the last GroupByFragment
                if (node.Member.Name == "Key")
                {
                    var groupByFragment = _context.FragmentSequence.OfType<GroupByFragment>().LastOrDefault();
                    if (groupByFragment == null)
                    {
                        throw new InvalidOperationException("GroupBy fragment not found in context for g.Key access");
                    }
                    _logger.LogDebug("Mapped g.Key to GROUP BY expression: {Expression}", groupByFragment.Expression);
                    return Expression.Constant(groupByFragment.Expression);
                }
                
                throw new NotSupportedException($"IGrouping property '{node.Member.Name}' is not supported. Use g.Key for the grouping key.");
            }
            
            // Special handling for path segment parameters
            if (typeof(IGraphPathSegment).IsAssignableFrom(param.Type))
            {
                _logger.LogDebug("Processing path segment parameter property: {Property}", node.Member.Name);

                var propertyMapping = node.Member.Name switch
                {
                    nameof(IGraphPathSegment.StartNode) => _sourceAlias ?? "src0",
                    nameof(IGraphPathSegment.EndNode) => _targetAlias ?? "tgt0", 
                    nameof(IGraphPathSegment.Relationship) => _relationshipAlias ?? "r0",
                    _ => throw new NotSupportedException($"Path segment property '{node.Member.Name}' is not supported")
                };

                _logger.LogDebug("Mapped path segment property {Property} to alias {Alias}", node.Member.Name, propertyMapping);
                return Expression.Constant(propertyMapping);
            }

            // Map C# property names to AGE property names
            var propertyName = MapPropertyName(node.Member.Name);
            return Expression.Constant($"{_alias}.{propertyName}");
        }

        // Handle path segment property access through nested member expressions (e.g., ps.EndNode.FirstName)
        if (node.Expression is MemberExpression nestedMember &&
            nestedMember.Expression is ParameterExpression nestedParam &&
            typeof(IGraphPathSegment).IsAssignableFrom(nestedParam.Type))
        {
            _logger.LogDebug("Processing nested path segment property access: {Expression}", node);

            var segmentProperty = nestedMember.Member.Name;
            var nodeProperty = node.Member.Name;

            var alias = segmentProperty switch
            {
                nameof(IGraphPathSegment.StartNode) => _sourceAlias ?? "src0",
                nameof(IGraphPathSegment.EndNode) => _targetAlias ?? "tgt0",
                nameof(IGraphPathSegment.Relationship) => _relationshipAlias ?? "r0",
                _ => throw new NotSupportedException($"Path segment property '{segmentProperty}' is not supported")
            };

            _logger.LogDebug("Path segment alias mapping: {SegmentProperty} -> {Alias} (source={Source}, target={Target}, rel={Rel})", 
                segmentProperty, alias, _sourceAlias, _targetAlias, _relationshipAlias);
            
            // Add debug logging to identify the problem
            if (segmentProperty == nameof(IGraphPathSegment.StartNode) && _sourceAlias == null)
            {
                _logger.LogError("CRITICAL: _sourceAlias is null for StartNode access - falling back to 'src'");
            }
            if (segmentProperty == nameof(IGraphPathSegment.Relationship) && _relationshipAlias == null)
            {
                _logger.LogError("CRITICAL: _relationshipAlias is null for Relationship access - falling back to 'r'");
            }

            // Map C# property names to AGE property names
            var propertyName = MapPropertyName(nodeProperty);
            var result = $"{alias}.{propertyName}";
            _logger.LogDebug("Mapped path segment nested property {SegmentProperty}.{NodeProperty} to {Result}", 
                segmentProperty, nodeProperty, result);
            return Expression.Constant(result);
        }

        // Handle member access on a converted parameter (e.g., ((IEntity)p).Id)
        if (node.Expression is UnaryExpression unary && 
            (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked) &&
            unary.Operand is ParameterExpression)
        {
            // Map C# property names to AGE property names
            var propertyName = MapPropertyName(node.Member.Name);
            return Expression.Constant($"{_alias}.{propertyName}");
        }

        // Handle chained member access (e.g., p.FirstName.Length, p.Address.City)
        if (node.Expression is MemberExpression innerMember)
        {
            // Special case: string.Length property
            if (node.Member.Name == "Length" && node.Member.DeclaringType == typeof(string))
            {
                var innerValue = VisitAndReturnCypher(innerMember);
                return Expression.Constant($"size({innerValue})");
            }

            // Special case: DateTime properties (Year, Month, Day, etc.)
            if (node.Member.DeclaringType == typeof(DateTime))
            {
                var innerValue = VisitAndReturnCypher(innerMember);
                return node.Member.Name switch
                {
                    "Year" => Expression.Constant($"toInteger(substring({innerValue}, 0, 4))"),
                    "Month" => Expression.Constant($"toInteger(substring({innerValue}, 5, 2))"),
                    "Day" => Expression.Constant($"toInteger(substring({innerValue}, 8, 2))"),
                    "Hour" => Expression.Constant($"toInteger(substring({innerValue}, 11, 2))"),
                    "Minute" => Expression.Constant($"toInteger(substring({innerValue}, 14, 2))"),
                    "Second" => Expression.Constant($"toInteger(substring({innerValue}, 17, 2))"),
                    "DayOfWeek" => Expression.Constant($"toInteger(substring({innerValue}, 0, 4)) % 7"), // Approximate
                    _ => throw new NotSupportedException($"DateTime property {node.Member.Name} is not supported")
                };
            }

            // Handle nested property access (e.g., p.Address.City, c.A.B.Property1)
            // Build the property path by walking up the member expression chain
            var propertyPath = new List<string>();
            var current = node;
            Expression? baseExpression = null;
            
            while (current != null)
            {
                propertyPath.Insert(0, MapPropertyName(current.Member.Name));
                
                if (current.Expression is MemberExpression nextMember)
                {
                    current = nextMember;
                }
                else if (current.Expression is ParameterExpression baseParam)
                {
                    baseExpression = baseParam;
                    break;
                }
                else if (current.Expression is UnaryExpression unaryExpr && 
                         (unaryExpr.NodeType == ExpressionType.Convert || unaryExpr.NodeType == ExpressionType.ConvertChecked) &&
                         unaryExpr.Operand is ParameterExpression convertedParam)
                {
                    baseExpression = convertedParam;
                    break;
                }
                else
                {
                    // Not a simple parameter chain, try evaluation
                    current = null;
                }
            }
            
            // If we found a parameter at the base, construct the nested property path
            if (baseExpression is ParameterExpression bpExpr)
            {
                // Special handling for IGrouping parameters (e.g., group.Key.FirstName)
                if (bpExpr.Type.IsGenericType && bpExpr.Type.GetGenericTypeDefinition().Name.Contains("IGrouping"))
                {
                    // The first property path element should be "Key"
                    // Map group.Key to the GROUP BY expression, then append remaining properties
                    if (propertyPath.Count > 0 && propertyPath[0] == "Key")
                    {
                        var groupByFragment = _context.FragmentSequence.OfType<GroupByFragment>().LastOrDefault();
                        if (groupByFragment == null)
                            throw new InvalidOperationException("GroupBy fragment not found for IGrouping.Key access");

                        var resolvedKey = groupByFragment.Expression;
                        var remainingPath = propertyPath.Skip(1).ToList();
                        var resolvedPath = remainingPath.Count > 0
                            ? $"{resolvedKey}.{string.Join(".", remainingPath)}"
                            : resolvedKey;
                        _logger.LogDebug("Mapped IGrouping chain access to {Path}", resolvedPath);
                        return Expression.Constant(resolvedPath);
                    }
                    
                    throw new NotSupportedException($"IGrouping property '{propertyPath.FirstOrDefault()}' is not supported. Use group.Key");
                }

                var fp = $"{_alias}.{string.Join(".", propertyPath)}";
                _logger.LogDebug("Mapped nested property access to {Path}", fp);
                return Expression.Constant(fp);
            }
        }

        // Otherwise, evaluate the member access (e.g., local variable)
        try
        {
            var objectMember = Expression.Convert(node, typeof(object));
            var getterLambda = Expression.Lambda<Func<object>>(objectMember);
            var getter = getterLambda.Compile();
            var value = getter();

            var paramRef = AddParameter(value);
            return Expression.Constant(paramRef);
        }
        catch
        {
            throw new NotSupportedException($"Cannot evaluate member expression: {node}");
        }
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
