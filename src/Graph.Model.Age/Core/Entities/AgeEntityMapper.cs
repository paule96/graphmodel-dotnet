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

using System.Globalization;
using System.Text.Json;
using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql.Age.Types;
using static Cvoya.Graph.Model.Age.Core.Entities.LabelsExtractor;

/// <summary>
/// Converts AGE vertices/edges back into EntityInfo structures for deserialization.
/// </summary>
internal sealed class AgeEntityMapper
{
    private readonly EntityFactory entityFactory;
    private readonly ILogger<AgeEntityMapper> _logger;

    public AgeEntityMapper(EntityFactory entityFactory, ILoggerFactory? loggerFactory)
    {
        this.entityFactory = entityFactory;
        _logger = loggerFactory?.CreateLogger<AgeEntityMapper>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AgeEntityMapper>.Instance;
    }

    public EntityInfo MapVertex(Vertex vertex, Type targetType)
    {
        var label = vertex.Label;
        var labels = ExtractLabels(vertex);

        // Resolve the most derived type for class hierarchy support.
        // When querying by base type (e.g., Person), if the stored label corresponds to
        // a derived type (e.g., Manager), we need to deserialize as the derived type.
        // First try from inheritance_labels (most derived label in the hierarchy),
        // then fall back to the vertex label.
        var resolvedType = targetType;
        var typeResolutionLabel = labels.FirstOrDefault() ?? label;
        if (!string.IsNullOrWhiteSpace(typeResolutionLabel))
        {
            try
            {
                Type? mostDerived;

                // If the target type is an interface (e.g., INode), resolve the concrete
                // type directly from the vertex label.
                if (targetType.IsInterface)
                {
                    try
                    {
                        mostDerived = Labels.GetTypeFromLabel(typeResolutionLabel);
                    }
                    catch
                    {
                        mostDerived = null;
                    }
                }
                else
                {
                    mostDerived = Labels.GetMostDerivedType(targetType, typeResolutionLabel);
                }

                if (mostDerived != null)
                {
                    resolvedType = mostDerived;
                    _logger.LogDebug("MapVertex: Resolved type from label '{Label}': {Type} (was {OriginalType})",
                        typeResolutionLabel, resolvedType.Name, targetType.Name);
                }
            }
            catch
            {
                // If resolution fails, fall back to the original target type
            }
        }

        var simpleProperties = new Dictionary<string, Property>(StringComparer.Ordinal);
        var complexProperties = new Dictionary<string, Property>(StringComparer.Ordinal);

        _logger.LogDebug("MapVertex: Label={Label}, PropertiesCount={Count}, TargetType={Type}",
            label, vertex.Properties.Count, resolvedType.Name);

        foreach (var (key, rawValue) in vertex.Properties)
        {
            // Skip internal AGE inheritance property that is handled separately
            if (key == "inheritance_labels")
                continue;

            var value = NormalizeValue(rawValue);
            var csharpPropertyName = MapAgePropertyNameToCSharp(key);

            _logger.LogDebug("MapVertex: Processing property '{AgeKey}' -> '{CSharpKey}', Value={Value}",
                key, csharpPropertyName, value);

            var propertyInfo = resolvedType.GetProperty(csharpPropertyName);
            var propertyType = propertyInfo?.PropertyType;
            var convertedValue = ConvertValue(value, csharpPropertyName, propertyType);

            // If the raw value is a JsonElement Object and the target property is complex,
            // convert it to a Dictionary so it can be matched by the IDictionary check below.
            // This handles complex POCO properties (like AddressValue, MemorySourceNode) 
            // that are stored as JSON objects in AGE and returned as JsonElement from the vertex.
            if (rawValue is JsonElement rawJson && rawJson.ValueKind == JsonValueKind.Object
                && propertyType != null && !GraphDataModel.IsSimple(propertyType))
            {
                convertedValue = ConvertJsonElementToDictionary(rawJson);
            }
            // Also handle the case where ConvertValue returns a JsonElement for complex types
            else if (convertedValue is JsonElement convJson && convJson.ValueKind == JsonValueKind.Object
                && propertyType != null && !GraphDataModel.IsSimple(propertyType))
            {
                convertedValue = ConvertJsonElementToDictionary(convJson);
            }

            // Handle JSON string that contains a serialized complex collection.
            // When a List<Dictionary<string, object?>> is stored in AGE, it comes
            // back as a string. The format may be:
            //   - A proper JSON array: "[{...},{...}]" (from our JsonSerializer change)
            //   - Concatenated objects: "{...},{...}" (AGE's internal representation)
            // Parse it back into a List<IDictionary<string, object?>> for the
            // EntityCollection code path below.
            if (convertedValue is string strVal && strVal.Length >= 2 && (strVal[0] == '[' || strVal[0] == '{'))
            {
                var jsonToParse = strVal[0] == '[' ? strVal : $"[{strVal}]";
                try
                {
                    using var doc = JsonDocument.Parse(jsonToParse);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                        && doc.RootElement[0].ValueKind == JsonValueKind.Object)
                    {
                        var parsedItems = doc.RootElement.EnumerateArray()
                            .Select(e => (object)ConvertJsonElementToDictionary(e))
                            .Where(static item => item is IDictionary<string, object?>)
                            .ToList();
                        if (parsedItems.Count > 0)
                        {
                            convertedValue = parsedItems;
                            _logger.LogDebug("MapVertex: Parsed JSON collection for property '{Prop}' with {Count} items",
                                csharpPropertyName, parsedItems.Count);
                        }
                    }
                }
                catch
                {
                    // Not valid JSON — treat as a regular string
                }
            }

            if (convertedValue is IDictionary<string, object?> dict && !GraphDataModel.IsSimple(dict.GetType()))
            {
                var entityInfo = CreateEntityInfoFromDictionary(dict, csharpPropertyName);
                complexProperties[csharpPropertyName] = new Property(null!, csharpPropertyName, false, entityInfo);
                continue;
            }
            else if (convertedValue is IList<object?> list)
            {
                bool isComplexCollection = false;
                if (list.Count > 0 && list[0] is IDictionary<string, object?>)
                    isComplexCollection = true;
                else if (propertyType != null && GraphDataModel.IsComplex(propertyType))
                    isComplexCollection = true;

                if (isComplexCollection)
                {
                    var elementInfos = list.Select(item =>
                    {
                        if (item is JsonElement jsonElement)
                            return CreateEntityInfoFromDictionary(ConvertJsonElementToDictionary(jsonElement), csharpPropertyName);
                        if (item is IDictionary<string, object?> d)
                            return CreateEntityInfoFromDictionary(d, csharpPropertyName);
                        return new EntityInfo(typeof(object), csharpPropertyName, Array.Empty<string>(), new Dictionary<string, Property>(StringComparer.Ordinal), new Dictionary<string, Property>(StringComparer.Ordinal));
                    }).ToList();

                    complexProperties[csharpPropertyName] = new Property(null!, csharpPropertyName, false, new EntityCollection(typeof(object), elementInfos));
                }
                else
                {
                    var elementType = DetermineElementType(list);
                    var simpleValues = list.Select(item => new SimpleValue(item ?? null!, item?.GetType() ?? elementType)).ToList();
                    simpleProperties[csharpPropertyName] = new Property(null!, csharpPropertyName, false, new SimpleCollection(simpleValues, elementType));
                }
            }
            else
            {
                simpleProperties[csharpPropertyName] = CreateSimpleProperty(csharpPropertyName, convertedValue);
            }
        }

        if (!simpleProperties.ContainsKey(nameof(INode.Labels)))
        {
            simpleProperties[nameof(INode.Labels)] = new Property(null!, nameof(INode.Labels), false,
                new SimpleCollection(labels.Select(l => new SimpleValue(l, typeof(string))).ToList(), typeof(string)));
        }

        return new EntityInfo(
            resolvedType,
            label,
            labels,
            simpleProperties,
            complexProperties,
            InheritanceLabels: labels
        );
    }

    public EntityInfo MapEdge(Edge edge, Type targetType)
    {
        var label = edge.Label;
        var allLabels = ExtractLabels(edge);

        // Resolve the most derived type for relationship hierarchy support.
        var resolvedType = targetType;
        var typeResolutionLabel = allLabels.FirstOrDefault() ?? label;
        if (!string.IsNullOrWhiteSpace(typeResolutionLabel))
        {
            try
            {
                Type? mostDerived;

                // If the target type is an interface (e.g., IRelationship), resolve the concrete
                // type directly from the edge label instead of walking the hierarchy.
                if (targetType.IsInterface)
                {
                    try
                    {
                        mostDerived = Labels.GetTypeFromLabel(typeResolutionLabel);
                    }
                    catch
                    {
                        mostDerived = null;
                    }
                }
                else
                {
                    mostDerived = Labels.GetMostDerivedType(targetType, typeResolutionLabel);
                }

                if (mostDerived != null)
                {
                    resolvedType = mostDerived;
                    _logger.LogDebug("MapEdge: Resolved type from label '{Label}': {Type} (was {OriginalType})",
                        typeResolutionLabel, resolvedType.Name, targetType.Name);
                }
            }
            catch
            {
                // If resolution fails, fall back to the original target type
            }
        }

        var simpleProperties = new Dictionary<string, Property>(StringComparer.Ordinal);

        foreach (var (key, rawValue) in edge.Properties)
        {
            // Skip internal AGE inheritance property that is handled separately
            if (key == "inheritance_labels")
                continue;

            var value = NormalizeValue(rawValue);
            var csharpPropertyName = MapAgePropertyNameToCSharp(key);
            var propertyInfo = resolvedType.GetProperty(csharpPropertyName);
            var propertyType = propertyInfo?.PropertyType;
            var convertedValue = ConvertValue(value, csharpPropertyName, propertyType);
            simpleProperties[csharpPropertyName] = CreateSimpleProperty(csharpPropertyName, convertedValue);
        }

        if (!simpleProperties.ContainsKey(nameof(IRelationship.Id)))
            simpleProperties[nameof(IRelationship.Id)] = new Property(null!, nameof(IRelationship.Id), false, new SimpleValue(edge.Id.Value.ToString(), typeof(string)));
        if (!simpleProperties.ContainsKey(nameof(IRelationship.StartNodeId)))
            simpleProperties[nameof(IRelationship.StartNodeId)] = new Property(null!, nameof(IRelationship.StartNodeId), false, new SimpleValue(edge.StartId.Value.ToString(), typeof(string)));
        if (!simpleProperties.ContainsKey(nameof(IRelationship.EndNodeId)))
            simpleProperties[nameof(IRelationship.EndNodeId)] = new Property(null!, nameof(IRelationship.EndNodeId), false, new SimpleValue(edge.EndId.Value.ToString(), typeof(string)));

        // Set Type property from the edge label
        simpleProperties[nameof(IRelationship.Type)] = new Property(
            null!,
            nameof(IRelationship.Type),
            false,
            new SimpleValue(edge.Label, typeof(string)));

        var inheritanceLabels = allLabels;
        return new EntityInfo(
            resolvedType,
            edge.Label,
            allLabels,
            simpleProperties,
            new Dictionary<string, Property>(StringComparer.Ordinal),
            InheritanceLabels: inheritanceLabels
        );
    }

    private static string MapAgePropertyNameToCSharp(string ageKey)
    {
        return ageKey switch
        {
            "user_id" => "Id",
            _ => ageKey
        };
    }

    // ExtractLabels(Vertex) and ExtractLabels(Edge) moved to LabelsExtractor
    // and imported via `using static LabelsExtractor`.

    private static object? NormalizeValue(object? rawValue)
    {
        if (rawValue is Agtype agtypeValue)
        {
            if (agtypeValue.IsVertex) return agtypeValue.GetVertex();
            if (agtypeValue.IsEdge) return agtypeValue.GetEdge();
            return agtypeValue.ToString();
        }
        return rawValue;
    }

    private static object? ConvertValue(object? value, string propertyName, Type? targetType)
    {
        if (value is null) return null;
        if (targetType == null || targetType == typeof(object)) return value;

        // Unwrap Nullable<T> to its underlying type for comparison
        var effectiveType = targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>)
            ? Nullable.GetUnderlyingType(targetType)!
            : targetType;

        if (value is JsonElement jsonElement)
        {
            switch (effectiveType)
            {
                case not null when effectiveType == typeof(string):
                    return jsonElement.GetString();
                case not null when effectiveType == typeof(int):
                    return jsonElement.GetInt32();
                case not null when effectiveType == typeof(long):
                    return jsonElement.GetInt64();
                case not null when effectiveType == typeof(double):
                    return jsonElement.GetDouble();
                case not null when effectiveType == typeof(bool):
                    return jsonElement.GetBoolean();
                case not null when effectiveType == typeof(DateTime):
                    return jsonElement.GetDateTime();
                case not null when effectiveType == typeof(Guid):
                    return jsonElement.GetGuid();
                case not null when effectiveType == typeof(Point):
                    return ParsePointFromJson(jsonElement);
                default:
                    return jsonElement.ToString()!;
            }
        }

        if (value is string strVal)
        {
            if (effectiveType == typeof(DateTime))
                return DateTime.Parse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            if (effectiveType == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(strVal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (effectiveType == typeof(Point))
                return ParsePointFromJson(JsonDocument.Parse(strVal).RootElement);
            if (effectiveType == typeof(bool))
            {
                if (strVal.Equals("true", StringComparison.OrdinalIgnoreCase) || strVal == "1") return true;
                if (strVal.Equals("false", StringComparison.OrdinalIgnoreCase) || strVal == "0") return false;
            }
            if (effectiveType == typeof(int) && int.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var intVal)) return intVal;
            if (effectiveType == typeof(long) && long.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var longVal)) return longVal;
            if (effectiveType == typeof(double) && double.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleVal)) return doubleVal;
            if (effectiveType == typeof(float) && float.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var floatVal)) return floatVal;
            if (effectiveType == typeof(decimal) && decimal.TryParse(strVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var decVal)) return decVal;
        }

        // Konnektr 2.x: complex property values arrive as Dictionary<string, object>
        // from InferredObjectConverter. Convert them to the target C# type so the
        // generated serializer receives them as SimpleValue (which it expects for
        // simple POCOs like Point). For non-simple types (like MemorySource), leave
        // as dictionary so the IDictionary check in MapVertex stores it as an
        // EntityInfo that the generated complex serializer can process.
        if (value is IDictionary<string, object?> dict
            && effectiveType != typeof(IDictionary<string, object>)
            && effectiveType != typeof(Dictionary<string, object>)
            && GraphDataModel.IsSimple(effectiveType))
        {
            try
            {
                var json = JsonSerializer.Serialize(dict);
                var result = JsonSerializer.Deserialize(json, effectiveType,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result != null)
                    return result;
            }
            catch
            {
                // Fall through — the dictionary will be handled below
            }
        }

        // Handle numeric type conversions for edge entity property values
        // that come from AGE as types different from the target CLR type
        // (e.g., AGE returns Decimal for numeric properties, target expects double).
        if (value is not string && effectiveType != value.GetType())
        {
            try
            {
                if (effectiveType == typeof(double) && value is decimal decVal)
                    return (double)decVal;
                if (effectiveType == typeof(float) && value is decimal decFloat)
                    return (float)decFloat;
                if (effectiveType == typeof(int) && value is long longVal)
                    return (int)longVal;
                if (effectiveType == typeof(long) && value is int intVal)
                    return (long)intVal;
                return Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture);
            }
            catch
            {
                // If conversion fails, fall through to return original value
            }
        }

        return value;
    }

    private static Point ParsePointFromJson(JsonElement json)
    {
        double longitude = 0, latitude = 0, height = 0;
        if (json.TryGetProperty("longitude", out var lon)) longitude = lon.GetDouble();
        else if (json.TryGetProperty("Longitude", out lon)) longitude = lon.GetDouble();
        else if (json.TryGetProperty("x", out var x)) longitude = x.GetDouble();
        else if (json.ValueKind == JsonValueKind.Array && json.GetArrayLength() >= 2)
        {
            longitude = json[0].GetDouble();
            latitude = json[1].GetDouble();
            if (json.GetArrayLength() >= 3) height = json[2].GetDouble();
            return new Point { Longitude = longitude, Latitude = latitude, Height = height };
        }
        if (json.TryGetProperty("latitude", out var lat)) latitude = lat.GetDouble();
        else if (json.TryGetProperty("Latitude", out lat)) latitude = lat.GetDouble();
        else if (json.TryGetProperty("y", out var y)) latitude = y.GetDouble();
        if (json.TryGetProperty("height", out var h)) height = h.GetDouble();
        else if (json.TryGetProperty("Height", out h)) height = h.GetDouble();
        else if (json.TryGetProperty("z", out var z)) height = z.GetDouble();
        return new Point { Longitude = longitude, Latitude = latitude, Height = height };
    }

    private static Property CreateSimpleProperty(string name, object? value)
    {
        if (value is null)
            return new Property(null!, name, false, new SimpleValue(null!, typeof(object)));
        return new Property(null!, name, false, new SimpleValue(value, value.GetType()));
    }

    private static EntityInfo CreateEntityInfoFromDictionary(IDictionary<string, object?> dict, string typeName)
    {
        var simpleProps = new Dictionary<string, Property>(StringComparer.Ordinal);
        var complexProps = new Dictionary<string, Property>(StringComparer.Ordinal);
        foreach (var (key, val) in dict)
        {
            if (val is IDictionary<string, object?> nestedDict)
            {
                // Nested complex object — recurse to create nested EntityInfo
                var nestedEntityInfo = CreateEntityInfoFromDictionary(nestedDict, key);
                complexProps[key] = new Property(null!, key, false, nestedEntityInfo);
            }
            else if (val is IList<object?> list)
            {
                // Check if this is a collection of complex objects
                if (list.Count > 0 && list[0] is IDictionary<string, object?>)
                {
                    var elementInfos = list
                        .Select(item => item is IDictionary<string, object?> d
                            ? CreateEntityInfoFromDictionary(d, key)
                            : new EntityInfo(typeof(object), key, Array.Empty<string>(),
                                new Dictionary<string, Property>(StringComparer.Ordinal),
                                new Dictionary<string, Property>(StringComparer.Ordinal)))
                        .ToList();
                    complexProps[key] = new Property(null!, key, false, new EntityCollection(typeof(object), elementInfos));
                }
                else
                {
                    var elementType = DetermineElementType(list);
                    var simpleValues = list.Select(item => new SimpleValue(item ?? null!, item?.GetType() ?? elementType)).ToList();
                    simpleProps[key] = new Property(null!, key, false, new SimpleCollection(simpleValues, elementType));
                }
            }
            else
            {
                simpleProps[key] = CreateSimpleProperty(key, val);
            }
        }
        return new EntityInfo(typeof(object), typeName, Array.Empty<string>(), simpleProps, complexProps);
    }

    private static Dictionary<string, object?> ConvertJsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElementToValue(prop.Value);
        }
        return dict;
    }

    private static object? ConvertJsonElementToValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => ConvertJsonElementToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElementToValue).ToList(),
            _ => null
        };
    }

    private static bool IsComplexCollectionType(Type type)
    {
        if (!type.IsGenericType) return false;
        var elementType = type.GetGenericArguments().FirstOrDefault();
        return elementType != null && GraphDataModel.IsComplex(elementType);
    }

    private static Type DetermineElementType(IList<object?> list)
    {
        foreach (var item in list)
        {
            if (item != null) return item.GetType();
        }
        return typeof(object);
    }
}
