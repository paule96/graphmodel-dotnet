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

namespace Cvoya.Graph.Model.Age.Querying.Cypher.Execution;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cvoya.Graph.Model.Age.Core;
using Cvoya.Graph.Model.Age.Core.Entities;
using Cvoya.Graph.Model.Age.Querying.Cypher.Visitors;
using Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Npgsql.Age;
using Npgsql.Age.Types;

/// <summary>
/// Executes LINQ queries against AGE by converting them to Cypher.
/// </summary>
internal sealed class AgeCypherEngine
{
    private readonly AgeGraphContext _graphContext;
    private readonly ILogger<AgeCypherEngine> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly EntityFactory _entityFactory;
    private readonly AgeEntityMapper _entityMapper;
    
    // New shared architecture components
    private readonly AgeResultProcessor _ageResultProcessor;
    private readonly ResultMaterializer<AgeValueConverter> _sharedMaterializer;
    
    public AgeCypherEngine(AgeGraphContext graphContext, ILoggerFactory loggerFactory)
    {
        _graphContext = graphContext ?? throw new ArgumentNullException(nameof(graphContext));
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<AgeCypherEngine>() ?? NullLogger<AgeCypherEngine>.Instance;
        _entityFactory = new EntityFactory(loggerFactory);
        _entityMapper = new AgeEntityMapper(_entityFactory, loggerFactory);
        
        // Initialize shared architecture components
        _ageResultProcessor = new AgeResultProcessor(_entityFactory, _entityMapper, loggerFactory);
        var ageValueConverter = new AgeValueConverter();
        _sharedMaterializer = new ResultMaterializer<AgeValueConverter>(_entityFactory, ageValueConverter, loggerFactory);
    }

    public async Task<T?> ExecuteAsync<T>(
        Expression expression,
        AgeGraphTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Executing query for type {Type}", typeof(T).Name);

            // Detect if this is an aggregation operation
            var aggregationOp = DetectAggregationType(expression);
            var isAggregation = aggregationOp != null;

            // Handle ToDictionary specially — execute the underlying query then build the dictionary client-side
            if (aggregationOp == "ToDictionary")
            {
                return await ExecuteToDictionaryAsync<T>(expression, transaction, cancellationToken);
            }
            
            // Extract the element type from the expression
            // For aggregation operations, use the result type T instead of the source element type
            var elementType = isAggregation ? typeof(T) : ExtractElementType(typeof(T), expression);

            // Detect if we have a projection
            var (hasProjection, projectionExpression, sourceElementType) = DetectProjection(expression);

            // Build and execute the Cypher query
            // For projections, use the source element type (e.g., relationship type) not the projected result type
            var cypherElementType = hasProjection ? sourceElementType : elementType;
            var (cypher, parameters) = BuildCypherQuery(cypherElementType, expression);

            _logger.LogDebug("Generated Cypher: {Cypher}", cypher);
            // For projections to scalar types, use the projected result type for materialization
            // (the query returns scalar values, not full entities). For entity projections,
            // use the source element type.
            // For aggregations, always use typeof(T) since the Cypher returns scalar values
            // regardless of intermediate projections (e.g., count(*), sum(age)).
            var projectedElementType = hasProjection && !isAggregation
                ? ExtractProjectedResultType(typeof(T), projectionExpression)
                : elementType;
            var materializedType = isAggregation ? typeof(T) : (hasProjection ? projectedElementType : elementType);
            var result = await ExecuteQueryWithSharedArchitecture<T>(
                cypher,
                parameters,
                materializedType,
                transaction,
                cancellationToken,
                hasProjection ? projectionExpression : null,
                hasProjection ? elementType : null,
                aggregationOp);

            if (aggregationOp == "Single" && result is System.Collections.IEnumerable enumerable)
            {
                var count = 0;
                foreach (var _ in enumerable)
                {
                    count++;
                    if (count > 1)
                    {
                        throw new InvalidOperationException("Sequence contains more than one element");
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute query for type {Type}", typeof(T).Name);
            throw;
        }
    }

    private (string cypher, Dictionary<string, object?> parameters) BuildCypherQuery(Type elementType, Expression expression)
    {
        // Use the visitor pattern for sophisticated LINQ query translation
        var context = new CypherQueryContext(elementType, _loggerFactory, _graphContext.SchemaRegistry);
        var visitor = new AgeCypherQueryVisitor(context);
        
        try
        {
            // Let the visitor handle the expression tree traversal
            visitor.Visit(expression);
            
            // Finalize the query - adds default projections if needed
            // This ensures path segment queries work correctly even without explicit ToList/ToArray
            visitor.FinalizeQuery(elementType);
            
            // Get the built query and parameters
            var query = context.GetQuery();
            var parameters = context.GetParameters().ToDictionary(kv => kv.Key, kv => kv.Value);
            
            _logger?.LogInformation($"Generated Cypher query via visitor:\n{query}");
            return (query, parameters);
        }
        catch (NotSupportedException ex)
        {
            _logger?.LogError(ex, "Visitor-based translation failed for element type {ElementType}", elementType.Name);
            throw;
        }
    }

    private static Type ExtractProjectedResultType(Type resultType, LambdaExpression? projection)
    {
        if (projection?.Body is MemberExpression member)
            return member.Type;

        // For List<T> result types, T is the element type
        if (resultType.IsGenericType)
        {
            var args = resultType.GetGenericArguments();
            if (args.Length == 1) return args[0];
        }

        return resultType;
    }

    private string? DetectAggregationType(Expression expression)
    {
        Expression current = expression;
        while (current is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;
            
            // Check for Count/LongCount markers (both sync and async)
            if (methodName == "Count" || methodName == "CountAsync" || methodName == "CountAsyncMarker" || 
                methodName == "LongCount" || methodName == "LongCountAsync" || methodName == "LongCountAsyncMarker")
            {
                return "Count";
            }
            
            // Check for Any markers (both sync and async)
            if (methodName == "Any" || methodName == "AnyAsync" || methodName == "AnyAsyncMarker")
            {
                return "Any";
            }
            
            // Check for All markers
            if (methodName == "All" || methodName == "AllAsync" || methodName == "AllAsyncMarker")
            {
                return "All";
            }
            
            // Check for Sum markers
            if (methodName == "Sum" || methodName == "SumAsync" || methodName == "SumAsyncMarker")
            {
                return "Sum";
            }
            
            // Check for Average markers
            if (methodName == "Average" || methodName == "AverageAsync" || methodName == "AverageAsyncMarker")
            {
                return "Average";
            }
            
            // Check for Min markers
            if (methodName == "Min" || methodName == "MinAsync" || methodName == "MinAsyncMarker")
            {
                return "Min";
            }
            
            // Check for Max markers
            if (methodName == "Max" || methodName == "MaxAsync" || methodName == "MaxAsyncMarker")
            {
                return "Max";
            }
            
            // Check for First/Last/Single markers
            if (methodName == "First" || methodName == "FirstAsync" || methodName == "FirstAsyncMarker" ||
                methodName == "FirstOrDefault" || methodName == "FirstOrDefaultAsync" || methodName == "FirstOrDefaultAsyncMarker")
            {
                return "First";
            }
            
            if (methodName == "Last" || methodName == "LastAsync" || methodName == "LastAsyncMarker" ||
                methodName == "LastOrDefault" || methodName == "LastOrDefaultAsync" || methodName == "LastOrDefaultAsyncMarker")
            {
                return "Last";
            }
            
            if (methodName == "Single" || methodName == "SingleAsync" || methodName == "SingleAsyncMarker" ||
                methodName == "SingleOrDefault" || methodName == "SingleOrDefaultAsync" || methodName == "SingleOrDefaultAsyncMarker")
            {
                return "Single";
            }
            
            // Check for ToDictionary markers
            if (methodName == "ToDictionaryAsync" || methodName == "ToDictionaryAsyncMarker")
            {
                return "ToDictionary";
            }
            
            if (methodCall.Arguments.Count > 0)
            {
                current = methodCall.Arguments[0];
            }
            else
            {
                break;
            }
        }
        
        return null;
    }

    private async Task<T?> ExecuteToDictionaryAsync<T>(Expression expression, AgeGraphTransaction? transaction, CancellationToken cancellationToken)
    {
        // The expression is: ToDictionaryAsyncMarker(source.Expression, keySelector)
        // We need to:
        // 1. Extract the source expression and keySelector
        // 2. Execute the source as List<TSource>
        // 3. Build Dictionary<TKey, TSource> using the keySelector
        if (expression is not MethodCallExpression toDictCall || toDictCall.Arguments.Count < 2)
            throw new InvalidOperationException("Expected ToDictionaryAsyncMarker call with source and keySelector");

        var sourceExpression = toDictCall.Arguments[0];
        var keySelectorArg = toDictCall.Arguments[1];

        // Extract the keySelector lambda
        LambdaExpression keySelectorLambda;
        if (keySelectorArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            keySelectorLambda = (LambdaExpression)quote.Operand;
        else
            keySelectorLambda = (LambdaExpression)keySelectorArg;

        // Extract source element type from the queryable
        var sourceElementType = ExtractElementTypeFromExpression(sourceExpression) ?? typeof(object);

        // Execute the underlying query as List<TSourceElement>
        var listMethod = GetType().GetMethod(nameof(ExecuteAsListAsync),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (listMethod == null)
            throw new InvalidOperationException("Could not find ExecuteAsListAsync method");

        var genericListMethod = listMethod.MakeGenericMethod(sourceElementType);
        var listTask = (Task)genericListMethod.Invoke(this, [sourceExpression, transaction, cancellationToken])!;
        await listTask.ConfigureAwait(false);

        // Get the result list via reflection
        var resultProperty = listTask.GetType().GetProperty("Result");
        var sourceList = (System.Collections.IEnumerable)resultProperty!.GetValue(listTask)!;

        // Build dictionary from the list using the compiled key selector
        var keySelectorFunc = keySelectorLambda.Compile();
        var dictType = typeof(Dictionary<,>).MakeGenericType(keySelectorLambda.ReturnType, sourceElementType);
        var dict = (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;

        foreach (var item in sourceList)
        {
            var key = keySelectorFunc.DynamicInvoke(item);
            dict.Add(key!, item);
        }

        return (T?)dict;
    }

    private async Task<List<TElement>> ExecuteAsListAsync<TElement>(
        Expression sourceExpression, AgeGraphTransaction? transaction, CancellationToken cancellationToken)
    {
        // Build a fresh query from the source expression
        var (cypher, parameters) = BuildCypherQuery(typeof(TElement), sourceExpression);

        _logger.LogDebug("ToDictionary source Cypher: {Cypher}", cypher);

        // Execute and materialize as list
        var entityInfos = await ExecuteRawQueryAsync(
            cypher, parameters, typeof(TElement), transaction, cancellationToken);

        var materialized = await _sharedMaterializer.MaterializeAsync<List<TElement>>(entityInfos, cancellationToken);
        return materialized ?? [];
    }

    private async Task<List<EntityInfo>> ExecuteRawQueryAsync(
        string cypher, Dictionary<string, object?> parameters, Type elementType,
        AgeGraphTransaction? transaction, CancellationToken cancellationToken)
    {
        var command = _graphContext.Connection.CreateCypherCommand(_graphContext.GraphName, cypher, parameters);
        command.Transaction = transaction?.Transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await _ageResultProcessor.ProcessAsync(reader, elementType, cancellationToken);
    }

    private (bool hasProjection, LambdaExpression? projectionExpression, Type sourceElementType) DetectProjection(Expression expression)
    {
        Expression current = expression;
        while (current is MethodCallExpression methodCall)
        {
            if (methodCall.Method.Name == "Select" && methodCall.Arguments.Count >= 2)
            {
                // Found a Select operation
                var lambdaArg = methodCall.Arguments[1];
                
                if (lambdaArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                {
                    lambdaArg = quote.Operand;
                }
                
                if (lambdaArg is LambdaExpression lambda)
                {
                    // Get the source element type from the first argument
                    var sourceType = methodCall.Arguments[0].Type;
                    if (sourceType.IsGenericType)
                    {
                        var genericArgs = sourceType.GetGenericArguments();
                        if (genericArgs.Length > 0)
                        {
                            return (true, lambda, genericArgs[0]);
                        }
                    }
                }
            }
            
            if (methodCall.Arguments.Count > 0)
            {
                current = methodCall.Arguments[0];
            }
            else
            {
                break;
            }
        }
        
        return (false, null, typeof(object));
    }
    

    private async Task<T?> ExecuteQueryWithSharedArchitecture<T>(
        string cypher,
        Dictionary<string, object?> parameters,
        Type elementType,
        AgeGraphTransaction? transaction,
        CancellationToken cancellationToken,
        LambdaExpression? projectionExpression = null,
        Type? projectionResultType = null,
        string? aggregationType = null)
    {
        // Build the command - use custom column definitions for projections or path segments
        NpgsqlCommand command;
        var isPathSegmentType = elementType.IsGenericType &&
            elementType.GetGenericTypeDefinition().Name.Contains("GraphPathSegment", StringComparison.Ordinal);

        if (projectionExpression != null || isPathSegmentType)
        {
            string columnDefs;
            if (projectionExpression != null)
            {
                columnDefs = BuildColumnDefinitions(projectionExpression);
            }
            else
            {
                // For path segment queries, build column definitions matching the 3-aliases
                // returned by DetermineReturnClause for MatchSegmentFragment.
                // The aliases are: srcAlias, relAlias, tgtAlias (e.g. src0, r0, tgt0).
                columnDefs = BuildPathSegmentColumnDefinitions();
            }
            command = CreateCypherCommandWithColumns(_graphContext.Connection, _graphContext.GraphName, cypher, parameters, columnDefs);
        }
        else
        {
            command = _graphContext.Connection.CreateCypherCommand(_graphContext.GraphName, cypher, parameters);
        }

        command.Transaction = transaction?.Transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // For scalar results (not INode/IRelationship), read native types directly.
        // Projections (anonymous types, member projections) need the EntityInfo pipeline
        // so the ResultMaterializer can construct proper objects from multi-column returns.
        // Path segments also need the EntityInfo pipeline since they're composed of vertices/edges.
        var isProjection = projectionExpression != null || IsAnonymousType(elementType);
        if (!typeof(INode).IsAssignableFrom(elementType) && !typeof(IRelationship).IsAssignableFrom(elementType)
            && !isPathSegmentType && !isProjection)
        {
            return await ScalarResultMaterializer.MaterializeAsync<T>(reader, elementType, cancellationToken, aggregationType).ConfigureAwait(false);
        }

        // Use AgeResultProcessor to convert raw results to EntityInfo structures
        var entityInfos = await _ageResultProcessor.ProcessAsync(
            reader, elementType, cancellationToken, projectionExpression, projectionResultType, aggregationType);

        // Use shared ResultMaterializer to convert EntityInfo to final objects
        return await _sharedMaterializer.MaterializeAsync<T>(entityInfos, cancellationToken);
    }

    // MaterializeScalarResultAsync moved to ScalarResultMaterializer.MaterializeAsync

    private static bool IsAnonymousType(Type type) =>
        type.IsGenericType
        && type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0
        && type.Name.StartsWith("<>") && type.Name.Contains("AnonymousType");

    private static Type ExtractElementType(Type resultType, Expression expression)
    {
        // If the result type is a collection, extract the element type
        if (resultType.IsGenericType)
        {
            var genericArgs = resultType.GetGenericArguments();
            if (genericArgs.Length == 1)
            {
                return genericArgs[0];
            }
        }

        // Try to extract from the expression tree
        return ExtractElementTypeFromExpression(expression) ?? resultType;
    }

    private static Type? ExtractElementTypeFromExpression(Expression expression)
    {
        if (expression is ConstantExpression constant)
        {
            var queryableType = constant.Type;
            if (queryableType.IsGenericType)
            {
                var interfaces = queryableType.GetInterfaces();
                foreach (var iface in interfaces)
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQueryable<>))
                    {
                        return iface.GetGenericArguments()[0];
                    }
                }
            }
        }

        if (expression is MethodCallExpression methodCall && methodCall.Arguments.Count > 0)
        {
            return ExtractElementTypeFromExpression(methodCall.Arguments[0]);
        }

        return null;
    }

    private string BuildColumnDefinitions(LambdaExpression projectionExpression)
    {
        var body = projectionExpression.Body;
        
        // Handle simple property projection: Select(p => p.FirstName)
        if (body is MemberExpression memberExpr)
        {
            var columnName = memberExpr.Member.Name;
            // Use double quotes for PostgreSQL identifier escaping
            return $"(\"{columnName}\" agtype)";
        }
        
        // Handle anonymous type projection: Select(p => new { p.FirstName, p.LastName })
        if (body is NewExpression newExpr)
        {
            var columns = new List<string>();
            
            for (int i = 0; i < newExpr.Arguments.Count; i++)
            {
                var member = newExpr.Members?[i];
                // When Members is null (named record types like `new Foo(x, y)`),
                // extract parameter names from the constructor via reflection so column
                // aliases match the record's parameter names (e.g., "Since", "EndNode")
                // instead of falling back to "field0", "field1" etc.
                var columnName = member?.Name ?? GetNewExpressionParameterName(newExpr, i) ?? $"field{i}";
                var memberType = member switch
                {
                    PropertyInfo pi => pi.PropertyType,
                    FieldInfo fi => fi.FieldType,
                    _ => newExpr.Arguments[i].Type
                };
                
                // Apache AGE doesn't support proper identifier escaping in Cypher
                // Use c_ prefix in Cypher RETURN, then map to actual property name in SQL column definition
                var cypherAlias = $"c_{columnName}";
                
                if (IsPathSegmentType(memberType))
                {
                    // Path segments are expanded into three columns: source node, relationship, target node
                    columns.Add($"\"{cypherAlias}_src\" agtype");
                    columns.Add($"\"{cypherAlias}_r\" agtype");
                    columns.Add($"\"{cypherAlias}_tgt\" agtype");
                }
                else
                {
                    // Use double quotes for PostgreSQL identifier escaping in SQL portion
                    columns.Add($"\"{cypherAlias}\" agtype");
                }
            }
            
            return $"({string.Join(", ", columns)})";
        }
        
        // Default fallback
        return "(result agtype)";
    }

    private static bool IsPathSegmentType(Type type)
        => ExpressionTranslationHelper.IsPathSegmentType(type);

    private NpgsqlCommand CreateCypherCommandWithColumns(
        NpgsqlConnection connection,
        string graphName,
        string cypher,
        Dictionary<string, object?> parameters,
        string columnDefinitions)
    {
        // Build the SQL query with explicit column definitions.
        // In Konnektr 1.x, CypherHelpers.EscapeCypher() was called inside CreateCypherCommand
        // to escape backslashes. In Konnektr 2.x, EscapeCypher was removed, so the visitor
        // code now generates Cypher with properly escaped backslashes (e.g., \\m for regex
        // word boundary anchors). Since $$...$$ dollar-quoted strings preserve backslashes
        // literally, no additional escaping is needed here.
        
        // Serialize parameters to JSON
        var parametersJson = System.Text.Json.JsonSerializer.Serialize(parameters);
        var agtypeParams = new Agtype(parametersJson);
        
        // Build the full SQL query
        var sql = $"SELECT * FROM ag_catalog.cypher('{graphName}', $$ {cypher} $$, $1) as {columnDefinitions};";
        
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = agtypeParams, DataTypeName = "ag_catalog.agtype" });
        
        return command;
    }

    private static string BuildPathSegmentColumnDefinitions()
    {
        // For path segment queries with no projection, the Cypher RETURN clause returns
        // the 3 created aliases from the MatchSegmentFragment (src0, r0, tgt0).
        // Build matching SQL column definitions.
        // In a chained pattern: hop 0 has (src0, r0, tgt0), hop 1 (tgt0, r1, tgt1), etc.
        // For single-hop path segments (the common case), aliases are src0, r0, tgt0.
        return "(src0 agtype, r0 agtype, tgt0 agtype)";
    }

    /// <summary>
    /// When a NewExpression's Members array is null (happens for named record types like
    /// <c>new PathSegmentProjection(since, endNode)</c>), extracts the constructor parameter
    /// name at the given index so column aliases match the record's parameter names.
    /// </summary>
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
