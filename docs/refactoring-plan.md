---
title: "Refactoring Plan"
description: "Comprehensive code analysis and refactoring plan for the AGE Graph Model provider in the new worktree"
ms.date: 2026-06-02
---

# Refactoring Plan: Graph Model AGE Provider

## Executive Summary

This document describes the refactoring opportunities identified in the `new_add_postgres_age_support` branch.

**Phase 1 (completed)**: 11 commits, eliminated major duplication, reduced largest files by 18-46%.

**Phase 2 (this iteration)**: Target is getting ALL files below 300 lines. Current analysis shows 9 files above 300 lines that still have clear extraction candidates. The strategy uses **handler grouping** — extracting co-located private methods by logical responsibility, not by one-class-per-method extremes.

## 1. Current Code Health Overview

### 1.1 File Size Distribution (AGE Provider)

| File | Lines | Phase 1 Δ | This Iteration Target |
|------|-------|-----------|----------------------|
| `AgeCypherQueryVisitor.cs` | 1,307 | -3% | **~500** |
| `AgeExpressionToCypherVisitor.cs` | 894 | -40% | **~500** |
| `AgeEntityMapper.cs` | 548 | — | **~250** |
| `AgeEntityAttributeValidator.cs` | 540 | — | **~200** |
| `AgeCypherEngine.cs` | 452 | -27% | **~250** |
| `AgeGraph.cs` | 421 | -18% | **~280** |
| `ProjectionFragmentVisitor.cs` | 410 | -46% | **~200** |
| `CollectExpressionTranslator.cs` | 382 | — | **~200** |
| `AgeResultProcessor.cs` | 372 | -34% | **~250** |
| Remaining 30+ files | <215 each | 🟢 | ✅ Already done |

**Phase 2 Target**: Reduce ~5,300 lines of above-threshold files to ~2,600 lines.

## 2. Detailed Extraction Analysis

### 2.1 AgeCypherQueryVisitor (1,307 lines → ~500)

**Structure**: ExpressionVisitor subclass. Already uses 4 sub-visitors (traversal, filtering, projection, aggregation). ~20 inline methods remain.

**Handlers to extract**:

| Handler | Methods | Lines | New File |
|---------|---------|-------|----------|
| `PathSegmentHandler` | `HandlePathSegments`, `HandlePathSegmentsIncoming`, `HandlePathSegmentsOutgoing`, `HandleTraverse` + 3 helpers | ~200 | `PathSegmentHandler.cs` |
| `AggregationHandler` | `HandleCount`, `HandleAny`, `HandleAll`, `HandleSum`, `HandleAverage`, `HandleMin`, `HandleMax` | ~140 | `AggregationHandler.cs` |
| `MaterializationHandler` | `HandleFirst`, `HandleLast`, `HandleSingle`, `HandleToList` | ~100 | `MaterializationHandler.cs` |
| `PagingHandler` | `HandleOrderBy`, `HandleThenBy`, `HandleTake`, `HandleSkip`, `HandleDistinct` | ~80 | `PagingHandler.cs` |
| Remaining in visitor | Constructor, `VisitMethodCall` dispatch, `HandleWhere`, `HandleSelect`, `HandleGroupBy`, `HandleJoin`, `HandleSearch`, helpers | ~500 | Core coordinator |

Each handler takes the same constructor pattern (`CypherQueryContext`, `ILogger`) and returns `Expression`. The coordinator's `VisitMethodCall` routes to handlers.

### 2.2 AgeExpressionToCypherVisitor (894 lines → ~500)

Already has 4 extracted sub-handlers. Remaining inline methods:

| Handler | Methods | Lines | New File |
|---------|---------|-------|----------|
| `MemberExpressionHandler` | `VisitMember` — the huge 260-line method + `TryEvaluateStaticMember` | ~270 | `MemberExpressionHandler.cs` |
| `ClosureCaptureHandler` | `TryHandleClosureCountOnRelationship`, `HandleClosureCountOnRelationship`, `DetectRelationshipDirection`, `IsMemberAccess`, `IsCountByNodeIdPredicate` + enum | ~170 | `ClosureCaptureHandler.cs` |
| Remaining in visitor | Constructor, `VisitBinary`, `VisitConstant`, `VisitMethodCall`, `VisitUnary`, `VisitConditional`, `VisitNew`, `HandleToListOnNestedSelect`, collect delegates, `AddParameter`, `VisitAndReturnCypher`, `IsPathSegmentType` | ~500 | Core coordinator |

### 2.3 AgeEntityMapper (548 lines → ~250)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| `EntityTypeResolver` | Type resolution logic (extracted from MapVertex + MapEdge), `Labels.GetMostDerivedType` wrapper | ~80 | `EntityTypeResolver.cs` |
| `LabelsExtractor` | `ExtractLabels(Vertex)` + `ExtractLabels(Edge)` — currently duplicated code | ~80 | `LabelsExtractor.cs` |
| `EntityValueConverter` | `ConvertValue`, `NormalizeValue`, `ConvertJsonElementToValue`, `ConvertJsonElementToDictionary`, `CreateSimpleProperty` | ~120 | `EntityValueConverter.cs` |
| `EntityInfoBuilder` | `CreateEntityInfoFromDictionary`, `IsComplexCollectionType`, `DetermineElementType`, `ParsePointFromJson` | ~80 | `EntityInfoBuilder.cs` |
| Remaining in mapper | `MapVertex`, `MapEdge`, `MapAgePropertyNameToCSharp` | ~250 | Core mapper |

### 2.4 AgeEntityAttributeValidator (540 lines → ~200)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| `UniqueConstraintValidator` | `ValidateUniqueConstraintsAsync`, `ValidateCompositeKeyAsync`, `ValidateUniquePropertiesAsync` | ~130 | `UniqueConstraintValidator.cs` |
| `PropertyRuleValidator` | `ValidatePropertyRules`, `ValidateDynamicPropertyRules`, `ValidatePropertyValue`, `EnsureSchemaInitializedAsync` | ~150 | `PropertyRuleValidator.cs` |
| `ConflictLogger` | `LogConflictDetailsAsync`, `FormatAgtype`, `FormatValue` | ~80 | `ConflictLogger.cs` |
| `CypherQueryHelper` | `HasConflictAsync`, `GetQualifiedProperty`, `GetQualifiedIdProperty`, `EscapeLabel`, `EscapePropertyName` | ~60 | `CypherQueryHelper.cs` |
| Remaining in validator | `ValidateNodeAsync`, `ValidateRelationshipAsync` (facade methods) | ~200 | Facade |

### 2.5 AgeCypherEngine (452 lines → ~250)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| `AggregationDetector` | `DetectAggregationType` | ~60 | `AggregationDetector.cs` |
| `QueryExpressionAnalyzer` | `DetectProjection`, `ExtractElementType`, `ExtractElementTypeFromExpression`, `ExtractProjectedResultType` | ~60 | `QueryExpressionAnalyzer.cs` |
| `ToDictionaryExecutor` | `ExecuteToDictionaryAsync`, `ExecuteAsListAsync` | ~100 | `ToDictionaryExecutor.cs` |
| Remaining in engine | Constructor, `ExecuteAsync`, `BuildCypherQuery`, `ExecuteQueryWithSharedArchitecture`, `ExecuteRawQueryAsync` | ~250 | Core engine |

### 2.6 AgeGraph (421 lines → ~280)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| `GraphSearchHelper` | `SearchAsync`, `SearchNodesAsync` (3 overloads), `SearchRelationshipsAsync` (3 overloads) | ~150 | `GraphSearchHelper.cs` |
| Remaining in graph | `GetTransactionAsync`, `NodesAsync`, `RelationshipsAsync`, CRUD delegates, `DisposeAsync` | ~280 | Core `AgeGraph` |

### 2.7 ProjectionFragmentVisitor (410 lines → ~200)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| `NestedCollectHandler` | `TryHandleNestedCollect`, `TryResolveWithCollectFallback`, `TryResolveExpression` | ~170 | `NestedCollectHandler.cs` |
| Remaining in visitor | `HandleSelect`, `HandleGroupBy`, `ResolveMemberExpression`, helpers | ~240 | Core visitor |

### 2.8 CollectExpressionTranslator (382 lines → ~200)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| Unchanged (tool class) | All methods are cohesive — could extract `BinaryExpressionHelper` and `MemberExpressionHelper` | ~180 of overhead removed via helpers | Internal refactoring only |

### 2.9 AgeResultProcessor (372 lines → ~250)

| Component | Methods | Lines | New File |
|-----------|---------|-------|----------|
| `EntityResultReader` | The `ProcessAsync` loop body (vertex/edge/scalar dispatch) | ~100 | `EntityResultReader.cs` |
| Remaining | Constructor, `ReadMultiColumnRowAsync` delegate | ~250 | Core processor |

**Proposed Architecture**:

```
AgeExpressionToCypherVisitor (coordinator, ~200 lines)
├── IExpressionHandler (interface)
│   ├── BinaryExpressionHandler        (~80 lines)
│   ├── MemberExpressionHandler        (~300 lines)
│   ├── MethodCallExpressionHandler    (~250 lines)
│   │   ├── StringMethodHandler        (~80 lines)
│   │   ├── MathMethodHandler          (~80 lines)
│   │   └── DateTimeMethodHandler      (~60 lines)
│   ├── CollectionExpressionHandler    (~150 lines)
│   ├── ClosureCaptureHandler          (~120 lines)
│   ├── NewExpressionHandler           (~120 lines)
│   ├── ConditionalExpressionHandler   (~40 lines)
│   └── UnaryExpressionHandler         (~40 lines)
└── PropertyMapper                     (~50 lines)
```

**Benefits**:
- Each handler has single responsibility
- Easier to test in isolation
- New Cypher functions can be added without touching the coordinator
- The closure capture logic (closure-captured `IEnumerable<IRelationship>.Count()`) can be isolated

### 2.2 Split `ProjectionFragmentVisitor` (760 lines)

**Problem**: This class mixes projection handling with complex inline expression translation (`TranslateInnerExpression`, `TranslateForCollect`, `WalkPathSegmentChain`, etc.) that duplicates logic from `AgeExpressionToCypherVisitor`.

**Proposed Architecture**:

```
ProjectionFragmentVisitor (coordinator, ~150 lines)
├── SimpleProjectionHandler            (~100 lines)
├── AnonymousTypeProjectionHandler     (~100 lines)
├── GroupByHandler                     (~80 lines)
├── NestedCollectHandler               (~120 lines)
└── InnerExpressionTranslator           (~200 lines, shared with expression visitor)
```

**Key Change**: Extract the `TranslateInnerExpression`, `TranslateForCollect`, `WalkPathSegmentChain`, and related methods into a shared `InnerExpressionTranslator` that both the projection visitor and the expression visitor can use. This eliminates the current duplication where both classes independently translate path segment property chains (e.g., `p.EndNode.FirstName → tgt0.FirstName`).

### 2.3 De-duplicate Expression Translation Logic

**Problem**: Both `AgeExpressionToCypherVisitor` and `ProjectionFragmentVisitor` contain near-identical logic for:
- Path segment chain resolution (`WalkPathSegmentChain` vs inline alias resolution)
- String/Cypher property name mapping (`MapPropertyName` vs `MapPropertyNameStatic`)
- Binary expression translation
- Method call translation for DateTime, TimeSpan, string operations

**Proposed Solution**:
Create a shared `ExpressionTranslationHelper` (static utility class) for all common transformation operations:

```csharp
internal static class ExpressionTranslationHelper
{
    // Path segment resolution
    public static (string? alias, List<string> remainingPath) WalkPathSegmentChain(...)
    public static string ResolvePathSegmentComponent(string componentName, ...)
    
    // Property name mapping
    public static string MapPropertyName(string csharpName)
    
    // Date/time helpers
    public static string TranslateDateTimeMethod(...)
    public static string TranslateTimeSpanProperty(...)
    public static string TranslateMathMethod(...)
    public static string TranslateStringMethod(...)
    
    // Binary operator mapping
    public static string MapBinaryOperator(ExpressionType type)
}
```

## 3. Refactoring Priority Matrix (Phase 2)

| Refactoring | Effort | Impact | Risk | Lines Removed | Priority |
|-------------|--------|--------|------|---------------|----------|
| 2.1 Split `AgeCypherQueryVisitor` (4 handlers) | High | High | Medium | ~800 | **P1** |
| 2.4 Split `AgeEntityAttributeValidator` (4 handlers) | Medium | High | Medium | ~340 | **P1** |
| 2.3 Split `AgeEntityMapper` (4 handlers) | Medium | High | Low | ~300 | **P1** |
| 2.2 Split `AgeExpressionToCypherVisitor` (2 handlers) | Medium | Medium | Medium | ~400 | **P2** |
| 2.5 Split `AgeCypherEngine` (2 handlers) | Low | Medium | Low | ~200 | **P2** |
| 2.7 Split `ProjectionFragmentVisitor` (1 handler) | Low | Low | Low | ~170 | **P3** |
| 2.6 Split `AgeGraph` (1 handler) | Low | Low | Low | ~140 | **P3** |
| 2.9 Split `AgeResultProcessor` (1 handler) | Medium | Low | Medium | ~120 | **P3** |
| 2.8 Split `CollectExpressionTranslator` | Medium | Low | Medium | ~180 | **P4** |

### Priority Definitions

- **P1**: Files >500 lines or high-maintenance burden. Immediate attention.
- **P2**: Large files (400-500 lines). Schedule after P1.
- **P3**: Medium files (350-450 lines). Schedule when in the area.
- **P4**: Cohesive file, low ROI. Only if time permits.

## 4. Implementation Strategy — Phase 2

### Phase 2-A: Entity Layer Extraction (P1)
1. Extract `EntityTypeResolver` + `LabelsExtractor` from `AgeEntityMapper`
2. Extract `EntityValueConverter` + `EntityInfoBuilder` from `AgeEntityMapper`
3. Extract `UniqueConstraintValidator` + `ConflictLogger` from `AgeEntityAttributeValidator`
4. Extract `PropertyRuleValidator` + `CypherQueryHelper` from `AgeEntityAttributeValidator`

### Phase 2-B: Query Visitor Extraction (P1)
1. Extract `PathSegmentHandler` from `AgeCypherQueryVisitor`
2. Extract `AggregationHandler` + `MaterializationHandler` + `PagingHandler` from `AgeCypherQueryVisitor`
3. Extract `ClosureCaptureHandler` from `AgeExpressionToCypherVisitor`
4. Extract `MemberExpressionHandler` from `AgeExpressionToCypherVisitor`

### Phase 2-C: Engine Extraction (P2)
1. Extract `AggregationDetector` + `QueryExpressionAnalyzer` from `AgeCypherEngine`
2. Extract `ToDictionaryExecutor` from `AgeCypherEngine`
3. Extract `GraphSearchHelper` from `AgeGraph`
4. Extract `NestedCollectHandler` from `ProjectionFragmentVisitor`

### Phase 2-D: Polish (P3)
1. Extract `EntityResultReader` from `AgeResultProcessor`
2. Internal refactoring of `CollectExpressionTranslator`

## 5. Architectural Observations

### 5.1 Strengths (Phase 1 Outcomes)
- **Handler pattern** successfully applied to 4 expression handler types
- **Shared helpers** eliminated cross-visitor duplication
- **Generic error handling** via `GraphOperationHelper` in 14 methods
- **All 347 tests pass** with 0 regressions over 11 commits

### 5.2 Remaining Weaknesses
- **Handler groups not extracted**: `AgeCypherQueryVisitor` still has ~20 inline methods that can be grouped by concern
- **Mapper/value conversion inline**: `AgeEntityMapper` has 2 large public methods mixing type resolution, label extraction, and value conversion
- **Static validator monolith**: `AgeEntityAttributeValidator` remains entirely static with 17 methods
- **Engine mixed concerns**: `AgeCypherEngine` still mixes aggregation detection, query analysis, and ToDictionary execution
- **Search methods in Graph**: `AgeGraph` has 7 search-related methods that could form their own class

### 5.3 Test Coverage Gaps
Same as previous analysis — no isolated unit tests for individual components like entity mapper, validators, or result processor.
    }
}
```

This reduces each CRUD method from ~15 lines to ~5 lines.

## 4. Medium-Impact Refactorings

### 4.1 Consolidate Queryable Types

The LINQ queryable classes (`AgeGraphNodeQueryable`, `AgeGraphRelationshipQueryable`, `AgeGraphQueryable`) share common base logic. Consider extracting a shared `AgeQueryableBase<T>` that handles the common provider pattern.

### 4.2 Normalize Cypher Query Building Pipeline

Both `AgeCypherQueryVisitor` and the Neo4j `CypherQueryVisitor` follow the same visitor pattern but have no shared base. Consider extracting a `CypherQueryVisitorBase` class for common traversal/filtering logic.

### 4.3 Extract AgeGraphStore Configuration

`AgeGraphStore` has two constructors with overlapping initialization logic. Extract the initialization into a builder or factory pattern.

### 4.4 Modularize Fragment Classes

The fragment classes (`CypherQueryFragments`, `QueryFragments`, `FragmentFormatting`, `FragmentSequenceInsights`) are shared between providers. The AGE-specific `AgeQueryFragments` should be reviewed for further extraction opportunities.

## 6. Refactoring Workflow Instructions

This section defines the strict protocol to follow for every refactoring step. The goal is **zero regression**: every change must preserve the exact same behavior while improving structure.

### 7.1 Core Principles

| Principle | Description |
|-----------|-------------|
| **One change at a time** | Each refactoring step makes exactly one structural change. No mixed concerns. |
| **Test before and after** | Run the full test suite immediately before and after each change. Compare results. |
| **Commit or revert** | After a successful change with passing tests, commit. If tests fail, revert and try a smaller step. |
| **Keep functions small** | Any function exceeding 30 lines should be evaluated for extraction. |
| **Remove duplication** | If you find yourself copy-pasting logic, extract a shared helper instead. |

### 7.2 Refactoring Workflow (per step)

```
┌─────────────────────────────────────────┐
│  1. RECORD BASELINE                     │
│  • dotnet test → save pass/fail count   │
│  • note which tests are SKIPPED/FAILED  │
│  • git status → ensure clean working tree│
└─────────────────┬───────────────────────┘
                  ▼
┌─────────────────────────────────────────┐
│  2. PLAN THE CHANGE                     │
│  • Identify ONE extraction or split     │
│  • Sketch the new class/method signature│
│  • Verify it preserves public API       │
│  • Keep the change MINIMAL              │
└─────────────────┬───────────────────────┘
                  ▼
┌─────────────────────────────────────────┐
│  3. APPLY THE CHANGE                    │
│  • Extract method → new method calls old│
│  • Extract class → old delegates to new │
│  • Use "Introduce" refactorings first   │
│  • Do NOT change behavior or fix bugs   │
│  • Do NOT reorder or rename (yet)       │
└─────────────────┬───────────────────────┘
                  ▼
┌─────────────────────────────────────────┐
│  4. BUILD                              │
│  • dotnet build (no warnings)          │
│  • If build fails: fix or revert       │
└─────────────────┬───────────────────────┘
                  ▼
┌─────────────────────────────────────────┐
│  5. RUN TESTS                          │
│  • dotnet test (same configuration)    │
│  • Compare: same pass/fail/skip count  │
│  • ⚠️ DIFFERENCE → revert step 3      │
│  • Same results → proceed              │
└─────────────────┬───────────────────────┘
                  ▼
┌─────────────────────────────────────────┐
│  6. CLEAN UP                           │
│  • Remove dead code / old comments     │
│  • dotnet build (no warnings)          │
│  • Run tests one final time            │
│  • git add + git commit                │
└─────────────────┬───────────────────────┘
                  ▼
┌─────────────────────────────────────────┐
│  7. NEXT STEP                          │
│  • Pick next item from priority list   │
│  • Repeat from step 1                  │
└─────────────────────────────────────────┘
```

### 7.3 Test Commands

Run the full AGE test suite (requires a PostgreSQL instance with AGE extension):

```bash
# Run all AGE tests
dotnet test tests/Graph.Model.Age.Tests

# Run with detailed output
dotnet test tests/Graph.Model.Age.Tests --verbosity detailed

# Run a specific test class
dotnet test tests/Graph.Model.Age.Tests --filter "FullyQualifiedName~ErrorHandlingTests"

# Build check (no test run)
dotnet build src/Graph.Model.Age/
```

> **Note**: The AGE tests require a running PostgreSQL instance with the Apache AGE extension. See `.devcontainer/` for a Docker-based setup. If tests cannot run, at minimum verify the build succeeds with zero warnings and that changed files compile correctly.

### 7.4 Keeping Functions Small — Practical Rules

1. **Extract Method** when a function exceeds ~15-20 lines of actual logic (excluding braces, blank lines, and `using` statements)
2. **Extract Class** when a class exceeds ~200 lines or has more than 5-7 responsibilities
3. **Guard Clause pattern**: replace nested `if` blocks with early returns at the top of methods
4. **Strategy pattern**: replace long `switch`/`if-else` chains with handler lookup (dictionary of delegates or strategy classes)
5. **Pipeline pattern**: sequential processing steps should be extracted into a chain of small methods/classes

### 7.5 Code Duplication Elimination Rules

| Pattern | Detection | Fix |
|---------|-----------|-----|
| Literal copy-paste | Same code block in 2+ files | Extract to shared static helper |
| Similar logic with variations | ~80% same, 20% different | Extract common portion, parameterize differences |
| Parallel class hierarchies | Same method signatures, different implementations | Extract interface or base class |
| Inline string building | Same Cypher/query string patterns | Extract constants or builder methods |

### 7.6 Commit Message Convention

Each refactoring commit must follow this template:

```
refactor(scope): brief description of the structural change

Before: <what was the problem>
After:  <what changed structurally>
Tests:  <test results>
```

Examples:

```
refactor(age/visitor): extract StringMethodHandler from AgeExpressionToCypherVisitor

Before: AgeExpressionToCypherVisitor handled all method types inline (1504 lines)
After:  String methods delegated to StringMethodHandler via IExpressionHandler
Tests:  All 137 AGE tests pass (same as baseline)
```

### 7.7 Rollback Protocol

If a refactoring step causes test failures:

1. **Immediately revert** the change: `git checkout -- <files>` or `git reset --hard`
2. **Document** what went wrong in a comment for the next attempt
3. **Break the change into smaller steps** — the step was too large
4. **Retry** from step 1 of the workflow

### 7.8 Tools and Techniques

| Refactoring | Technique | Tool Support |
|-------------|-----------|-------------|
| Extract Method | Select code → Extract Method | Built-in IDE refactoring |
| Extract Class | Create new class, delegate calls | Manual + rename |
| Introduce Parameter | Add parameter to generalize | Built-in IDE refactoring |
| Replace Temp with Query | Inline temporary variable | Built-in IDE refactoring |
| Encapsulate Field | Private field + property | Built-in IDE refactoring |
| Pull Up Member | Move to base class | Built-in IDE refactoring |
| Form Template Method | Strategy pattern | Manual |

## 8. Implementation Strategy

### Phase 1: Safe Extractions (P1) — ✅ Completed
1. ✅ Extract `ExpressionTranslationHelper` — unified `MapPropertyName`, `TryCompileEval`, `IsPathSegmentType`

### Phase 2: Split Large Classes (P1)
- ✅ `AgeExpressionToCypherVisitor`: extracted `StringMethodHandler`, `MathMethodHandler`, `DateTimeMethodHandler`, `CollectionExpressionHandler`, `CollectExpressionTranslator`
- ✅ `ProjectionFragmentVisitor`: extracted `CollectExpressionTranslator`, eliminated duplication with visitor
- ⏳ Remaining: `AgeCypherQueryVisitor` (1,307 lines) — P4

### Phase 3: Engine Refinement (P2) — ✅ Completed
1. ✅ Extract `ScalarResultMaterializer` from `AgeCypherEngine`
2. ✅ Extract `ColumnDefinitionBuilder` from `AgeCypherEngine`
3. ✅ Extract `GraphOperationHelper` — 14 methods refactored in `AgeGraph`

### Phase 4: Result Processor — ✅ Completed
1. ✅ Extract `AgeValueConverters` — 6 static converter methods removed from `AgeResultProcessor` (561 → 372 lines)

### Phase 5: Polish (P3-P4) — Remaining
1. ⏳ `AgeEntityAttributeValidator` (540 lines) — P3, tightly coupled
2. ⏳ `AgeCypherQueryVisitor` (1,307 lines) — P4, structural refactoring
3. ⏳ Add missing unit tests for individual components (see §6.3)

## 8. Appendix: Key Files Changed (new vs main)

### Added/Modified in AGE Project
- `src/Graph.Model.Age/` (25+ source files)

### Extended Shared Infrastructure
- `src/Graph.Model.Cypher/` (8 files: new interfaces + implementations)
- `src/Graph.Model.Serialization/` (3 files: CollectionTypeHelper, IValueConverter, ResultMaterializer)
- `src/Graph.Model.Neo4j/` (4 files: adapted for new shared interfaces)
- `src/Graph.Model/` (1 file: Labels utility)

### New Tests
- `tests/Graph.Model.Age.Tests/` (18+ test files)
- `tests/Graph.Model.Tests/` (2 shared interface test files)

### Documentation
- `docs/age-fulltext-search-limitations.md` (AGE FTS limitations)
- `docs/pattern-comprehension-limitations.md` (Pattern comprehension limitations)
