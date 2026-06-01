---
title: "Refactoring Plan"
description: "Comprehensive code analysis and refactoring plan for the AGE Graph Model provider in the new worktree"
ms.date: 2026-06-01
---

# Refactoring Plan: Graph Model AGE Provider

## Executive Summary

This document describes the refactoring opportunities identified in the `new_add_postgres_age_support` branch of the `graphmodel-dotnet` repository. The branch contains a migration of Apache AGE support from an earlier draft (`add_postgres_age_support`) onto the current `main` branch history, along with package updates and additional feature work.

**Scope**: 90+ source files changed across 6 projects, with the AGE provider project (`src/Graph.Model.Age/`) containing the bulk of new code.

**Key Findings**: Several classes exceed 500 lines and would benefit from splitting. There is duplication between the expression visitor and the projection fragment visitor. Some methods mix multiple responsibilities.

## 1. Current Code Health Overview

### 1.1 File Size Distribution (AGE Provider)

| File | Lines | Risk | Suggested Action |
|------|-------|------|------------------|
| `AgeExpressionToCypherVisitor.cs` | 1,504 | 🔴 Critical | Split into specialized visitors |
| `AgeCypherQueryVisitor.cs` | 1,344 | 🔴 Critical | Extract remaining inline logic to modular visitors |
| `ProjectionFragmentVisitor.cs` | 760 | 🟡 High | Split projection and collect logic |
| `AgeCypherEngine.cs` | 617 | 🟡 High | Extract materialization |
| `AgeResultProcessor.cs` | 561 | 🟡 High | Split result processing pipeline |
| `AgeEntityMapper.cs` | 548 | 🟡 High | Extract conversion and resolution logic |
| `AgeEntityAttributeValidator.cs` | 543 | 🟡 High | Split validation strategies |
| `AgeGraph.cs` | 512 | 🟡 High | Extract CRUD delegation patterns |
| Remaining 27 files | <200 each | 🟢 Low | Generally well-sized |

### 1.2 Project Comparison (LOC)

| Project | Estimated LOC | Notes |
|---------|---------------|-------|
| `Graph.Model.Age` | ~8,000+ | New provider, largest codebase |
| `Graph.Model.Neo4j` | ~2,000 | Established provider |
| `Graph.Model.Cypher` | ~800 | Shared abstractions (extended) |
| `Graph.Model.Serialization` | ~1,500 | Shared (extended for AGE) |

**Observation**: The AGE provider is roughly 4x larger than the Neo4j provider. Some of this is inherent (AGE needs more workarounds), but some is accidental complexity.

## 2. Critical Refactorings

### 2.1 Split `AgeExpressionToCypherVisitor` (1,504 lines)

**Problem**: This single class handles ALL expression type translations: binary, member, constant, method calls (string, math, DateTime), unary, conditional, new expressions, collection operations, closure captures, and path segment resolution. It violates the Single Responsibility Principle.

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

## 3. High-Value Refactorings

### 3.1 Split `AgeCypherEngine` (617 lines)

**Problem**: The query execution engine handles: aggregation detection, Cypher query building, command creation, result materialization, scalar materialization, ToDictionary support, and column definition building.

**Proposed Split**:

```
AgeCypherEngine (coordinator, ~200 lines)
├── QueryBuilding — BuildCypherQuery, DetectProjection
├── QueryExecution — ExecuteRawQueryAsync, CreateCypherCommandWithColumns
├── ScalarMaterializer — MaterializeScalarResultAsync (static)
├── AggregationHandler — DetectAggregationType, HandleAggregation
├── ToDictionaryHandler — ExecuteToDictionaryAsync, ExecuteAsListAsync
└── ColumnDefinitionBuilder — BuildColumnDefinitions, BuildPathSegmentColumnDefinitions
```

**Alternative**: Extract `ScalarMaterializer` and `ColumnDefinitionBuilder` as separate classes immediately. The engine is the most-tested path and should remain stable.

### 3.2 Split `AgeResultProcessor` (561 lines)

**Problem**: The result processor has become a monolith for converting AGE query results to `EntityInfo` objects. `ReadMultiColumnRowAsync` alone is ~250 lines with deeply nested conditionals.

**Proposed Split**:

```
AgeResultProcessor (coordinator, ~100 lines)
├── MultiColumnRowReader        (~150 lines)
├── PathSegmentReconstructor    (~80 lines)
├── AgtypeListConverter         (~80 lines)
├── AgtypeMapConverter          (~60 lines)
├── AgtypeScalarConverter       (~60 lines)
└── JsonFallbackDeserializer    (~40 lines)
```

### 3.3 Split `AgeEntityAttributeValidator` (543 lines)

**Problem**: Static class mixing node/relationship validation, dynamic property rules, unique constraints, composite keys, and conflict logging. All methods are `static`, making it hard to unit test.

**Proposed Split**:

```
AgeEntityAttributeValidator (facade, ~50 lines)
├── NodeValidationStrategy         (~80 lines)
├── RelationshipValidationStrategy  (~80 lines)
├── DynamicPropertyValidator       (~120 lines)
├── StaticPropertyValidator        (~80 lines)
├── UniqueConstraintValidator      (~100 lines)
├── CompositeKeyValidator          (~80 lines)
└── ConflictLogger                 (~60 lines)
```

### 3.4 Extract Error Handling from `AgeGraph` (512 lines)

**Problem**: The `AgeGraph` class implements the `IGraph` interface with extensive try-catch wrapping that follows the same pattern in every method. The error handling boilerplate is ~30% of the file.

**Proposed Solution**:
Extract the repetitive pattern into a reusable helper that calls a delegate within a try-catch:

```csharp
internal static class GraphOperationHelper
{
    public static async Task<T> ExecuteAsync<T>(
        ILogger logger,
        string operationName,
        Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not GraphException)
        {
            logger.LogError(ex, "Failed to {Operation}", operationName);
            throw new GraphException($"Failed to {operationName}", ex);
        }
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

## 5. Refactoring Priority Matrix

| Refactoring | Effort | Impact | Risk | Priority |
|-------------|--------|--------|------|----------|
| 2.1 Split `AgeExpressionToCypherVisitor` | High | High | Medium | **P1** |
| 2.3 De-duplicate expression translation | Medium | High | Medium | **P1** |
| 2.2 Split `ProjectionFragmentVisitor` | Medium | High | Medium | **P1** |
| 3.4 Extract error handling from `AgeGraph` | Low | Medium | Low | **P2** |
| 3.2 Split `AgeResultProcessor` | High | Medium | High | **P2** |
| 3.1 Split `AgeCypherEngine` | Medium | Medium | High | **P2** |
| 3.3 Split `AgeEntityAttributeValidator` | Medium | Medium | Medium | **P3** |
| 4.1 Consolidate queryable types | Low | Low | Low | **P3** |
| 4.2 Normalize Cypher visitor | High | Low | High | **P4** |
| 4.3 Extract config builder | Low | Low | Low | **P4** |

### Priority Definitions

- **P1**: Addresses code duplication and maintainability bottlenecks. Should be done before significant new feature work.
- **P2**: Improves testability and reduces class size. Schedule after P1.
- **P3**: Cleanup with moderate benefit. Schedule when in the area.
- **P4**: Nice-to-have. No immediate need.

## 6. Architectural Observations

### 6.1 Strengths

- **Modular fragment visitor pattern**: The `FragmentEmittingVisitorBase` and its sub-visitors (`TraversalFragmentVisitor`, `FilteringFragmentVisitor`, `ProjectionFragmentVisitor`, `AggregationFragmentVisitor`) show good separation of concerns.
- **Interface-based Cypher abstractions**: The `ICypherExpressionProcessor`, `ICypherQueryBuilderContext`, `ICypherCollectionProvider` interfaces enable provider-specific implementations.
- **Generic materializer**: `ResultMaterializer<TValueConverter>` correctly uses generics to support provider-specific value conversion.
- **Comprehensive test coverage**: The test suite has dedicated test files for each major feature area.

### 6.2 Weaknesses

- **Expression translation duplication**: Two separate systems (`AgeExpressionToCypherVisitor` and `ProjectionFragmentVisitor.TranslateInnerExpression`) both translate LINQ expressions to Cypher with different approaches.
- **Mixed concerns in execution**: `AgeCypherEngine` couples query building, command execution, and result materialization.
- **Static validators**: `AgeEntityAttributeValidator` is entirely static, making it hard to mock in tests and hard to extend with new validation rules.
- **Error handling boilerplate**: Repetitive try-catch patterns across the codebase.

### 6.3 Test Coverage Gaps

The analysis found these files lack corresponding unit tests for individual components (integration tests exercise the full pipeline but don't test units in isolation):

| Component | Unit Tests | Integration Tests |
|-----------|-----------|-------------------|
| `AgeExpressionToCypherVisitor` | ✅ FragmentRendererTests | ✅ Implicit via QueryTests |
| `AgeEntityMapper` | ❌ Missing | ✅ Implicit |
| `AgeEntityAttributeValidator` | ❌ Missing | ✅ AttributeValidationTests |
| `AgeResultProcessor` | ❌ Missing | ✅ Implicit via QueryTests |
| `AgeNodeManager` | ❌ Missing | ✅ Implicit via CRUD tests |
| `AgeRelationshipManager` | ❌ Missing | ✅ Implicit via CRUD tests |
| `AgeSerializationBridge` | ❌ Missing | ✅ Implicit |

## 7. Refactoring Workflow Instructions

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

### Phase 1: Safe Extractions (P1)
1. Extract `ExpressionTranslationHelper` static class with all shared logic
2. Wire it into both `AgeExpressionToCypherVisitor` and `ProjectionFragmentVisitor`
3. Verify no behavior change via existing tests (per §7 workflow)

### Phase 2: Split Large Classes (P1)
1. Decompose `AgeExpressionToCypherVisitor` into handler classes (one per handler type)
2. Split `ProjectionFragmentVisitor` — extract `CollectHandler` and `InnerExpressionTranslator`
3. Run full test suite after each extraction (§7.2 workflow)

### Phase 3: Engine Refinement (P2)
1. Extract `ScalarMaterializer` from `AgeCypherEngine`
2. Extract `ColumnDefinitionBuilder` from `AgeCypherEngine`
3. Extract error handling helper for `AgeGraph` try-catch patterns

### Phase 4: Validation and Result Processing (P2-P3)
1. Refactor `AgeEntityAttributeValidator` from static to strategy-based
2. Split `AgeResultProcessor` — extract `MultiColumnRowReader`, `PathSegmentReconstructor`, `AgtypeListConverter`

### Phase 5: Polish (P3-P4)
1. Consolidate queryable types — extract shared `AgeQueryableBase<T>`
2. Extract config builder from `AgeGraphStore`
3. Add missing unit tests for individual components (see §6.3)

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
