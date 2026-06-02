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

namespace Cvoya.Graph.Model.Age.Core;

using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Age.Core.Internal;
using Cvoya.Graph.Model.Age.Querying.Linq.Providers;
using Cvoya.Graph.Model.Age.Querying.Linq.Queryables;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles full-text search operations for AGE graphs.
/// </summary>
internal static class GraphSearchHelper
{
    public static async Task<IGraphQueryable<IEntity>> SearchAsync(
        string query, AgeGraphContext graphContext, IGraphTransaction? transaction, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));
        return await GraphOperationHelper.ExecuteAsync(
            logger,
            $"Failed to create search queryable for query: {query}",
            async () =>
            {
                logger.LogDebug("Performing AGE full text search on all entities with query: {Query}", query);

                AgeGraphTransaction? ageTx = transaction != null
                    ? await TransactionHelpers.GetOrCreateTransactionAsync(graphContext, transaction, true)
                    : null;

                var provider = new AgeGraphQueryProvider(graphContext, ageTx);
                var searchExpression = new AgeFullTextSearchExpression(query, typeof(IEntity));
                return (IGraphQueryable<IEntity>)new AgeGraphQueryable<IEntity>(provider, graphContext, searchExpression);
            }).ConfigureAwait(false);
    }

    public static async Task<IGraphNodeQueryable<INode>> SearchNodesAsync(
        string query, AgeGraphContext graphContext, IGraphTransaction? transaction, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));
        return await GraphOperationHelper.ExecuteAsync(
            logger,
            $"Failed to create node search queryable for query: {query}",
            async () =>
            {
                logger.LogDebug("Performing AGE full text search on nodes with query: {Query}", query);

                AgeGraphTransaction? ageTx = transaction != null
                    ? await TransactionHelpers.GetOrCreateTransactionAsync(graphContext, transaction, true)
                    : null;

                var provider = new AgeGraphQueryProvider(graphContext, ageTx);
                var searchExpression = new AgeFullTextSearchExpression(query, typeof(INode));
                return (IGraphNodeQueryable<INode>)new AgeGraphNodeQueryable<INode>(provider, graphContext, searchExpression);
            }).ConfigureAwait(false);
    }

    public static async Task<IGraphRelationshipQueryable<IRelationship>> SearchRelationshipsAsync(
        string query, AgeGraphContext graphContext, IGraphTransaction? transaction, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));
        return await GraphOperationHelper.ExecuteAsync(
            logger,
            $"Failed to create relationship search queryable for query: {query}",
            async () =>
            {
                logger.LogDebug("Performing AGE full text search on relationships with query: {Query}", query);

                AgeGraphTransaction? ageTx = transaction != null
                    ? await TransactionHelpers.GetOrCreateTransactionAsync(graphContext, transaction, true)
                    : null;

                var provider = new AgeGraphQueryProvider(graphContext, ageTx);
                var searchExpression = new AgeFullTextSearchExpression(query, typeof(IRelationship));
                return (IGraphRelationshipQueryable<IRelationship>)new AgeGraphRelationshipQueryable<IRelationship>(provider, graphContext, searchExpression);
            }).ConfigureAwait(false);
    }

    public static async Task<IGraphNodeQueryable<T>> SearchNodesAsync<T>(
        string query, AgeGraphContext graphContext, IGraphTransaction? transaction, ILogger logger)
        where T : INode
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));
        return await GraphOperationHelper.ExecuteAsync(
            logger,
            $"Failed to create typed node search queryable for type {typeof(T).Name} and query: {query}",
            async () =>
            {
                logger.LogDebug("Performing AGE full text search on nodes of type {NodeType} with query: {Query}", typeof(T).Name, query);

                AgeGraphTransaction? ageTx = transaction != null
                    ? await TransactionHelpers.GetOrCreateTransactionAsync(graphContext, transaction, true)
                    : null;

                var provider = new AgeGraphQueryProvider(graphContext, ageTx);
                var searchExpression = new AgeFullTextSearchExpression(query, typeof(T));
                return (IGraphNodeQueryable<T>)new AgeGraphNodeQueryable<T>(provider, graphContext, searchExpression);
            }).ConfigureAwait(false);
    }

    public static async Task<IGraphRelationshipQueryable<T>> SearchRelationshipsAsync<T>(
        string query, AgeGraphContext graphContext, IGraphTransaction? transaction, ILogger logger)
        where T : IRelationship
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));
        return await GraphOperationHelper.ExecuteAsync(
            logger,
            $"Failed to create typed relationship search queryable for type {typeof(T).Name} and query: {query}",
            async () =>
            {
                logger.LogDebug("Performing AGE full text search on relationships of type {RelType} with query: {Query}", typeof(T).Name, query);

                AgeGraphTransaction? ageTx = transaction != null
                    ? await TransactionHelpers.GetOrCreateTransactionAsync(graphContext, transaction, true)
                    : null;

                var provider = new AgeGraphQueryProvider(graphContext, ageTx);
                var searchExpression = new AgeFullTextSearchExpression(query, typeof(T));
                return (IGraphRelationshipQueryable<T>)new AgeGraphRelationshipQueryable<T>(provider, graphContext, searchExpression);
            }).ConfigureAwait(false);
    }
}
