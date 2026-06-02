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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using Cvoya.Graph.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Detects and handles closure-captured IEnumerable&lt;IRelationship&gt;.Count(lambda) expressions,
/// translating them to Cypher size() patterns.
/// </summary>
internal sealed class ClosureCaptureHandler
{
    private readonly ILogger _logger;
    private readonly string _sourceAlias;

    public ClosureCaptureHandler(ILogger logger, string sourceAlias)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sourceAlias = sourceAlias;
    }

    /// <summary>
    /// Tries to detect a closure-captured IEnumerable&lt;IRelationship&gt;.Count(lambda) expression
    /// and determines if it can be translated to a Cypher size() pattern.
    /// Returns true if the expression matches the expected pattern.
    /// </summary>
    public bool TryHandleClosureCountOnRelationship(MethodCallExpression node)
    {
        Expression? sourceExpr;
        Expression? predicateExpr;

        if (node.Object != null && node.Arguments.Count == 1)
        {
            sourceExpr = node.Object;
            predicateExpr = node.Arguments[0];
        }
        else if (node.Object == null && node.Arguments.Count == 2)
        {
            sourceExpr = node.Arguments[0];
            predicateExpr = node.Arguments[1];
        }
        else
        {
            return false;
        }

        if (sourceExpr is not MemberExpression memberExpr)
            return false;

        object? capturedValue;
        try
        {
            var capturedLambda = Expression.Lambda<Func<object>>(Expression.Convert(memberExpr, typeof(object)));
            capturedValue = capturedLambda.Compile()();
        }
        catch
        {
            return false;
        }

        if (capturedValue is not IEnumerable enumerable)
            return false;

        var elementType = GetEnumerableElementType(capturedValue.GetType());
        if (elementType == null || !typeof(IRelationship).IsAssignableFrom(elementType))
            return false;

        var relationshipLabel = GetRelationshipLabel(enumerable, elementType);
        if (relationshipLabel == null)
            return false;

        var lambdaArg = predicateExpr;
        if (lambdaArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            lambdaArg = quote.Operand;
        if (lambdaArg is not LambdaExpression lambda)
            return false;

        return IsCountByNodeIdPredicate(lambda.Body);
    }

    /// <summary>
    /// Translates a closure-captured IEnumerable&lt;IRelationship&gt;.Count(lambda) expression
    /// to a Cypher size() pattern.
    /// </summary>
    public string HandleClosureCountOnRelationship(MethodCallExpression node)
    {
        Expression sourceExpr;
        Expression predicateExpr;
        if (node.Object != null && node.Arguments.Count == 1)
        {
            sourceExpr = node.Object;
            predicateExpr = node.Arguments[0];
        }
        else if (node.Object == null && node.Arguments.Count == 2)
        {
            sourceExpr = node.Arguments[0];
            predicateExpr = node.Arguments[1];
        }
        else
        {
            throw new NotSupportedException("Expected Count with 1 or 2 arguments on a captured collection");
        }

        var captureExpr = (MemberExpression)sourceExpr;
        var evalLambda = Expression.Lambda<Func<object>>(Expression.Convert(captureExpr, typeof(object)));
        var capturedValue = evalLambda.Compile()();
        var enumerable = (IEnumerable)capturedValue;
        var elementType = GetEnumerableElementType(capturedValue.GetType())!;
        var relationshipLabel = GetRelationshipLabel(enumerable, elementType)!;

        var lambdaArg = predicateExpr;
        if (lambdaArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            lambdaArg = quote.Operand;
        var lambda = (LambdaExpression)lambdaArg;

        var direction = DetectRelationshipDirection(lambda.Body);

        string cypherPattern;
        if (direction == RelationshipDirection.Outgoing)
            cypherPattern = $"({_sourceAlias})-[:{relationshipLabel}]->()";
        else if (direction == RelationshipDirection.Incoming)
            cypherPattern = $"({_sourceAlias})<-[:{relationshipLabel}]-()";
        else
            cypherPattern = $"({_sourceAlias})-[:{relationshipLabel}]-()";

        _logger.LogDebug("Translated closure .Count() to Cypher size() pattern: {Pattern}", cypherPattern);
        return $"size({cypherPattern})";
    }

    private enum RelationshipDirection { Outgoing, Incoming, Both }

    private static RelationshipDirection DetectRelationshipDirection(Expression predicateBody)
    {
        if (predicateBody is BinaryExpression binary && binary.NodeType == ExpressionType.OrElse)
            return RelationshipDirection.Both;

        if (predicateBody is not BinaryExpression eq || eq.NodeType != ExpressionType.Equal)
            return RelationshipDirection.Both;

        var isStartNodeId = IsMemberAccess(eq.Left, "StartNodeId") || IsMemberAccess(eq.Right, "StartNodeId");
        var isEndNodeId = IsMemberAccess(eq.Left, "EndNodeId") || IsMemberAccess(eq.Right, "EndNodeId");

        if (isStartNodeId) return RelationshipDirection.Outgoing;
        if (isEndNodeId) return RelationshipDirection.Incoming;
        return RelationshipDirection.Both;
    }

    private static bool IsMemberAccess(Expression expr, string memberName)
    {
        if (expr is MemberExpression mem)
            return mem.Member.Name == memberName;
        if (expr is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            return IsMemberAccess(unary.Operand, memberName);
        return false;
    }

    private static bool IsCountByNodeIdPredicate(Expression predicateBody)
    {
        if (predicateBody is BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.Equal)
                return IsMemberAccess(binary.Left, "StartNodeId") || IsMemberAccess(binary.Left, "EndNodeId")
                    || IsMemberAccess(binary.Right, "StartNodeId") || IsMemberAccess(binary.Right, "EndNodeId");
            if (binary.NodeType == ExpressionType.OrElse)
                return IsCountByNodeIdPredicate(binary.Left) && IsCountByNodeIdPredicate(binary.Right);
        }
        return false;
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(IEnumerable<>))
                return type.GetGenericArguments()[0];
        }

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private static string? GetRelationshipLabel(IEnumerable enumerable, Type elementType)
    {
        foreach (var item in enumerable)
        {
            if (item == null) continue;

            try
            {
                return Labels.GetLabelFromType(elementType);
            }
            catch { }

            var name = elementType.Name;
            if (name.EndsWith("Relationship", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "Relationship".Length);
            return name;
        }

        var typeName = elementType.Name;
        if (typeName.EndsWith("Relationship", StringComparison.Ordinal))
            typeName = typeName.Substring(0, typeName.Length - "Relationship".Length);
        return typeName;
    }
}
