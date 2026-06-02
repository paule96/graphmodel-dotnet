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

using System.Linq.Expressions;

/// <summary>
/// Detects the type of aggregation operation from an expression tree.
/// </summary>
internal static class AggregationDetector
{
    public static string? DetectAggregationType(Expression expression)
    {
        Expression current = expression;
        while (current is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;

            // Check for Count/LongCount markers
            if (methodName is "Count" or "CountAsync" or "CountAsyncMarker" or
                           "LongCount" or "LongCountAsync" or "LongCountAsyncMarker")
            {
                return "Count";
            }

            if (methodName is "Any" or "AnyAsync" or "AnyAsyncMarker")
                return "Any";
            if (methodName is "All" or "AllAsync" or "AllAsyncMarker")
                return "All";
            if (methodName is "Sum" or "SumAsync" or "SumAsyncMarker")
                return "Sum";
            if (methodName is "Average" or "AverageAsync" or "AverageAsyncMarker")
                return "Average";
            if (methodName is "Min" or "MinAsync" or "MinAsyncMarker")
                return "Min";
            if (methodName is "Max" or "MaxAsync" or "MaxAsyncMarker")
                return "Max";

            // First/Last/Single markers
            if (methodName is "First" or "FirstAsync" or "FirstAsyncMarker" or
                           "FirstOrDefault" or "FirstOrDefaultAsync" or "FirstOrDefaultAsyncMarker")
                return "First";
            if (methodName is "Last" or "LastAsync" or "LastAsyncMarker" or
                           "LastOrDefault" or "LastOrDefaultAsync" or "LastOrDefaultAsyncMarker")
                return "Last";
            if (methodName is "Single" or "SingleAsync" or "SingleAsyncMarker" or
                           "SingleOrDefault" or "SingleOrDefaultAsync" or "SingleOrDefaultAsyncMarker")
                return "Single";

            // ToDictionary markers
            if (methodName is "ToDictionaryAsync" or "ToDictionaryAsyncMarker")
                return "ToDictionary";

            if (methodCall.Arguments.Count > 0)
                current = methodCall.Arguments[0];
            else
                break;
        }

        return null;
    }
}
