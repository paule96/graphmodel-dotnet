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

using Cvoya.Graph.Model;
using Cvoya.Graph.Model.Cypher.Querying.Cypher.Builders;

/// <summary>
/// Tracks alias and multi-hop traversal state while building Cypher queries for AGE.
/// </summary>
internal sealed class CypherQueryScope(Type rootType) : ICypherQueryScope
{
    private readonly Dictionary<int, (string src, string rel, string tgt)> hopAliases = [];
    private readonly Dictionary<int, (Type src, Type rel, Type tgt)> hopTypes = [];

    public Type RootType { get; } = rootType;
    public string? CurrentAlias { get; set; }
    public int? TraversalMinDepth { get; private set; }
    public int? TraversalMaxDepth { get; private set; }
    public GraphTraversalDirection? TraversalDirection { get; private set; }
    public string? LastProjectedExpression { get; set; }
    public int LastPathSegmentHop { get; set; } = -1;
    public int CurrentHop { get; private set; } = 0;

    public void SetTraversalDepth(int minDepth, int maxDepth)
    {
        TraversalMinDepth = minDepth;
        TraversalMaxDepth = maxDepth;
    }

    public void ClearTraversalDepth()
    {
        TraversalMinDepth = null;
        TraversalMaxDepth = null;
    }

    public void SetTraversalDirection(GraphTraversalDirection direction)
        => TraversalDirection = direction;

    public void ClearTraversalDirection()
        => TraversalDirection = null;

    public void PushAlias(string alias) { /* AGE manages CurrentAlias directly */ }
    public void PopAlias() { }
    public bool IsInPathSegmentContext() => false;

    public void AdvanceHop() { CurrentHop++; }

    public string GetNumberedAlias(string baseAlias) => $"{baseAlias}{CurrentHop}";

    public string GetNumberedAliasForHop(string baseAlias, int hopNumber) => $"{baseAlias}{hopNumber}";

    public void StoreHopAliases(int hopNumber, string sourceAlias, string relationshipAlias, string targetAlias)
    {
        hopAliases[hopNumber] = (sourceAlias, relationshipAlias, targetAlias);
    }

    public (string src, string rel, string tgt)? GetHopAliases(int hopNumber)
    {
        return hopAliases.TryGetValue(hopNumber, out var aliases) ? aliases : null;
    }

    public void StoreHopTypes(int hopNumber, Type sourceType, Type relationshipType, Type targetType)
    {
        hopTypes[hopNumber] = (sourceType, relationshipType, targetType);
    }

    public (Type src, Type rel, Type tgt)? GetHopTypes(int hopNumber)
    {
        return hopTypes.TryGetValue(hopNumber, out var types) ? types : null;
    }
}
