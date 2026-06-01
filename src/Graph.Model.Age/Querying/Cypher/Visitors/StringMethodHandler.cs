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
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles translation of .NET string methods to Cypher expressions for AGE.
/// </summary>
internal sealed class StringMethodHandler
{
    private readonly Func<Expression, string> _visitAndReturnCypher;
    private readonly Func<object?, string> _addParameter;
    private readonly ILogger _logger;

    public StringMethodHandler(
        Func<Expression, string> visitAndReturnCypher,
        Func<object?, string> addParameter,
        ILogger logger)
    {
        _visitAndReturnCypher = visitAndReturnCypher;
        _addParameter = addParameter;
        _logger = logger;
    }

    public Expression HandleStringMethod(MethodCallExpression node)
    {
        var obj = node.Object != null ? _visitAndReturnCypher(node.Object) : null;

        return node.Method.Name switch
        {
            "StartsWith" when node.Arguments.Count == 1 =>
                HandleStringStartsWith(obj, node.Arguments[0]),

            "EndsWith" when node.Arguments.Count == 1 =>
                HandleStringEndsWith(obj, node.Arguments[0]),

            "Contains" when node.Arguments.Count == 1 =>
                HandleStringContains(obj, node.Arguments[0]),

            "ToLower" when node.Arguments.Count == 0 =>
                Expression.Constant($"toLower({obj})"),

            "ToUpper" when node.Arguments.Count == 0 =>
                Expression.Constant($"toUpper({obj})"),

            "Trim" when node.Arguments.Count == 0 =>
                Expression.Constant($"trim({obj})"),

            "Substring" when node.Arguments.Count == 1 =>
                Expression.Constant($"substring({obj}, {_visitAndReturnCypher(node.Arguments[0])})"),

            "Substring" when node.Arguments.Count == 2 =>
                Expression.Constant($"substring({obj}, {_visitAndReturnCypher(node.Arguments[0])}, {_visitAndReturnCypher(node.Arguments[1])})"),

            "Replace" when node.Arguments.Count == 2 =>
                Expression.Constant($"replace({obj}, {_visitAndReturnCypher(node.Arguments[0])}, {_visitAndReturnCypher(node.Arguments[1])})"),

            "get_Length" when node.Arguments.Count == 0 =>
                Expression.Constant($"size({obj})"),

            _ => throw new NotSupportedException($"String method {node.Method.Name} is not supported")
        };
    }

    private Expression HandleStringContains(string? obj, Expression substringExpression)
    {
        object? substringValue = null;

        if (substringExpression is ConstantExpression constantExpr)
        {
            substringValue = constantExpr.Value;
        }
        else if (substringExpression is MemberExpression memberExpr)
        {
            try
            {
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(memberExpr, typeof(object)));
                var compiled = lambda.Compile();
                substringValue = compiled();
            }
            catch { }
        }

        if (substringValue is string substring)
        {
            var regexPattern = $".*{Regex.Escape(substring)}.*";
            var paramName = _addParameter(regexPattern);
            return Expression.Constant($"{obj} =~ {paramName}");
        }

        _logger.LogWarning("Could not evaluate Contains argument, attempting fallback");
        var substringCypher = _visitAndReturnCypher(substringExpression);
        return Expression.Constant($"{obj} =~ ('.*' + {substringCypher} + '.*')");
    }

    private Expression HandleStringStartsWith(string? obj, Expression prefixExpression)
    {
        object? prefixValue = null;

        if (prefixExpression is ConstantExpression constantExpr)
            prefixValue = constantExpr.Value;
        else if (prefixExpression is MemberExpression memberExpr)
        {
            try
            {
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(memberExpr, typeof(object)));
                prefixValue = lambda.Compile();
            }
            catch { }
        }

        if (prefixValue is string prefix)
        {
            var regexPattern = $"^{Regex.Escape(prefix)}.*";
            var paramName = _addParameter(regexPattern);
            return Expression.Constant($"{obj} =~ {paramName}");
        }

        var prefixCypher = _visitAndReturnCypher(prefixExpression);
        return Expression.Constant($"{obj} =~ ('^' + {prefixCypher} + '.*')");
    }

    private Expression HandleStringEndsWith(string? obj, Expression suffixExpression)
    {
        object? suffixValue = null;

        if (suffixExpression is ConstantExpression constantExpr)
            suffixValue = constantExpr.Value;
        else if (suffixExpression is MemberExpression memberExpr)
        {
            try
            {
                var lambda = Expression.Lambda<Func<object>>(Expression.Convert(memberExpr, typeof(object)));
                suffixValue = lambda.Compile();
            }
            catch { }
        }

        if (suffixValue is string suffix)
        {
            var regexPattern = $".*{Regex.Escape(suffix)}$";
            var paramName = _addParameter(regexPattern);
            return Expression.Constant($"{obj} =~ {paramName}");
        }

        var suffixCypher = _visitAndReturnCypher(suffixExpression);
        return Expression.Constant($"{obj} =~ ('.*' + {suffixCypher} + '$')");
    }
}
