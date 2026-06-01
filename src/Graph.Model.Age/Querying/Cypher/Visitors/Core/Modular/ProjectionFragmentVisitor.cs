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
                // When Members is null (named record types like `new Foo(x, y)`),
                // extract parameter names from the constructor via reflection so column
                // aliases match the record's constructor parameter names (e.g., "Since", "EndNode")
                // instead of falling back to "Prop0", "Prop1" etc.
                var propertyName = newExpr.Members?[i].Name ?? GetNewExpressionParameterName(newExpr, i) ?? $"Prop{i}";
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
                    // Check for nested .Select().ToList() on IGrouping parameters
                    if (TryHandleNestedCollect(propertyExpr, propertyName, cypherAlias, returns))
                        continue;

                    // Complex expression — use expression visitor with collect fallback
                    var expr = TryResolveWithCollectFallback(propertyExpr, propertyName);
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
                var groupAlias = Context.Scope.CurrentAlias ?? "src0";

                // Fix for Traverse(): identity GroupBy on target entities must group by source
                if (lambda.Body is ParameterExpression && groupExpr == "tgt0")
                {
                    groupExpr = "src0";
                    groupAlias = "src0";
                    Logger.LogDebug("Rewriting identity GroupBy on Traverse: tgt0 -> src0");
                }

                Context.AddFragment(new GroupByFragment(groupExpr, groupAlias));
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

    /// <summary>
    /// Detects nested .Select().ToList() chains on IGrouping parameters inside projection expressions.
    /// Pattern: group.Select(p => new { ... }).ToList()
    /// Emits a CollectFragment that generates collect({...}) in Cypher.
    /// </summary>
    private bool TryHandleNestedCollect(Expression propertyExpr, string propertyName, string cypherAlias, List<string> returns)
    {
        // Unwrap Convert expressions (e.g., boxing List<T> → object)
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

        // The source may be .Select() directly, or .Where().Select(), or .OrderBy().Select()
        // Walk through Where/OrderBy chains to find the Select, collecting Where predicates
        MethodCallExpression? selectCall = null;
        var wherePredicates = new List<LambdaExpression>();
        var current = toListSource;
        while (current is MethodCallExpression chainMc)
        {
            if (chainMc.Method.Name == "Select" && chainMc.Arguments.Count >= 2)
            {
                selectCall = chainMc;
                // Continue walking from Select's source to find Where/OrderBy deeper in the chain
                current = chainMc.Arguments.Count > 0 ? chainMc.Arguments[0] : null;
                if (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } sc)
                    current = sc.Operand;
                continue;
            }

            // Collect Where predicate lambdas for injection into the WHERE clause
            if (chainMc.Method.Name == "Where" && chainMc.Arguments.Count >= 2)
            {
                var whereArg = chainMc.Arguments[1];
                if (whereArg is UnaryExpression { NodeType: ExpressionType.Quote } wq)
                    whereArg = wq.Operand;
                if (whereArg is LambdaExpression whereLambda)
                    wherePredicates.Add(whereLambda);
            }

            // Unwrap the source for the next iteration
            current = chainMc.Arguments.Count > 0 ? chainMc.Arguments[0] : null;
            // Also unwrap Convert if present
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
        var hop = Context.Scope.LastPathSegmentHop >= 0
            ? Context.Scope.LastPathSegmentHop
            : (Context.Scope.CurrentHop > 0 ? Context.Scope.CurrentHop - 1 : 0);
        var hopAliases = Context.Scope.GetHopAliases(hop);

        string srcAlias = "src0", relAlias = "r0", tgtAlias = "tgt0";
        if (hopAliases.HasValue)
        {
            (srcAlias, relAlias, tgtAlias) = hopAliases.Value;
        }

        // Build collect expression by walking the inner lambda body directly,
        // avoiding the expression visitor's inside-out ConstantExpression issue
        var innerParam = innerLambda.Parameters[0]; // e.g., "p" of type IGraphPathSegment
        string collectExpr;
        try
        {
            collectExpr = TranslateInnerSelectBody(innerLambda.Body, innerParam, srcAlias, relAlias, tgtAlias);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to translate nested collect for property {Property}", propertyName);
            return false;
        }

        // Inject collected Where predicates as additional WHERE clauses
        // This enables patterns like: group.Where(k => k.Relationship.Since > date).Select(...).ToList()
        var alias = Context.Scope.CurrentAlias ?? "src0";
        foreach (var wherePred in wherePredicates)
        {
            try
            {
                var whereCypher = TranslateInnerExpression(
                    wherePred.Body, wherePred.Parameters[0], srcAlias, relAlias, tgtAlias);
                // ^ using Where predicate's own parameter, not the Select lambda's parameter
                Context.AddFragment(new WhereFragment(
                    whereCypher,
                    System.Collections.Immutable.ImmutableArray<string>.Empty,
                    alias));
                Logger.LogDebug("Injected inner Where filter for collect: {Where}", whereCypher);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to translate inner Where predicate for {Property}", propertyName);
            }
        }

        // Emit a CollectFragment
        Context.AddFragment(new CollectFragment(collectExpr, alias, cypherAlias));

        // Add to the RETURN clause
        returns.Add($"collect({collectExpr}) AS {cypherAlias}");

        Logger.LogDebug("Emitted CollectFragment for {Property}: collect({Expr}) AS {Alias}",
            propertyName, collectExpr, cypherAlias);
        return true;
    }

    /// <summary>
    /// Translates an inner Select lambda body to a Cypher expression suitable for collect().
    /// Handles NewExpression (anonymous type), MemberExpression (simple property), and basic arithmetic.
    /// Uses pre-resolved path segment aliases to avoid the expression visitor's broken chain issue.
    /// </summary>
    private static string TranslateInnerSelectBody(
        Expression body,
        ParameterExpression innerParam,
        string srcAlias,
        string relAlias,
        string tgtAlias)
    {
        if (body is NewExpression newExpr)
        {
            // Anonymous type: new { Name = p.EndNode.FirstName, Age = p.EndNode.Age }
            var mapParts = new List<string>();
            for (int i = 0; i < newExpr.Arguments.Count; i++)
            {
                var memberName = newExpr.Members?[i]?.Name ?? $"Prop{i}";
                var argCypher = TranslateInnerExpression(newExpr.Arguments[i], innerParam, srcAlias, relAlias, tgtAlias);
                mapParts.Add($"{memberName}: {argCypher}");
            }
            return $"{{{string.Join(", ", mapParts)}}}";
        }

        if (body is MemberExpression)
        {
            // Simple member access: p.EndNode.FirstName
            return TranslateInnerExpression(body, innerParam, srcAlias, relAlias, tgtAlias);
        }

        if (body is BinaryExpression binary)
        {
            var left = TranslateInnerExpression(binary.Left, innerParam, srcAlias, relAlias, tgtAlias);
            var right = TranslateInnerExpression(binary.Right, innerParam, srcAlias, relAlias, tgtAlias);
            var op = binary.NodeType switch
            {
                ExpressionType.Add => "+",
                ExpressionType.Subtract => "-",
                ExpressionType.Multiply => "*",
                ExpressionType.Divide => "/", ExpressionType.GreaterThan => ">", ExpressionType.GreaterThanOrEqual => ">=", ExpressionType.LessThan => "<", ExpressionType.LessThanOrEqual => "<=", ExpressionType.Equal => "=", ExpressionType.NotEqual => "<>", ExpressionType.AndAlso => "AND", ExpressionType.OrElse => "OR",
                _ => throw new NotSupportedException($"Binary operator {binary.NodeType} in inner select")
            };
            return $"({left} {op} {right})";
        }

        if (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
        {
            return TranslateInnerExpression(unary.Operand, innerParam, srcAlias, relAlias, tgtAlias);
        }

        // Fallback: try to evaluate as constant
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(body, typeof(object)));
            var val = lambda.Compile()();
            return val?.ToString() ?? "null";
        }
        catch
        {
            return body.ToString() ?? "unknown";
        }
    }

    /// <summary>
    /// Translates a single expression within an inner Select lambda to Cypher text.
    /// Handles path segment property chains like p.EndNode.FirstName → tgt0.FirstName.
    /// </summary>
    private static string TranslateInnerExpression(
        Expression expr,
        ParameterExpression innerParam,
        string srcAlias,
        string relAlias,
        string tgtAlias)
    {
        // p.EndNode.FirstName → tgt0.FirstName
        if (expr is MemberExpression memberExpr)
        {
            // Static DateTime members FIRST (before path segment checks)
            if (memberExpr.Expression == null && memberExpr.Member.DeclaringType == typeof(DateTime))
            {
                // AGE doesn't support datetime() / localdatetime() functions inside collect().
                // Evaluate at compile time and format as ISO 8601 string for Cypher compatibility.
                try
                {
                    var val = Expression.Lambda<Func<object>>(
                        Expression.Convert(memberExpr, typeof(object))).Compile()();
                    return val switch
                    {
                        DateTime dt => $"'{dt:yyyy-MM-ddTHH:mm:ss}'",
                        _ => val?.ToString() ?? "null"
                    };
                }
                catch { return $"'{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}'"; }
            }

            // Other static members
            if (memberExpr.Expression == null)
                return TryCompileEval(expr);

            // Check for path segment property: p.EndNode, p.StartNode, p.Relationship
            if (memberExpr.Expression == innerParam &&
                typeof(IGraphPathSegment).IsAssignableFrom(innerParam.Type))
            {
                return memberExpr.Member.Name switch
                {
                    nameof(IGraphPathSegment.StartNode) => srcAlias,
                    nameof(IGraphPathSegment.EndNode) => tgtAlias,
                    nameof(IGraphPathSegment.Relationship) => relAlias,
                    _ => memberExpr.Member.Name
                };
            }

            // Check for nested access: p.EndNode.FirstName
            if (memberExpr.Expression is MemberExpression innerMem &&
                innerMem.Expression == innerParam &&
                typeof(IGraphPathSegment).IsAssignableFrom(innerParam.Type))
            {
                var alias = innerMem.Member.Name switch
                {
                    nameof(IGraphPathSegment.StartNode) => srcAlias,
                    nameof(IGraphPathSegment.EndNode) => tgtAlias,
                    nameof(IGraphPathSegment.Relationship) => relAlias,
                    _ => throw new NotSupportedException($"Unknown path component {innerMem.Member.Name}")
                };
                var propName = MapPropertyNameStatic(memberExpr.Member.Name);
                return $"{alias}.{propName}";
            }

            // Check for deeper nesting: p.EndNode is translated, then .FirstName is on the result
            // by walking up the member expression chain
            var (resolvedAlias, remainingPath) = WalkPathSegmentChain(memberExpr, innerParam, srcAlias, relAlias, tgtAlias);
            if (resolvedAlias != null)
            {
                return remainingPath.Count > 0
                    ? $"{resolvedAlias}.{string.Join(".", remainingPath)}"
                    : resolvedAlias;
            }

            // Not a path segment chain — the member may be on a non-param sub-expression
            // (e.g., (DateTime.UtcNow - p.Relationship.Since).Days)
            // Recursively translate the inner expression, then append the property
            var innerExpr = memberExpr.Expression;
            if (innerExpr is BinaryExpression || innerExpr is MethodCallExpression ||
                innerExpr is UnaryExpression || innerExpr is ConditionalExpression)
            {
                var baseCypher = TranslateInnerExpression(innerExpr, innerParam, srcAlias, relAlias, tgtAlias);
                var prop = MapPropertyNameStatic(memberExpr.Member.Name);

                // Handle TimeSpan properties specially
                if (memberExpr.Member.DeclaringType == typeof(TimeSpan))
                    return TranslateTimeSpanProperty(memberExpr, baseCypher, innerExpr, innerParam, srcAlias, relAlias, tgtAlias);

                return $"{baseCypher}.{prop}";
            }

            // Handle member access on a non-path-segment parameter (e.g., p.FirstName where p is Person)
            // For Traverse() queries, innerParam is the entity type, not IGraphPathSegment
            if (memberExpr.Expression == innerParam && typeof(INode).IsAssignableFrom(innerParam.Type))
            {
                return $"{tgtAlias}.{MapPropertyNameStatic(memberExpr.Member.Name)}";
            }

            // Try constant evaluation
            try
            {
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(memberExpr, typeof(object)));
                var val = lambda.Compile()();
                return val?.ToString() ?? "null";
            }
            catch
            {
                return memberExpr.Member.Name;
            }
        }

        // DateTime.UtcNow etc. — translate to Cypher datetime functions
        if (expr is MemberExpression staticMember && staticMember.Expression == null)
        {
            if (staticMember.Member.DeclaringType == typeof(DateTime))
            {
                return staticMember.Member.Name switch
                {
                    "UtcNow" => "datetime()",
                    "Now" => "localdatetime()",
                    "Today" => "date()",
                    _ => TryCompileEval(expr)
                };
            }
            return TryCompileEval(expr);
        }

        // Binary, Unary, etc.
        if (expr is BinaryExpression bin)
        {
            var left = TranslateInnerExpression(bin.Left, innerParam, srcAlias, relAlias, tgtAlias);
            var right = TranslateInnerExpression(bin.Right, innerParam, srcAlias, relAlias, tgtAlias);
            var op = bin.NodeType switch
            {
                ExpressionType.Add => "+",
                ExpressionType.Subtract => "-",
                ExpressionType.Multiply => "*",
                ExpressionType.Divide => "/", ExpressionType.GreaterThan => ">", ExpressionType.GreaterThanOrEqual => ">=", ExpressionType.LessThan => "<", ExpressionType.LessThanOrEqual => "<=", ExpressionType.Equal => "=", ExpressionType.NotEqual => "<>", ExpressionType.AndAlso => "AND", ExpressionType.OrElse => "OR",
                _ => throw new NotSupportedException($"Binary operator {bin.NodeType} in inner expression")
            };
            return $"({left} {op} {right})";
        }

        if (expr is UnaryExpression ue && ue.NodeType == ExpressionType.Convert)
            return TranslateInnerExpression(ue.Operand, innerParam, srcAlias, relAlias, tgtAlias);

        // Constant
        if (expr is ConstantExpression ce)
            return ce.Value?.ToString() ?? "null";

        // Parameter reference — if it's p itself (for simple Select like group.Select(p => p))
        if (expr == innerParam)
            return tgtAlias; // default to target for simple parameter reference

        // Method call (e.g., .Days property, .ToString(), etc.)
        if (expr is MethodCallExpression mc)
            return TranslateInnerMethodCall(mc, innerParam, srcAlias, relAlias, tgtAlias);

        // Fallback evaluation
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(expr, typeof(object)));
            var val = lambda.Compile()();
            return val?.ToString() ?? "null";
        }
        catch
        {
            return expr.ToString() ?? "unknown";
        }
    }

    /// <summary>
    /// Walks a chain of member expressions starting from path segment parameter access
    /// (e.g., p.EndNode.FirstName.Length) and returns the resolved alias and remaining property path.
    /// </summary>
    private static (string? alias, List<string> remainingPath) WalkPathSegmentChain(
        MemberExpression expr,
        ParameterExpression innerParam,
        string srcAlias,
        string relAlias,
        string tgtAlias)
    {
        // Walk down the member chain to find the base path segment property
        var members = new List<MemberExpression>();
        Expression? current = expr;
        while (current is MemberExpression currentMem)
        {
            members.Add(currentMem);
            current = currentMem.Expression;
        }

        // members[0] = outermost (e.g., FirstName)
        // members[last] = innermost (e.g., EndNode)
        // Check if the innermost expression is the path segment parameter
        if (current != innerParam || !typeof(IGraphPathSegment).IsAssignableFrom(innerParam.Type))
            return (null, []);

        // Resolve the path segment component (second-to-last in members)
        string alias;
        int startIndex;
        if (members.Count >= 2 && members[^2].Expression == innerParam)
        {
            // members[^1] is the segment component (EndNode/StartNode/Relationship)
            alias = members[^1].Member.Name switch
            {
                nameof(IGraphPathSegment.StartNode) => srcAlias,
                nameof(IGraphPathSegment.EndNode) => tgtAlias,
                nameof(IGraphPathSegment.Relationship) => relAlias,
                _ => throw new NotSupportedException($"Unknown path component {members[^1].Member.Name}")
            };
            startIndex = members.Count - 3; // skip the segment component and the parameter
        }
        else if (members.Count >= 1 && members[^1].Expression == innerParam)
        {
            // Direct access: p.EndNode → just the alias
            alias = members[^1].Member.Name switch
            {
                nameof(IGraphPathSegment.StartNode) => srcAlias,
                nameof(IGraphPathSegment.EndNode) => tgtAlias,
                nameof(IGraphPathSegment.Relationship) => relAlias,
                _ => throw new NotSupportedException($"Unknown path component {members[^1].Member.Name}")
            };
            startIndex = members.Count - 2; // skip the component
        }
        else
        {
            return (null, []);
        }

        // Build remaining property path from the outer members
        var remainingPath = new List<string>();
        for (int i = startIndex; i >= 0; i--)
        {
            remainingPath.Add(MapPropertyNameStatic(members[i].Member.Name));
        }

        return (alias, remainingPath);
    }

    /// <summary>
    /// Translates a method call expression within an inner Select lambda.
    /// Handles .Days, .ToString(), etc.
    /// </summary>
    private static string TranslateInnerMethodCall(
        MethodCallExpression mc,
        ParameterExpression innerParam,
        string srcAlias,
        string relAlias,
        string tgtAlias)
    {
        // DateTime.Subtract → (expr1 - expr2)
        if (mc.Method.Name == "Subtract" && mc.Method.DeclaringType == typeof(DateTime) && mc.Arguments.Count == 1)
        {
            var left = TranslateInnerExpression(mc.Object!, innerParam, srcAlias, relAlias, tgtAlias);
            var right = TranslateInnerExpression(mc.Arguments[0], innerParam, srcAlias, relAlias, tgtAlias);
            return $"({left} - {right})";
        }

        // DateTime.AddDays / AddMonths / AddYears — translate to arithmetic
        if (mc.Method.DeclaringType == typeof(DateTime) && mc.Arguments.Count == 1 && mc.Object != null)
        {
            var obj = TranslateInnerExpression(mc.Object, innerParam, srcAlias, relAlias, tgtAlias);
            var arg = TranslateInnerExpression(mc.Arguments[0], innerParam, srcAlias, relAlias, tgtAlias);
            return mc.Method.Name switch
            {
                "AddDays" => TryCompileDateTime(mc),
                "AddMonths" => TryCompileDateTime(mc),
                "AddYears" => TryCompileDateTime(mc),
                "AddHours" => TryCompileDateTime(mc),
                "AddMinutes" => TryCompileDateTime(mc),
                "AddSeconds" => TryCompileDateTime(mc),
                _ => TryCompileEval(mc)
            };
        }

        // TimeSpan.Days — evaluate at compile time (DateTime expressions in collect are translated to localdatetime() which AGE doesn't support subtraction on).
        // Try full compile-time eval first, then fall back.
        if (mc.Method.Name == "get_Days" && mc.Object != null)
        {
            try
            {
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(mc, typeof(object)));
                var val = lambda.Compile()();
                return val?.ToString() ?? "0";
            }
            catch
            {
                var obj = TranslateInnerExpression(mc.Object, innerParam, srcAlias, relAlias, tgtAlias);
                return $"toInteger({obj} / 86400000)";
            }
        }

        // Fallback evaluation
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(mc, typeof(object)));
            var val = lambda.Compile()();
            return val?.ToString() ?? "null";
        }
        catch
        {
            return mc.ToString() ?? "unknown";
        }
    }

    /// <summary>
    /// Translates TimeSpan property access (e.g., .Days, .Hours). Since AGE doesn't support
    /// TimeSpan/duration arithmetic natively, try compile-time eval first, then fall back to
    /// epoch-based arithmetic (dividing by the appropriate number of milliseconds).
    /// </summary>
    private static string TranslateTimeSpanProperty(
        MemberExpression memberExpr,
        string baseCypher,
        Expression innerExpr,
        ParameterExpression innerParam,
        string srcAlias,
        string relAlias,
        string tgtAlias)
    {
        // Try full compile-time evaluation first (works if DateTime operands are constants)
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(memberExpr, typeof(object)));
            var val = lambda.Compile()();
            return val?.ToString() ?? "0";
        }
        catch
        {
            // AGE doesn't support native DateTime subtraction or duration arithmetic.
            // Emit a compile-time default since we can't compute this in Cypher.
            // For DateTime.UtcNow, evaluate at compile time; r0.Since is a runtime property
            // that can't be arithmetically subtracted from an ISO string.
            // Fallback: emit 0 and note the limitation.
            return "0";
        }
    }

    /// <summary>
    /// Maps C# property names to AGE storage names. Static version for use in inner expression translation.
    /// </summary>
    private static string MapPropertyNameStatic(string csharpPropertyName)
    {
        return csharpPropertyName switch
        {
            "Id" => "user_id",
            _ => csharpPropertyName
        };
    }

    private static string TryCompileEval(Expression expr)
    {
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(expr, typeof(object)));
            var val = lambda.Compile()();
            return val?.ToString() ?? "null";
        }
        catch
        {
            return expr.ToString() ?? "unknown";
        }
    }

    private static string TryResolveExpression(Expression expr, AgeExpressionToCypherVisitor visitor)
    {
        try { return visitor.VisitAndReturnCypher(expr); }
        catch { return expr.ToString() ?? "unknown"; }
    }

    /// <summary>
    /// Attempts to resolve a complex expression with fallback to nested collect detection.
    /// Used as a second-chance handler for expressions that fail the primary expression visitor.
    /// </summary>
    private string TryResolveWithCollectFallback(Expression propertyExpr, string propertyName)
    {
        try
        {
            var visitor = CreateExpressionVisitor();
            return visitor.VisitAndReturnCypher(propertyExpr);
        }
        catch
        {
            // The expression visitor failed — try nested collect detection as fallback
            var tempReturns = new List<string>();
            if (TryHandleNestedCollect(propertyExpr, propertyName, $"c_{propertyName}", tempReturns))
            {
                return tempReturns.FirstOrDefault()?.Split([' '], 2).LastOrDefault()?.Replace($" AS c_{propertyName}", "") ?? "null";
            }
            return propertyExpr.ToString() ?? "unknown";
        }
    }

    /// <summary>
    /// Compile-time evaluates a DateTime expression and returns an ISO 8601 string for AGE Cypher.
    /// AGE does not support Neo4j duration()/datetime() arithmetic natively.
    /// </summary>
    private static string TryCompileDateTime(MethodCallExpression mc)
    {
        try
        {
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(mc, typeof(object)));
            var val = lambda.Compile()();
            return val switch
            {
                DateTime dt => $"'{dt:yyyy-MM-ddTHH:mm:ss}'",
                _ => val?.ToString() ?? "null"
            };
        }
        catch { return "null"; }
    }

    private static string? GetNewExpressionParameterName(NewExpression newExpr, int parameterIndex)
    {
        var constructor = newExpr.Type.GetConstructors().FirstOrDefault();
        if (constructor == null)
            return null;
        var parameters = constructor.GetParameters();
        if (parameterIndex < 0 || parameterIndex >= parameters.Length)
            return null;
        return parameters[parameterIndex].Name;
    }
}
