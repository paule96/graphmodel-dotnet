---
title: "Test Coverage Analysis & Improvement Plan"
description: "Analysis of code coverage gaps in the AGE Graph Model provider and prioritized recommendations for test improvements"
ms.date: 2026-06-02
---

# Test Coverage Analysis & Improvement Plan

## Overview

Coverage data was collected from the existing **347 test suite** (2 pre-existing failures, 9 skipped) using `dotnet-test` with cobertura format. The analysis focuses on `Cvoya.Graph.Model.Age` — the primary refactored provider.

**Overall coverage for AGE provider**: ~68% line coverage average across all files.

## Files with 0% Coverage (No Tests At All)

These files are completely untested. Some are infrastructure/queryables that are exercised indirectly through integration tests, but may lack direct unit coverage.

| File | Type | Risk |
|------|------|------|
| `AgeGraphQueryProvider.cs` | LINQ provider | Medium — critical path for query execution |
| `AgeGraphNodeQueryable.cs` | LINQ queryable | Low — thin wrapper, mostly boilerplate |
| `AgeGraphRelationshipQueryable.cs` | LINQ queryable | Low — thin wrapper |
| `GraphSearchHelper.cs` | Search facade | **High** — extracted from AgeGraph, contains full-text search logic |
| `EntityInfoBuilder.cs` | Entity builder | **High** — all JSON/collection conversion logic |
| `AgeValueConverters.cs` | Value conversion | **High** — converted values from AGE types |
| `FragmentSequenceInsights.cs` | Fragment analysis | Low — diagnostic helper |
| `TraversalInfo.cs` | Traversal model | Low — data holder |

## Files with Critically Low Coverage (<50%)

| File | Coverage | Lines | Risk |
|------|----------|-------|------|
| `LabelsExtractor.cs` | 43% | 23 | Low — simple extraction, but inheritance edge cases untested |
| `CollectExpressionTranslator.cs` | 48% | ~200 | **High** — complex translation logic shared across visitors |
| `AgeGraphStore.cs` | 47% | 36 | Medium — graph store operations |
| `GraphOperationHelper.cs` | 50% | 12 | Low — simple async wrapper |
| `AgeEntityAttributeValidator.cs` | 50% | 8 | Medium — facade delegating to validators |

## Files with Moderate Coverage (50-70%)

These have partial coverage but significant untested paths:

| File | Coverage | Total Lines | Uncovered Lines |
|------|----------|-------------|-----------------|
| `DateTimeMethodHandler.cs` | 53% | 34 | ~16 |
| `AgeExpressionToCypherVisitor.cs` | 55% | 150 | ~68 |
| `FragmentEmittingVisitorBase.cs` | 56% | 25 | ~11 |
| `MathMethodHandler.cs` | 60% | 67 | ~27 |
| `CypherQueryHelper.cs` | 60% | 20 | ~8 |
| `AggregationFragmentVisitor.cs` | 60% | 63 | ~25 |
| `CollectionExpressionHandler.cs` | 61% | 83 | ~32 |
| `AgeResultProcessor.cs` | 63% | 31 | ~11 |
| `ExpressionTranslationHelper.cs` | 66% | 32 | ~11 |
| `AgeGraph.cs` | 67% | 100 | ~33 |
| `ClosureCaptureHandler.cs` | 68% | 148 | ~47 |

## Files with Good Coverage (>80-100%)

| File | Coverage |
|------|----------|
| `AgeCypherQueryVisitor.cs` | 85% |
| `JoinHandler.cs` | 87% |
| `TraversalFragmentVisitor.cs` | 88% |
| `FilteringFragmentVisitor.cs` | 89% |
| `LabelsExtractor.cs` | 94% (partial variant) |
| `AgeFragmentRenderer.cs` | 91% |
| `AgeGraphTransaction.cs` (partial) | 97% |
| `CypherQueryScope.cs` | 100% |
| `ColumnDefinitionBuilder.cs` | 100% |

## Priority Test Improvement Plan

### P1 — Critical: Zero-Coverage Files with High Risk

1. **`GraphSearchHelper`** — Write unit tests for:
   - `SearchAsync()` with valid query
   - `SearchNodesAsync<T>()` with typed parameter
   - `SearchRelationshipsAsync<T>()` with typed parameter
   - Null/empty query validation path

2. **`EntityInfoBuilder`** — Write unit tests for:
   - `CreateEntityInfoFromDictionary()` with nested objects
   - `ConvertJsonElementToDictionary()` with various JSON structures
   - `ParsePointFromJson()` with lon/lat, x/y, and array formats
   - `CreateSimpleProperty()` null vs non-null values
   - `IsComplexCollectionType()` and `DetermineElementType()`

3. **`AgeValueConverters`** — Write unit tests for:
   - `ConvertDictionaryToType()` round-trip
   - `ConvertScalarAgtype()` with various target types
   - `ConvertJsonElementToEntityInfo()`

### P2 — Moderate Coverage: Complex Logic Paths

4. **`CollectExpressionTranslator`** (48%) — Write tests for:
   - `TranslateInnerSelectBody()` with anonymous types
   - `TranslateInnerExpression()` with path segment chains
   - `WalkPathSegmentChain()` with various depths
   - `TranslateInnerMethodCall()` with DateTime arithmetic
   - Binary expression operators

5. **`AgeExpressionToCypherVisitor`** (55%) — Add tests for:
   - `VisitBinary()` null comparisons
   - `VisitMember()` path segment nested access
   - `VisitNew()` anonymous type projections
   - `HandleToListOnNestedSelect()` group collect patterns
   - `VisitConditional()` and `VisitUnary()`

6. **`ClosureCaptureHandler`** (68%) — Add tests for:
   - `TryHandleClosureCountOnRelationship()` with captured collections
   - Instance vs static Count forms
   - Direction detection (outgoing, incoming, both)
   - Empty collection edge cases

### P3 — Lower Priority: Simple Coverage Gaps

7. **`DateTimeMethodHandler`** (53%) — Add edge case tests for date formats
8. **`MathMethodHandler`** (60%) — Add remaining math method variations
9. **`CypherQueryHelper`** (60%) — Test `HasConflictAsync()` query building
10. **`AgeGraph.cs`** (67%) — Test `DisposeAsync()` and transaction cleanup

## Test Infrastructure Notes

- Tests require a running PostgreSQL instance with Apache AGE extension
- The test project (`tests/Graph.Model.Age.Tests/`) has 18+ test files
- 9 tests are skipped (likely due to missing infrastructure or known limitations)
- 2 tests fail consistently (documented in `docs/pattern-comprehension-limitations.md`)

## Suggested Next Steps

1. Add `GraphSearchHelperTests.cs` and `EntityInfoBuilderTests.cs` for P1 coverage
2. Extract shared test helpers for AGE value construction (reused across many tests)
3. Consider introducing a mock/fake `AgeGraphContext` for unit tests that don't need PostgreSQL
4. Create parametrized theory tests for `CollectExpressionTranslator` (many input variants)
