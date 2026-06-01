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
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Npgsql.Age.Types;

/// <summary>
/// Materializes scalar (non-entity) results from AGE Cypher queries.
/// Handles type conversion from Agtype to CLR types for aggregation results
/// like count, sum, average, min, max, etc.
/// </summary>
internal static class ScalarResultMaterializer
{
    /// <summary>
    /// Reads scalar results from a query and materializes them as <typeparamref name="T"/>.
    /// </summary>
    public static async Task<T?> MaterializeAsync<T>(
        NpgsqlDataReader reader,
        Type elementType,
        CancellationToken cancellationToken,
        string? aggregationType = null)
    {
        var results = new List<object?>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Handle DBNull (e.g., SUM/AVG of empty set returns a single NULL row)
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                // Average of empty set should throw InvalidOperationException
                if (string.Equals(aggregationType, "Average", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Sequence contains no elements");
                }

                // Sum/Min/Max of empty set returns null, which defaults to 0/default
                results.Add(null);
                continue;
            }

            var agVal = reader.GetFieldValue<Agtype>(0);
            object? rawValue = null;

            // Use Agtype's native accessor methods based on target type
            if (elementType == typeof(string)) { try { rawValue = agVal.GetString(); } catch { rawValue = agVal.ToString(); } }
            else if (elementType == typeof(long) || elementType == typeof(long?)) { try { rawValue = agVal.GetInt64(); } catch { } }
            else if (elementType == typeof(int) || elementType == typeof(int?))
            {
                // count(*) in AGE returns bigint (Int64). Try Int32 first, then fall back to Int64 and cast.
                try { rawValue = agVal.GetInt32(); }
                catch { try { rawValue = (int)agVal.GetInt64(); } catch { } }
            }
            else if (elementType == typeof(short) || elementType == typeof(short?)) { try { rawValue = agVal.GetInt16(); } catch { } }
            else if (elementType == typeof(double) || elementType == typeof(double?)) { try { rawValue = agVal.GetDouble(); } catch { } }
            else if (elementType == typeof(float) || elementType == typeof(float?)) { try { rawValue = agVal.GetFloat(); } catch { } }
            else if (elementType == typeof(decimal) || elementType == typeof(decimal?)) { try { rawValue = agVal.GetDecimal(); } catch { } }
            else if (elementType == typeof(bool) || elementType == typeof(bool?)) { try { rawValue = agVal.GetBoolean(); } catch { } }
            else if (elementType == typeof(byte)) { try { rawValue = agVal.GetByte(); } catch { } }
            else { try { rawValue = agVal.GetString() ?? agVal.ToString(); } catch { rawValue = agVal.ToString(); } }

            if (rawValue is null)
                rawValue = agVal.ToString();

            results.Add(rawValue);
        }

        return CollectionHelper.ToListOrSingle<T>(results, elementType);
    }
}
