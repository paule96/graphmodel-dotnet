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

namespace Cvoya.Graph.Model.Age.Core.Entities;

using System.Linq.Expressions;
using Cvoya.Graph.Model.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.Age.Types;

/// <summary>
/// Age-specific result processor.
/// </summary>
internal sealed class AgeResultProcessor
{
    private readonly EntityFactory _entityFactory;
    private readonly AgeEntityMapper _entityMapper;
    private readonly ILogger<AgeResultProcessor> _logger;

    public AgeResultProcessor(EntityFactory entityFactory, AgeEntityMapper entityMapper, ILoggerFactory? loggerFactory = null)
    {
        _entityFactory = entityFactory ?? throw new ArgumentNullException(nameof(entityFactory));
        _entityMapper = entityMapper ?? throw new ArgumentNullException(nameof(entityMapper));
        _logger = loggerFactory?.CreateLogger<AgeResultProcessor>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgeResultProcessor>.Instance;
    }

    public async Task<List<EntityInfo>> ProcessAsync(
        NpgsqlDataReader reader,
        Type elementType,
        CancellationToken cancellationToken,
        LambdaExpression? projectionExpression = null,
        Type? projectionResultType = null,
        string? aggregationType = null)
    {
        var results = new List<EntityInfo>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                // For multi-column projections, skip single-column Agtype read and go straight
                // to ReadMultiColumnRowAsync which handles all columns including nulls
                if (reader.FieldCount > 1 && !typeof(INode).IsAssignableFrom(elementType) && !typeof(IRelationship).IsAssignableFrom(elementType))
                {
                    var entityInfo = await ReadMultiColumnRowAsync(reader, elementType, cancellationToken).ConfigureAwait(false);
                    if (entityInfo != null)
                        results.Add(entityInfo);
                    continue;
                }

                // Single column result - read the Agtype value
                if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                    continue;

                var agtype = reader.GetFieldValue<Agtype>(0);
                _logger.LogDebug("ProcessAsync: Read Agtype IsVertex={IsVertex}, IsEdge={IsEdge}",
                    agtype.IsVertex, agtype.IsEdge);

                if (agtype.IsVertex && typeof(INode).IsAssignableFrom(elementType))
                {
                    var vertex = agtype.GetVertex();
                    var entityInfo = _entityMapper.MapVertex(vertex, elementType);
                    results.Add(entityInfo);
                }
                else if (agtype.IsEdge && typeof(IRelationship).IsAssignableFrom(elementType))
                {
                    var edge = agtype.GetEdge();
                    var entityInfo = _entityMapper.MapEdge(edge, elementType);
                    results.Add(entityInfo);
                }
                else if (!typeof(INode).IsAssignableFrom(elementType) && !typeof(IRelationship).IsAssignableFrom(elementType))
                {
                    // For projections (single or multi-column), read all columns and create
                    // an EntityInfo with properties matching column names.
                    // This enables ResultMaterializer.CreateAnonymousTypeObject to construct
                    // the anonymous type or complex type instances from the EntityInfo.
                    var entityInfo = await ReadMultiColumnRowAsync(reader, elementType, cancellationToken).ConfigureAwait(false);
                    if (entityInfo != null)
                    {
                        results.Add(entityInfo);
                    }
                    else
                    {
                        // Fallback: scalar value for single-column non-projection results
                        _logger.LogDebug("ProcessAsync: Agtype is not Vertex/Edge, treating as scalar value. Type={Type}",
                            agtype.GetType().Name);
                        
                        var agTypeStr = agtype.ToString();
                        _logger.LogDebug("ProcessAsync: Scalar value string='{Str}', elementType={Type}", agTypeStr, elementType.Name);
                        object? scalarValue = agTypeStr;
                        
                        // Try to convert to the target element type
                        if (elementType == typeof(string)) { /* Already a string, keep as-is */ }
                        else if (elementType == typeof(int) && int.TryParse(agTypeStr, out var intVal)) scalarValue = intVal;
                        else if (elementType == typeof(long) && long.TryParse(agTypeStr, out var longVal)) scalarValue = longVal;
                        else if (elementType == typeof(double) && double.TryParse(agTypeStr, out var doubleVal)) scalarValue = doubleVal;
                        else if (elementType == typeof(bool) && bool.TryParse(agTypeStr, out var boolVal)) scalarValue = boolVal;
                        
                        var simpleProps = new Dictionary<string, Property>(StringComparer.Ordinal)
                        {
                            ["Value"] = new Property(null!, "Value", false, new SimpleValue(scalarValue ?? (object)agTypeStr!, scalarValue?.GetType() ?? typeof(object)))
                        };
                        var fallbackEntityInfo = new EntityInfo(elementType, "ScalarValue", Array.Empty<string>(), simpleProps, new Dictionary<string, Property>(StringComparer.Ordinal));
                        results.Add(fallbackEntityInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessAsync: Error processing record");
                throw;
            }
        }

        return results;
    }

    /// <summary>
    /// Reads a multi-column row from the reader and builds an EntityInfo with simple properties
    /// matching the column names. This supports anonymous type projections like
    /// .Select(p => new { p.Name, p.Age }) where the RETURN clause produces multiple columns.
    /// </summary>
    private async Task<EntityInfo?> ReadMultiColumnRowAsync(
        NpgsqlDataReader reader,
        Type elementType,
        CancellationToken cancellationToken)
    {
        var simpleProps = new Dictionary<string, Property>(StringComparer.Ordinal);
        var complexProps = new Dictionary<string, Property>(StringComparer.Ordinal);
        var fieldCount = reader.FieldCount;

        for (int i = 0; i < fieldCount; i++)
        {
            // Get the column name and strip the "c_" prefix added by BuildColumnDefinitions
            var columnName = reader.GetName(i);
            var propertyName = columnName.StartsWith("c_", StringComparison.Ordinal)
                ? columnName.Substring(2)
                : columnName;

            // Map path segment aliases to the property names expected by the
            // ResultMaterializer.CreatePathSegmentFromEntityInfo.
            if (columnName is "src0" or "src1" or "src2" or "src3" or "src4")
                propertyName = "StartNode";
            else if (columnName is "r0" or "r1" or "r2" or "r3" or "r4")
                propertyName = "Relationship";
            else if (columnName is "tgt0" or "tgt1" or "tgt2" or "tgt3" or "tgt4")
                propertyName = "EndNode";

            if (await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false))
                continue;

            try
            {
                var agVal = reader.GetFieldValue<Agtype>(i);

                if (agVal.IsVertex)
                {
                    var vertex = agVal.GetVertex();
                    var nestedTarget = elementType.GetProperty(propertyName)?.PropertyType ?? typeof(object);
                    var nestedEntityInfo = _entityMapper.MapVertex(vertex, nestedTarget);
                    complexProps[propertyName] = new Property(null!, propertyName, false, nestedEntityInfo);
                }
                else if (agVal.IsEdge)
                {
                    var edge = agVal.GetEdge();
                    var nestedTarget = elementType.GetProperty(propertyName)?.PropertyType ?? typeof(object);
                    var nestedEntityInfo = _entityMapper.MapEdge(edge, nestedTarget);
                    complexProps[propertyName] = new Property(null!, propertyName, false, nestedEntityInfo);
                }
                else
                {
                    // Determine target type from elementType's matching property
                    var targetProp = elementType.GetProperty(propertyName);
                    var targetType = targetProp?.PropertyType ?? typeof(string);
                    object? convertedValue = null;

                    // Try Agtype typed accessors first
                    try
                    {
                        if (targetType == typeof(string)) { convertedValue = agVal.GetString(); }
                        else if (targetType == typeof(int) || targetType == typeof(int?)) { convertedValue = agVal.GetInt32(); }
                        else if (targetType == typeof(long) || targetType == typeof(long?)) { convertedValue = agVal.GetInt64(); }
                        else if (targetType == typeof(double) || targetType == typeof(double?)) { convertedValue = agVal.GetDouble(); }
                        else if (targetType == typeof(float) || targetType == typeof(float?)) { convertedValue = agVal.GetFloat(); }
                        else if (targetType == typeof(decimal) || targetType == typeof(decimal?)) { convertedValue = agVal.GetDecimal(); }
                        else if (targetType == typeof(bool) || targetType == typeof(bool?)) { convertedValue = agVal.GetBoolean(); }
                        else if (targetType == typeof(DateTime) || targetType == typeof(DateTime?))
                        {
                            var strVal = agVal.ToString()?.Trim('"', ' ', '\'');
                            DateTime dtVal;
                            var parsed = DateTime.TryParse(strVal, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind, out dtVal)
                                || DateTime.TryParseExact(strVal,
                                    ["yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd",
                                     "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"],
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out dtVal);
                            if (parsed)
                                convertedValue = dtVal.Kind == DateTimeKind.Unspecified
                                    ? DateTime.SpecifyKind(dtVal, DateTimeKind.Local) : dtVal;
                        }
                    }
                    catch
                    {
                        // Typed accessor failed — fall back below
                    }

                    // If typed accessor didn't produce a value, try getting as string and converting
                    if (convertedValue == null)
                    {
                        try { convertedValue = agVal.GetString(); }
                        catch { /* ignore */ }
                    }

                    // If still null, try parsing from the string representation
                    if (convertedValue == null)
                        convertedValue = ConvertScalarAgtype(agVal.ToString() ?? string.Empty, targetType);

                    if (convertedValue != null)
                        simpleProps[propertyName] = new Property(null!, propertyName, false,
                            new SimpleValue(convertedValue, convertedValue.GetType()));
                }
            }
            catch
            {
                // Skip columns that can't be read
            }
        }

        // Group path segment columns (_src, _r, _tgt suffixes) into single complex properties.
        // For example, Foo_src + Foo_r + Foo_tgt → Foo (EntityInfo with StartNode, Relationship, EndNode)
        var pathSegmentKeys = complexProps.Keys
            .Where(k => k.EndsWith("_src", StringComparison.Ordinal) ||
                        k.EndsWith("_r", StringComparison.Ordinal) ||
                        k.EndsWith("_tgt", StringComparison.Ordinal))
            .Select(k => k.Length > 4 ? k.Substring(0, k.Length - 4) : k)
            .Distinct()
            .ToList();

        foreach (var baseKey in pathSegmentKeys)
        {
            var srcKey = $"{baseKey}_src";
            var relKey = $"{baseKey}_r";
            var tgtKey = $"{baseKey}_tgt";

            if (complexProps.TryGetValue(srcKey, out var srcProp) &&
                complexProps.TryGetValue(tgtKey, out var tgtProp))
            {
                complexProps.Remove(srcKey);
                complexProps.Remove(tgtKey);

                var segmentSimpleProps = new Dictionary<string, Property>(StringComparer.Ordinal);
                var segmentComplexProps = new Dictionary<string, Property>(StringComparer.Ordinal);

                if (srcProp.Value is EntityInfo srcEntity)
                    segmentComplexProps["StartNode"] = new Property(null!, "StartNode", false, srcEntity);
                if (tgtProp.Value is EntityInfo tgtEntity)
                    segmentComplexProps["EndNode"] = new Property(null!, "EndNode", false, tgtEntity);

                if (complexProps.TryGetValue(relKey, out var relProp))
                {
                    complexProps.Remove(relKey);
                    if (relProp.Value is EntityInfo relEntity)
                        segmentComplexProps["Relationship"] = new Property(null!, "Relationship", false, relEntity);
                }

                // Determine the target path segment type from elementType
                var segmentTargetType = elementType.GetProperty(baseKey)?.PropertyType
                    ?? typeof(object);

                var segmentEntityInfo = new EntityInfo(
                    segmentTargetType,
                    string.Empty,
                    Array.Empty<string>(),
                    segmentSimpleProps,
                    segmentComplexProps);

                complexProps[baseKey] = new Property(null!, baseKey, false, segmentEntityInfo);
            }
        }

        if (simpleProps.Count == 0 && complexProps.Count == 0)
            return null;

        return new EntityInfo(
            elementType,
            string.Empty,
            Array.Empty<string>(),
            simpleProps,
            complexProps);
    }

    private static object? ConvertScalarAgtype(string agTypeStr, Type targetType)
    {
        if (targetType == typeof(string)) return agTypeStr;
        if (targetType == typeof(int) && int.TryParse(agTypeStr, out var intVal)) return intVal;
        if (targetType == typeof(long) && long.TryParse(agTypeStr, out var longVal)) return longVal;
        if (targetType == typeof(double) && double.TryParse(agTypeStr, out var doubleVal)) return doubleVal;
        if (targetType == typeof(float) && float.TryParse(agTypeStr, out var floatVal)) return floatVal;
        if (targetType == typeof(decimal) && decimal.TryParse(agTypeStr, out var decVal)) return decVal;
        if (targetType == typeof(bool) && bool.TryParse(agTypeStr, out var boolVal)) return boolVal;
        if (targetType == typeof(DateTime) && DateTime.TryParse(agTypeStr, out var dtVal)) return dtVal;
        return agTypeStr;
    }
}
