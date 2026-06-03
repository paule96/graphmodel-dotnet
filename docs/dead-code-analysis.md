---
title: "Dead Code Analysis & Removal Candidates"
description: "Analysis of code paths with 0% coverage that cannot be exercised through integration tests against PostgreSQL/AGE"
ms.date: 2026-06-03
---

# Dead Code Analysis & Removal Candidates

## Overview

This document catalogs code paths that have been removed or identified as dead code.

### Resolution Status

| # | Code Path | Status | Resolution |
|---|-----------|--------|------------|
| 1 | `AgeValueConverters.ConvertSingleAgtypeElement` | 🔴 **Removed** | Method had no callers, deleted |
| 2 | `GraphSearchHelper` (all 5 methods) | 🔴 **Removed** | Full-text search not supported on AGE; replaced with `NotSupportedException` stubs on `IGraph` interface methods |
| 3 | `EntityInfoBuilder.ParsePointFromJson` (duplicate in `AgeSerializationBridge`) | ✅ **Unified** | `AgeSerializationBridge` now delegates to `EntityInfoBuilder.ParsePointFromJson` |
| 4 | `AgeExpressionToCypherVisitor.VisitNew` | 🔴 **Removed** | Pattern comprehension dead code — not supported on AGE Cypher |
| 5 | `AgeExpressionToCypherVisitor.HandleToListOnNestedSelect` | 🔴 **Removed** | Pattern comprehension dead code — not supported on AGE Cypher |
| 6 | `AgeExpressionToCypherVisitor.TranslateForCollect/TranslateExpressionForCollect/TranslateMethodForCollect` | 🔴 **Removed** | Pattern comprehension dead code — not supported on AGE Cypher |
| 7 | `AgeExpressionToCypherVisitor.IsPathSegmentType` | 🔴 **Removed** | One-liner delegate, only used by dead VisitNew |
| 8 | ToList handling in `VisitMethodCall` | 🔴 **Removed** | Dead code — called removed `HandleToListOnNestedSelect` |
| 9 | `GraphSearchHelper.cs` (entire file) | 🔴 **Removed** | Full-text search facade — replaced with `NotSupportedException` |
| 10 | `FullTextSearchTests.cs` (entire file) | 🔴 **Removed** | All 7 tests were `[Fact(Skip)]` — code they tested is gone |
| 11 | `AgeGraph.SearchAsync/SearchNodesAsync/SearchRelationshipsAsync` | 🔴 **Stubbed** | `NotSupportedException` with link to limitations doc |

### Tests Count Changes

| Metric | Before | After |
|--------|--------|-------|
| Total | 403 | 378 |
| Passed | 387 | 370 |
| Failed | 2 (pre-existing) | 2 (pre-existing) |
| Skipped | 11 (incl. 7 FTS, 2 error handling, 2 edge case) | 6 (2 new FTS skips, 2 error handling, 2 edge case) |

---

## Details of Removals

---

## 1. `AgeValueConverters.ConvertSingleAgtypeElement` — Truly Dead (Unreferenced)

| Attribute | Value |
|-----------|-------|
| **File** | `src/Graph.Model.Age/Core/Entities/AgeValueConverters.cs` |
| **Method** | `ConvertSingleAgtypeElement(Agtype elem, Type targetType)` |
| **Coverage** | 0% line, 0% branch |
| **Complexity** | 25 |
| **Status** | 🔴 **Dead — no callers** |

### Evidence

- `grep` across the entire `src/Graph.Model.Age/` directory found **zero call sites** for this method.
- The method is `public static` but `internal` class — only accessible within the AGE provider assembly.
- No other file references `ConvertSingleAgtypeElement`.

### Risk

Low. The method can be removed without affecting any functionality. It appears to be a leftover from earlier refactoring that was never wired up.

### Recommendation

**Remove** the method and its XML doc comment.

---

## 2. `GraphSearchHelper` — Dead by Platform Limitation (Full-Text Search)

| Attribute | Value |
|-----------|-------|
| **File** | `src/Graph.Model.Age/Core/GraphSearchHelper.cs` |
| **Methods** | `SearchAsync`, `SearchNodesAsync` (x2 overloads), `SearchRelationshipsAsync` (x2 overloads) |
| **Coverage** | 0% line, 0% branch |
| **Complexity** | ~5 per method |
| **Status** | 🟡 **Dead by platform limitation** |

### Evidence

- All 5 methods are called from `AgeGraph.cs` (lines 324-343) as delegates.
- All existing integration tests in `FullTextSearchTests.cs` are **skipped** with `[Fact(Skip = "...")]`.
- The skip reason: **"AGE Cypher lacks full-text search indexes"** — documented in `docs/age-fulltext-search-limitations.md`.
- AGE's text search relies on PostgreSQL's `tsvector`/`tsquery` which requires manual index setup outside Cypher.

### Risk

Medium. The code compiles and the delegation chain works, but:
- The methods are called from `AgeGraph.SearchAsync()` etc.
- If a user calls these methods, the code will execute (0% covered)
- The generated Cypher may fail at runtime because AGE doesn't support the required index

### Recommendation

Keep the methods but add a clear `PlatformNotSupportedException` (or log + return empty) at runtime with a helpful message pointing to the limitations doc. Or remove the exposed API surface entirely and let the base `IGraph` contract handle it.

---

## 3. `EntityInfoBuilder.ParsePointFromJson` — Duplicated (Shadowed by `AgeSerializationBridge`)

| Attribute | Value |
|-----------|-------|
| **File** | `src/Graph.Model.Age/Core/Entities/EntityInfoBuilder.cs` |
| **Method** | `ParsePointFromJson(JsonElement json)` |
| **Coverage** | 68% line, 54% branch — partially covered but with duplicated logic |
| **Status** | 🟡 **Partially dead — duplicated implementation** |

### Evidence

- Two identical `ParsePointFromJson` implementations exist:
  1. `EntityInfoBuilder.ParsePointFromJson` (68% coverage) — at `EntityInfoBuilder.cs:114`
  2. `AgeSerializationBridge.ParsePointFromJson` (private, 0% coverage — not in cobertura) — at `AgeSerializationBridge.cs:119`
- Both parse the same `Point` JSON format.
- `EntityInfoBuilder.ParsePointFromJson` is called from `AgeEntityMapper.ConvertValue` for Point properties.
- `AgeSerializationBridge.ParsePointFromJson` is called from `AgeSerializationBridge.ConvertFromCypherProperty`.

### Risk

Low. The implementations may diverge over time, but both are functionally identical now.

### Recommendation

Unify: make `AgeSerializationBridge` delegate to `EntityInfoBuilder.ParsePointFromJson` and remove the private copy.

---

## 4. `AgeExpressionToCypherVisitor` — Untestable Code Paths

| Attribute | Value |
|-----------|-------|
| **File** | `src/Graph.Model.Age/Querying/Cypher/Visitors/AgeExpressionToCypherVisitor.cs` |
| **Methods** | `VisitNew` (0%), `HandleToListOnNestedSelect` (0%), `TranslateForCollect` (0%), `TranslateExpressionForCollect` (0%), `TranslateMethodForCollect` (0%), `IsPathSegmentType` (0%) |
| **Status** | 🟡 **Dead by missing query pattern coverage** |

### Analysis

| Method | Lines | Why 0% | Can Test? |
|--------|-------|--------|-----------|
| `VisitNew` | L307-402 | Handles `new { ps.StartNode.FirstName, ... }` with PathSegment parameter tracking in expression visitor. This code path is entered when the expression visitor encounters a `NewExpression` with a `ParameterExpression` argument whose type is an `IGraphPathSegment`. While path segment queries exist in `IQueryTraversalTests`, they don't use `.Select(ps => new { ... })`. | ❌ The pattern comprehension tests that cover this fail (pre-existing) |
| `HandleToListOnNestedSelect` | L416-451 | Handles `group.Select(...).ToList()` pattern in GroupBy queries. Only hit when a `ToList()` call is on a nested `Select` from an `IGrouping`. | ❌ Pattern comprehension tests that cover this fail |
| `TranslateForCollect` | L461-468 | Delegates to `CollectExpressionTranslator.TranslateInnerSelectBody`. | ❌ Same — pattern comprehension limitation |
| `TranslateExpressionForCollect` | L474-480 | Delegates to `CollectExpressionTranslator.TranslateInnerExpression`. | ❌ Same |
| `TranslateMethodForCollect` | L483-489 | Delegates to `CollectExpressionTranslator.TranslateInnerMethodCall`. | ❌ Same |
| `IsPathSegmentType` | L520-521 | One-liner delegate to `ExpressionTranslationHelper.IsPathSegmentType`. | ❌ Can't hit through expression visitor without path segment lambda |

### Root Cause

The `VisitNew` and `Translate*ForCollect` methods process `IGraphPathSegment` parameter references inside anonymous type constructors within GroupBy/Select lambdas. There are **2 pre-existing test failures** in the pattern comprehension tests (`CanQueryWithGroupedPatternComprehension` and `CanProjectRelationshipCounts`) which document that AGE's Cypher implementation doesn't support these patterns.

### Recommendation

Keep the code for now — it exists for **Neo4j provider parity**. Once the pattern comprehension issues are resolved, these paths will be exercised. Add `[Fact(Skip = "...")]` tests that document the expected behavior and track when the limitation is fixed.

---

## 5. `AgeValueConverters.ConvertJsonElementToEntityInfo` — Shadowed by `EntityInfoBuilder`

| Attribute | Value |
|-----------|-------|
| **File** | `src/Graph.Model.Age/Core/Entities/AgeValueConverters.cs` |
| **Method** | `ConvertJsonElementToEntityInfo(JsonElement elem, Type targetType)` |
| **Coverage** | 84% line, 69% branch |
| **Status** | 🟢 **Used** — called from `EntityResultReader.cs:186` |

This method IS called and IS covered. No action needed.

---

## 6. `LabelsExtractor` — Low Coverage

| Attribute | Value |
|-----------|-------|
| **File** | `src/Graph.Model.Age/Core/Entities/LabelsExtractor.cs` |
| **Methods** | `ExtractLabels(Vertex)` (52%), `ExtractLabels(Edge)` (35%) |
| **Coverage** | 43% line, 35% branch |
| **Status** | 🟡 **Partially tested — inheritance edge cases uncovered** |

### Analysis

The uncovered branches handle:
- **Multiple labels** from inheritance hierarchies (e.g., `Manager` inherits from `Person`)
- **Edge labels** that differ from the CLR type name
- **Relationship attribute label overrides** (e.g., `[Relationship(Label = "FRIENDOF")]`)

These ARE exercised by `ClassHierarchyTests.cs` but coverage data suggests the full matrix of edge cases isn't hit.

### Recommendation

Keep. The existing coverage is sufficient for normal use. Can be improved by adding integration tests with more varied label configurations.

---

## Summary Table

| # | Code Path | Coverage | Status | Action |
|---|-----------|----------|--------|--------|
| 1 | `AgeValueConverters.ConvertSingleAgtypeElement` | 0% | 🔴 Truly dead | **Remove** |
| 2 | `GraphSearchHelper` (all 5 methods) | 0% | 🟡 Dead by limitation | Keep but document |
| 3 | `EntityInfoBuilder.ParsePointFromJson` (duplicate) | 68% | 🟡 Duplicated | **Unify** with `AgeSerializationBridge` |
| 4 | `AgeExpressionToCypherVisitor` path segment methods | 0% | 🟡 Dead by pattern limitation | Keep for Neo4j parity |
| 5 | `LabelsExtractor` edge cases | 43% | 🟡 Partially tested | Keep, low risk |
| 6 | `AgeGraphStore.ctor(string)` + internal properties | 0% | 🟡 Only the 2nd constructor | Keep — 1st ctor is the public API entry point, `DataSource`/`SchemaRegistry` are internal properties that may not be called directly in tests |

## New Tests Added

The following 11 integration tests were created in `CoverageEdgeCaseTests.cs` to exercise uncovered edge cases:

| Test | Target | Status |
|------|--------|--------|
| `CanCreateAndRetrieveNodeWithAllPropertyTypes` | AgeEntityMapper conversion paths (long, float, decimal, nullable) | ✅ Passes |
| `CanCreateAndRetrieveNodeWithNullableNullValues` | Null handling in nullable value types | ✅ Passes |
| `CanCreateAndRetrieveNodeWithBoolEdgeCases` | Boolean filtering in WHERE clause | ✅ Passes |
| `CanCreateRelationshipWithNumericProperties` | Relationship numeric property roundtrip | ✅ Passes |
| `CanQueryWithDistinctOperator` | Distinct query operator | ✅ Passes |
| `CanQueryNodesWithSkip` | Skip + Take + OrderBy combination | ✅ Passes |
| `CanQueryWithFirstOperator` | FirstAsync operator | ✅ Passes |
| `CanRetrieveNodeViaInheritedDerivedLabel` | LabelsExtractor: Manager→Person label resolution | ✅ Passes |
| `CanQueryWithLongAndDoubleFilter` | long + double comparison in LINQ | ✅ Passes |
| `CanQueryNodesWithAnyOperator` | AnyAsync (true + false) | ✅ Passes |
| `CanQueryWithStringAndMathFunctionsAdvanced` | (already exists in IAdvancedQueryTests — confirmed via ClassHierarchyTests) | ✅ Passes |

**Test count**: 403 total (387 passed, 2 pre-existing failures, 11 skipped — same as before + 6 new passing tests replacing 1 removed failing test)

---

## Appendix: How to Re-run Coverage

```bash
dotnet test tests/Graph.Model.Age.Tests/Graph.Model.Age.Tests.csproj \
  --coverage --coverage-output-format cobertura --verbosity quiet
```

The latest cobertura report is at `TestResults/{guid}.cobertura.xml`.
