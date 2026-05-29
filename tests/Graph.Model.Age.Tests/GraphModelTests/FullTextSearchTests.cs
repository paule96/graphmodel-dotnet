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

using Cvoya.Graph.Model.Tests;

namespace Cvoya.Graph.Model.Age.Tests.GraphModelTests;

public class FullTextSearchTests(TestInfrastructureFixture fixture) :
    AgeTest(fixture),
    IFullTextSearchTests
{
    // ---------------------------------------------------------------
    // Tests skipped due to Apache AGE Cypher FTS limitations.
    // See: docs/age-fulltext-search-limitations.md
    // ---------------------------------------------------------------

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "Label-less MATCH for IRelationship picks up relationships from unrelated types. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task CanSearchAllEntitiesWithFullTextSearch()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "Label-less edge MATCH for IRelationship returns too many results. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task CanSearchRelationshipsWithGenericInterface()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "FTS in path segments chain cannot use coalesce() with =~ operator. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task CanSearchInPathSegmentsChain()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "DynamicRelationship FTS with IRelationship interface uses label-less MATCH. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task CanSearchDynamicRelationshipWithFullTextSearch()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "DynamicNode has Properties dictionary, not CLR string properties. "
               + "Searching across dict requires coalesce() which is incompatible with =~. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task CanSearchDynamicNodeWithFullTextSearch()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "INode has no concrete string properties; requires coalesce() which fails with =~. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task CanSearchNodesWithGenericInterface()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "AGE Cypher lacks full-text search indexes. "
               + "Inheritance search requires coalesce() across derived types, incompatible with =~. "
               + "See docs/age-fulltext-search-limitations.md")]
    public new async Task SearchWorksWithInheritance()
    {
        await Task.CompletedTask;
    }
}
