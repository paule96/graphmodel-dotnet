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
using System.Linq;
using System.Linq.Expressions;

/// <summary>
/// Shared helper for expression-to-Cypher translation used across multiple visitors.
/// </summary>
internal static class ExpressionTranslationHelper
{
    /// <summary>
    /// Maps C# property names to AGE storage names.
    /// </summary>
    public static string MapPropertyName(string csharpPropertyName)
    {
        return csharpPropertyName switch
        {
            // Map C# "Id" property to our prefixed "user_id" field to avoid conflict with PostgreSQL internal "Id"
            // This ensures we always use our application-controlled IDs, not PostgreSQL internal IDs
            "Id" => "user_id",

            // For all other properties, keep the same name
            _ => csharpPropertyName
        };
    }

    /// <summary>
    /// Attempts to evaluate an expression at compile time and return its string value.
    /// Falls back to the expression's string representation on failure.
    /// </summary>
    public static string TryCompileEval(Expression expr)
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

    /// <summary>
    /// Checks whether the given type is a path segment type (IGraphPathSegment or GraphPathSegment).
    /// </summary>
    public static bool IsPathSegmentType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        if (type.GetGenericTypeDefinition().Name.Contains("GraphPathSegment", StringComparison.Ordinal))
        {
            return true;
        }

        // Also check for the interface form (IGraphPathSegment) used as result types
        if (type.GetGenericTypeDefinition().Name.Contains("IGraphPathSegment", StringComparison.Ordinal))
        {
            return true;
        }

        return type.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.Contains("IGraphPathSegment", StringComparison.Ordinal));
    }
}
