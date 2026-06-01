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

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cvoya.Graph.Model;
using Npgsql.Age;
using Microsoft.Extensions.Logging;
using Npgsql.Age.Types;
using static Cvoya.Graph.Model.Age.Querying.Cypher.Visitors.Core.ExpressionTranslationHelper;

/// <summary>
/// Validates attribute-driven constraints for AGE entities before persisting changes.
/// </summary>
internal static class AgeEntityAttributeValidator
{
    public static async Task ValidateNodeAsync<TNode>(
        TNode node,
        AgeGraphContext context,
        AgeGraphTransaction transaction,
        bool isUpdate,
        CancellationToken cancellationToken)
        where TNode : INode
    {
        ArgumentNullException.ThrowIfNull(node);
        await EnsureSchemaInitializedAsync(context.SchemaRegistry, cancellationToken).ConfigureAwait(false);

        // For DynamicNode, use the explicitly set Labels instead of CLR type name
        List<string> schemaLabels;
        if (node is DynamicNode dynamicNode)
        {
            schemaLabels = dynamicNode.Labels.ToList();
        }
        else
        {
            schemaLabels = [Labels.GetLabelFromType(node.GetType())];
        }

        foreach (var schemaLabel in schemaLabels)
        {
            if (context.SchemaRegistry.GetNodeSchema(schemaLabel) is not { } schema)
                continue;

            if (node is DynamicNode dynNode)
                ValidateDynamicPropertyRules(dynNode.Properties, schema, schemaLabel);
            else
                ValidatePropertyRules(node, schema, schemaLabel);

            if (schema.HasCompositeKey() || schema.Properties.Values.Any(p => p.IsUnique))
            {
                var nodeType = node.GetType();
                await ValidateUniqueConstraintsAsync(
                        entityId: node.Id,
                        entity: node,
                        schema: schema,
                        context: context,
                        transaction: transaction,
                        alias: "n",
                        labelForMatch: Labels.GetBaseTypeLabel(nodeType),
                        entityDisplayName: schema.Label,
                        isRelationship: false,
                        isUpdate: isUpdate,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public static async Task ValidateRelationshipAsync<TRelationship>(
        TRelationship relationship,
        AgeGraphContext context,
        AgeGraphTransaction transaction,
        bool isUpdate,
        CancellationToken cancellationToken)
        where TRelationship : IRelationship
    {
        ArgumentNullException.ThrowIfNull(relationship);
        await EnsureSchemaInitializedAsync(context.SchemaRegistry, cancellationToken).ConfigureAwait(false);

        // For DynamicRelationship, use the explicitly set Type instead of CLR type name
        List<string> schemaLabels;
        if (relationship is DynamicRelationship dynamicRel)
        {
            schemaLabels = [dynamicRel.Type];
        }
        else
        {
            schemaLabels = [Labels.GetLabelFromType(relationship.GetType())];
        }

        foreach (var schemaLabel in schemaLabels)
        {
            if (context.SchemaRegistry.GetRelationshipSchema(schemaLabel) is not { } schema)
                continue;

            if (relationship is DynamicRelationship dynRel)
                ValidateDynamicPropertyRules(dynRel.Properties, schema, schemaLabel);
            else
                ValidatePropertyRules(relationship, schema, schemaLabel);

            if (schema.HasCompositeKey() || schema.Properties.Values.Any(p => p.IsUnique))
            {
                var relType = relationship.GetType();
                await ValidateUniqueConstraintsAsync(
                        entityId: relationship.Id,
                        entity: relationship,
                        schema: schema,
                        context: context,
                        transaction: transaction,
                        alias: "r",
                        labelForMatch: Labels.GetBaseTypeLabel(relType),
                        entityDisplayName: schema.Label,
                        isRelationship: true,
                        isUpdate: isUpdate,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task EnsureSchemaInitializedAsync(SchemaRegistry registry, CancellationToken cancellationToken)
    {
        if (!registry.IsInitialized)
        {
            await registry.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidatePropertyRules(object entity, EntitySchemaInfo schema, string entityDisplayName)
    {
        foreach (var propertySchema in schema.Properties.Values)
        {
            if (propertySchema.Ignore)
            {
                continue;
            }

            var propertyInfo = propertySchema.PropertyInfo;

            // Safety check: the PropertyInfo must be usable on this entity type.
            // SchemaRegistry may have cached PropertyInfo from a different type
            // with the same schema label (e.g., multiple TestNode classes in tests).
            if (propertyInfo.DeclaringType != null && !propertyInfo.DeclaringType.IsAssignableFrom(entity.GetType()))
            {
                continue;
            }

            var value = propertyInfo.GetValue(entity);
            var propertyDisplayName = propertyInfo.Name;

            if (propertySchema.IsRequired)
            {
                if (value is null || (value is string stringValue && string.IsNullOrWhiteSpace(stringValue)))
                {
                    throw new GraphException($"Property '{propertyDisplayName}' on {entityDisplayName} is required and cannot be null or empty.");
                }
            }

            if (value is null)
            {
                continue;
            }

            var validation = propertySchema.Validation;
            if (validation.MinLength is null && validation.MaxLength is null && string.IsNullOrWhiteSpace(validation.Pattern)
                && validation.MinValue is null && validation.MaxValue is null)
            {
                continue;
            }

            ValidatePropertyValue(propertyDisplayName, value, validation, entityDisplayName);
        }
    }

    private static void ValidateDynamicPropertyRules(
        IReadOnlyDictionary<string, object?> properties,
        EntitySchemaInfo schema,
        string entityDisplayName)
    {
        // First pass: check all required properties from schema are present.
        // Dynamic entities use attribute Label values (e.g., "requiredString") as property keys,
        // NOT C# property names (e.g., "RequiredString"). Match against PropertySchemaInfo.Name
        // which contains the graph property name (attribute Label or C# fallback).
        foreach (var propertySchema in schema.Properties.Values)
        {
            if (propertySchema.Ignore)
                continue;

            var graphName = propertySchema.Name;
            var exists = properties.TryGetValue(graphName, out var value);

            if (propertySchema.IsRequired)
            {
                if (!exists || value is null || (value is string stringValue && string.IsNullOrWhiteSpace(stringValue)))
                {
                    // For required properties not provided, check if there's a CLR default value
                    if (!exists)
                    {
                        var propInfo = propertySchema.PropertyInfo;
                        if (propInfo != null && propInfo.DeclaringType != null)
                        {
                            try
                            {
                                var defaultInstance = Activator.CreateInstance(propInfo.DeclaringType);
                                var defaultValue = propInfo.GetValue(defaultInstance);
                                if (defaultValue != null && (defaultValue is not string ds || !string.IsNullOrWhiteSpace(ds)))
                                    continue;
                            }
                            catch { }
                        }
                    }

                    throw new GraphException($"Property '{graphName}' on {entityDisplayName} is required but not provided.");
                }
            }

            if (!exists || value is null)
                continue;

            // Validate enum values: if the CLR property type is an enum and the
            // dynamic value is a string, check it parses to a valid enum member.
            var enumPropInfo = propertySchema.PropertyInfo;
            if (enumPropInfo != null && enumPropInfo.PropertyType.IsEnum && value is string enumStr)
            {
                try
                {
                    var _ = Enum.Parse(enumPropInfo.PropertyType, enumStr, ignoreCase: false);
                }
                catch
                {
                    throw new GraphException($"Property '{graphName}' on {entityDisplayName} has value '{enumStr}' which is not a valid {enumPropInfo.PropertyType.Name} enum value.");
                }
            }

            var validation = propertySchema.Validation;
            if (validation.MinLength is null && validation.MaxLength is null && string.IsNullOrWhiteSpace(validation.Pattern)
                && validation.MinValue is null && validation.MaxValue is null)
                continue;

            ValidatePropertyValue(graphName, value, validation, entityDisplayName);
        }

        // Second pass: check for extra/unknown properties.
        // Dynamic entities use attribute Label values, so match only against
        // PropertySchemaInfo.Name (the graph property name), NOT C# property names.
        // This means "Note" (PascalCase) is correctly rejected when the Label is "note".
        foreach (var (propName, _) in properties)
        {
            if (propName == nameof(INode.Labels) || propName == "user_id" ||
                propName == nameof(IRelationship.StartNodeId) ||
                propName == nameof(IRelationship.EndNodeId) ||
                propName == nameof(IRelationship.Type))
                continue;

            var isKnown = schema.Properties.Values.Any(p =>
                string.Equals(p.Name, propName, StringComparison.Ordinal));

            if (!isKnown)
            {
                throw new GraphException($"Property '{propName}' is not defined in the schema for {entityDisplayName}.");
            }
        }
    }

    private static void ValidatePropertyValue(string propertyName, object value, PropertyValidation validation, string entityDisplayName)
    {
        if (validation.MinLength is int minLength && value is string stringValue && stringValue.Length < minLength)
        {
            throw new GraphException($"Property '{propertyName}' on {entityDisplayName} must have a minimum length of {minLength}. Current length: {stringValue.Length}.");
        }

        if (validation.MaxLength is int maxLength && value is string stringValueMax && stringValueMax.Length > maxLength)
        {
            throw new GraphException($"Property '{propertyName}' on {entityDisplayName} must have a maximum length of {maxLength}. Current length: {stringValueMax.Length}.");
        }

        if (!string.IsNullOrEmpty(validation.Pattern) && value is string patternValue)
        {
            if (!Regex.IsMatch(patternValue, validation.Pattern))
            {
                throw new GraphException($"Property '{propertyName}' on {entityDisplayName} must match the pattern '{validation.Pattern}'. Current value: {patternValue}");
            }
        }
    }

    private static async Task ValidateUniqueConstraintsAsync(
        string entityId,
        object entity,
        EntitySchemaInfo schema,
        AgeGraphContext context,
        AgeGraphTransaction transaction,
        string alias,
        string labelForMatch,
        string entityDisplayName,
        bool isRelationship,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        await ValidateCompositeKeyAsync(entityId, entity, schema, context, transaction, alias, labelForMatch, entityDisplayName, isRelationship, isUpdate, cancellationToken).ConfigureAwait(false);
        await ValidateUniquePropertiesAsync(entityId, entity, schema, context, transaction, alias, labelForMatch, entityDisplayName, isRelationship, isUpdate, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateCompositeKeyAsync(
        string entityId,
        object entity,
        EntitySchemaInfo schema,
        AgeGraphContext context,
        AgeGraphTransaction transaction,
        string alias,
        string labelForMatch,
        string entityDisplayName,
        bool isRelationship,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        if (!schema.HasCompositeKey())
        {
            return;
        }

        var keyProperties = schema.GetKeyProperties().Where(p => !p.Ignore).ToList();
        if (keyProperties.Count == 0)
        {
            return;
        }

        var parameters = new Dictionary<string, object?>();
        var whereParts = new List<string>();
        var parameterIndex = 0;

        foreach (var keyProperty in keyProperties)
        {
            var value = keyProperty.PropertyInfo.GetValue(entity);
            if (value is null || (value is string stringValue && string.IsNullOrWhiteSpace(stringValue)))
            {
                throw new GraphException($"Composite key property '{keyProperty.PropertyInfo.Name}' on {entityDisplayName} is required and cannot be null or empty.");
            }

            var parameterName = $"p{parameterIndex++}";
            whereParts.Add($"{GetQualifiedProperty(alias, keyProperty.Name)} = ${parameterName}");
            parameters[parameterName] = AgeSerializationBridge.ToAgeValue(value);
        }

        if (isUpdate)
        {
            parameters["currentId"] = entityId;
            whereParts.Add($"{GetQualifiedIdProperty(alias)} <> $currentId");
        }

        var whereClause = string.Join(" AND ", whereParts);
        var logger = context.LoggerFactory.CreateLogger(nameof(AgeEntityAttributeValidator));
        logger.LogDebug(
            "Validating composite key for {EntityDisplayName} with WHERE {WhereClause} and parameters {Parameters}",
            entityDisplayName,
            whereClause,
            string.Join(", ", parameters.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        var conflict = await HasConflictAsync(context, transaction, alias, labelForMatch, isRelationship, whereClause, parameters, cancellationToken).ConfigureAwait(false);
        if (conflict)
        {
            await LogConflictDetailsAsync(context, alias, labelForMatch, isRelationship, whereClause, parameters, cancellationToken).ConfigureAwait(false);
            var keyList = string.Join(", ", keyProperties.Select(p => p.PropertyInfo.Name));
            throw new GraphException($"Composite key ({keyList}) on {entityDisplayName} must be unique.");
        }
    }

    private static async Task ValidateUniquePropertiesAsync(
        string entityId,
        object entity,
        EntitySchemaInfo schema,
        AgeGraphContext context,
        AgeGraphTransaction transaction,
        string alias,
        string labelForMatch,
        string entityDisplayName,
        bool isRelationship,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        foreach (var propertySchema in schema.Properties.Values)
        {
            if (propertySchema.Ignore)
            {
                continue;
            }

            if (!propertySchema.IsUnique)
            {
                continue;
            }

            if (schema.HasCompositeKey() && propertySchema.IsKey)
            {
                // Composite keys are validated as a group, skip individual checks.
                continue;
            }

            var value = propertySchema.PropertyInfo.GetValue(entity);
            if (value is null)
            {
                continue;
            }

            var parameters = new Dictionary<string, object?>
            {
                ["value"] = AgeSerializationBridge.ToAgeValue(value)
            };

            var whereClause = $"{GetQualifiedProperty(alias, propertySchema.Name)} = $value";

            if (isUpdate)
            {
                parameters["currentId"] = entityId;
                whereClause = $"{whereClause} AND {GetQualifiedIdProperty(alias)} <> $currentId";
            }

            var conflict = await HasConflictAsync(context, transaction, alias, labelForMatch, isRelationship, whereClause, parameters, cancellationToken).ConfigureAwait(false);
            if (conflict)
            {
                var valueString = value switch
                {
                    string stringValue => stringValue,
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty
                };

                throw new GraphException($"Property '{propertySchema.PropertyInfo.Name}' on {entityDisplayName} must be unique. The value '{valueString}' already exists.");
            }
        }
    }

    private static async Task<bool> HasConflictAsync(
        AgeGraphContext context,
        AgeGraphTransaction transaction,
        string alias,
        string labelForMatch,
        bool isRelationship,
        string whereClause,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(whereClause))
        {
            return false;
        }

        var escapedLabel = EscapeLabel(labelForMatch);
        var matchPattern = isRelationship
            ? $"()-[{alias}:{escapedLabel}]-()"
            : $"({alias}:{escapedLabel})";

        var cypher = $"MATCH {matchPattern} WHERE {whereClause} RETURN 1 LIMIT 1";
        await using var command = context.Connection.CreateCypherCommand(context.GraphName, cypher, new Dictionary<string, object?>(parameters));
        command.Transaction = transaction?.Transaction;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return !reader.IsDBNull(0);
    }

    private static async Task LogConflictDetailsAsync(
        AgeGraphContext context,
        string alias,
        string labelForMatch,
        bool isRelationship,
        string whereClause,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var logger = context.LoggerFactory.CreateLogger(nameof(AgeEntityAttributeValidator));

        try
        {
            var escapedLabel = EscapeLabel(labelForMatch);
            var matchPattern = isRelationship
                ? $"()-[{alias}:{escapedLabel}]-()"
                : $"({alias}:{escapedLabel})";

            var cypher = $"MATCH {matchPattern} WHERE {whereClause} RETURN {alias} LIMIT 3";
            await using var command = context.Connection.CreateCypherCommand(context.GraphName, cypher, new Dictionary<string, object?>(parameters));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var conflicts = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var agtype = reader.GetFieldValue<Agtype>(0);
                    conflicts.Add(FormatAgtype(agtype));
                }
            }

            if (conflicts.Count == 0)
            {
                logger.LogWarning("Composite key conflict reported but no matching entities were found for logging.");
            }
            else
            {
                logger.LogWarning("Composite key conflict details: {Conflicts}", string.Join(" | ", conflicts));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to log composite key conflict details.");
        }
    }

    private static string FormatAgtype(Agtype agtype)
    {
        if (agtype.IsEdge)
        {
            var edge = agtype.GetEdge();
            var builder = new StringBuilder();
            builder.Append("Edge(");
            builder.Append("Id=").Append(edge.Id);
            builder.Append(", Label=").Append(edge.Label);
            builder.Append(", Start=").Append(edge.StartId);
            builder.Append(", End=").Append(edge.EndId);
            builder.Append(", Properties={");

            var first = true;
            foreach (var (key, value) in edge.Properties)
            {
                if (!first)
                {
                    builder.Append(", ");
                }
                first = false;
                builder.Append(key).Append('=').Append(FormatValue(value));
            }

            builder.Append("})");
            return builder.ToString();
        }

        if (agtype.IsVertex)
        {
            var vertex = agtype.GetVertex();
            var builder = new StringBuilder();
            builder.Append("Vertex(");
            builder.Append("Id=").Append(vertex.Id);
            builder.Append(", Label=").Append(vertex.Label);
            builder.Append(", Properties={");

            var first = true;
            foreach (var (key, value) in vertex.Properties)
            {
                if (!first)
                {
                    builder.Append(", ");
                }
                first = false;
                builder.Append(key).Append('=').Append(FormatValue(value));
            }

            builder.Append("})");
            return builder.ToString();
        }

        return agtype.ToString() ?? "<null>";
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            string s => s,
            Agtype nested => FormatAgtype(nested),
            IEnumerable<object?> list => $"[{string.Join(", ", list.Select(FormatValue))}]",
            IEnumerable enumerable => $"[{string.Join(", ", enumerable.Cast<object?>().Select(FormatValue))}]",
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string GetQualifiedProperty(string alias, string propertyGraphName)
    {
        var mapped = MapPropertyName(propertyGraphName);
        return $"{alias}.`{EscapePropertyName(mapped)}`";
    }

    private static string GetQualifiedIdProperty(string alias)
    {
        var mapped = MapPropertyName(nameof(IEntity.Id));
        return $"{alias}.`{EscapePropertyName(mapped)}`";
    }

    // MapPropertyName is imported via `using static ExpressionTranslationHelper`

    private static string EscapeLabel(string label) => $"`{label.Replace("`", "``")}`";

    private static string EscapePropertyName(string propertyName) => propertyName.Replace("`", "``");
}
