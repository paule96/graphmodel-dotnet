---
title: "Code Quality Audit — AGE Provider"
description: "Systematic analysis of code smells, anti-patterns, and improvement opportunities in the Graph.Model.Age provider"
ms.date: 2026-06-03
---

# Code Quality Audit — AGE Provider

## Summary

| Category | Issues Found | Critical | Major | Minor |
|----------|-------------|----------|-------|-------|
| Exception handling | 4 | 0 | 2 | 2 |
| Duplication | 6 | 0 | 3 | 3 |
| Complexity | 5 | 0 | 2 | 3 |
| Design | 4 | 0 | 1 | 3 |
| Maintainability | 3 | 0 | 1 | 2 |
| **Total** | **22** | **0** | **9** | **13** |

---

## Critical Issues

None. The codebase is generally well-structured after the refactoring. All issues below are quality improvements.

---

## Major Issues

### M1. Exception Swallowing — Empty Catch Blocks (15 instances)

Seven files contain `catch { }` blocks that silently swallow exceptions. While some are justified (fallback conversion attempts), others lose diagnostic information.

**Files affected:** `ScalarResultMaterializer.cs` (8×), `StringMethodHandler.cs` (3×), `AgeEntityMapper.cs` (1×), `AgeValueConverters.cs` (1×), `EntityResultReader.cs` (1×), `PropertyRuleValidator.cs` (1×), `ClosureCaptureHandler.cs` (1×)

**Fix:**
- For `ScalarResultMaterializer` (type conversion attempts): Comment why each fallback chain exists
- For `StringMethodHandler`: Log a debug/warning message instead of silent swallow
- For `AgeValueConverters.ParsePointFromJson` fallbacks: Add debug logging
- For `EntityResultReader` JSON parse fallback: Log the parse failure

```csharp
// BEFORE:
try { rawValue = agVal.GetInt64(); } catch { }

// AFTER:
try { rawValue = agVal.GetInt64(); }
catch (Exception ex) when (elementType != typeof(long))
{
    _logger?.LogDebug("Failed to parse as Int64, trying fallback: {Message}", ex.Message);
}
```

### M2. Massive Switch Statement with Repetitive Delegation — `AgeCypherQueryVisitor.VisitMethodCall`

The switch statement has ~40 method name pairings using `or` patterns, each delegating to a `Handle*` method. Each `Handle*` method then repeats the pattern: `Visit(source)` → `delegate to handler` → `return node`.

**Lines:** 370 in `AgeCypherQueryVisitor.cs`
**Issue:** 16 nearly identical `Handle*` methods with ~80% boilerplate duplication.

```csharp
// 16× repeated pattern:
private Expression HandleXxx(MethodCallExpression node)
{
    Visit(node.Arguments[0]);              // boilerplate
    _someHandler.HandleXxx(node);          // the one unique line
    return node;                           // boilerplate
}
```

**Fix:** Create a dispatch helper or use a delegate-based dispatch table:

```csharp
private delegate void HandlerAction(MethodCallExpression node);

private void DelegateWithVisit(MethodCallExpression node, HandlerAction action)
{
    Visit(node.Arguments[0]);
    action(node);
}

// Usage in switch:
"Where" => DelegateWithVisit(node, _filteringVisitor.HandleWhere),
"Select" => DelegateWithVisit(node, n => _projectionVisitor.HandleSelect(n)),
// etc.
```

### M3. Complex Batch of Conversion Methods — `AgeEntityMapper.MapVertex`

`MapVertex` (322 lines total file) has deeply nested if/else chains for property value handling:
1. NormalizeValue
2. ConvertValue
3. JsonElement → Dictionary check
4. JSON string collection parsing
5. IDictionary ↔ EntityInfo
6. IList ↔ EntityCollection or SimpleCollection

The method handles too many data shapes. The JSON string collection parsing alone is ~40 lines with try/catch.

**Fix:** Extract `TryParseJsonCollection` and `TryCreateComplexEntityInfo` helper methods from the main `MapVertex` loop.

### M4. `ScalarResultMaterializer` — One-Liner Try-Catch Chain (Readability)

```csharp
if (elementType == typeof(string)) { try { rawValue = agVal.GetString(); } catch { rawValue = agVal.ToString(); } }
else if (elementType == typeof(long) || elementType == typeof(long?)) { try { rawValue = agVal.GetInt64(); } catch { } }
else if (elementType == typeof(int) || elementType == typeof(int?)) { try { rawValue = agVal.GetInt32(); } catch { try { rawValue = (int)agVal.GetInt64(); } catch { } } }
// ... 6 more one-liners
```

**Fix:** Create a type-dispatch dictionary mapping `Type → Func<Agtype, object?>` at the class level, then look up in a single line:

```csharp
private static readonly Dictionary<Type, Func<Agtype, object?>> Converters = new()
{
    [typeof(string)] = ag => { try { return ag.GetString(); } catch { return ag.ToString(); } },
    [typeof(long)] = ag => { try { return ag.GetInt64(); } catch { return null; } },
    // ...
};
```

### M5. `AgeFragmentRenderer.Render` — Growing God Method

At 215 lines, `AgeFragmentRenderer.Render` collects ~15 different fragment types from the sequence and builds the query string. It has 6 helper methods, some using regex for fragment merging.

**Fix:** Extract fragment collection into a separate class/record that groups all fragments by type, then build the query from that. The rendering logic for each clause type (MATCH, WHERE, RETURN, ORDER BY, SKIP/LIMIT) could also be extracted.

```csharp
private sealed record FragmentBundle(
    IReadOnlyList<MatchRootFragment> MatchClauses,
    IReadOnlyList<WhereFragment> WhereClauses,
    IReadOnlyList<ProjectionFragment> Projections,
    // ... etc.
);
```

### M6. `AgeEntityMapper.ConvertValue` — Three Conversion Strategies in One Method

The method handles:
1. JsonElement → CLR type (switch on 9 types)
2. String → CLR type (if-chain for 10 types)
3. IDictionary<string, object?> → simple POCO (JSON serialize/deserialize)
4. Numeric type coercion (decimal→double, long→int, etc.)

This is maintainable but could be clearer by extracting strategy #3 (dict→POCO) and #4 (numeric coercion) into named helper methods.

### M7. Redundant `new` Dictionary Expressions (8 instances)

Several places create `new Dictionary<string, Property>(StringComparer.Ordinal)` for empty complex property dictionaries:

- `MapEdge` — creates empty dict for complex properties (always empty for edges)
- `EntityInfoBuilder.CreateEntityInfoFromDictionary` — creates empty complex dict
- `EntityResultReader` — creates empty dict in a loop

**Fix:** Use `static readonly` empty dictionary or `_emptyComplexProperties` field.

### M8. PropertyRuleValidator — Two-Pass Validation with Duplicated Logic

`ValidateDynamicPropertyRules` does a first pass for required checks and a second pass for unknown properties, but both passes enumerate `schema.Properties.Values` independently.

**Fix:** Single pass with both checks combined.

---

## Minor Issues

### m1. `EmitWhereFragment` — Default Parameters Never Overridden

```csharp
private void EmitWhereFragment(string predicate, string? alias = null, ImmutableArray<string> consumedAliases = default)
```

Both `alias` and `consumedAliases` have defaults that are always used. The caller never overrides them. The `ImmutableArray<string>.Empty` handling inside the method is dead code for the default case.

**Fix:** Remove default parameters or remove the fallback logic if never reached.

### m2. `MemberExpressionHandler` — 7 TryHandle Methods with Repeated Patterns

The handler has 7 `TryHandle*` methods: `PathSegmentNestedAccess`, `StaticDateTime`, `ParameterMember`, `NestedPathSegmentMember`, `ConvertedParameter`, `ChainedMember`, and `FallbackEvaluate`.

Three of them (`TryHandleParameterMember`, `TryHandleNestedPathSegmentMember`, `TryHandlePathSegmentNestedAccess`) resolve the same alias-to-segment mapping (`StartNode→srcAlias`, `EndNode→tgtAlias`, `Relationship→relAlias`) in slightly different contexts.

**Fix:** Extract common `ResolveSegmentAlias` helper:

```csharp
private string ResolveSegmentAlias(string segmentProperty)
    => segmentProperty switch
    {
        nameof(IGraphPathSegment.StartNode) => _sourceAlias ?? "src0",
        nameof(IGraphPathSegment.EndNode) => _targetAlias ?? "tgt0",
        nameof(IGraphPathSegment.Relationship) => _relationshipAlias ?? "r0",
        _ => throw new NotSupportedException($"...")
    };
```

### m3. `AgeCypherQueryVisitor.HandleSelect` — Direct Builder Manipulation

```csharp
private Expression HandleSelect(MethodCallExpression node)
{
    Visit(node.Arguments[0]);
    _projectionVisitor.HandleSelect(node);
    var complexPropertyFragment = new ComplexPropertyLoadingFragment(false, _context.Scope.CurrentAlias);
    _context.AddFragment(complexPropertyFragment);  // direct fragment creation
    return node;
}
```

The handler creates a `ComplexPropertyLoadingFragment` directly instead of delegating to a handler method. This couples the visitor to fragment implementation details.

**Fix:** Move this fragment emission into `_projectionVisitor.HandleSelect`.

### m4. `LabelsExtractor.ExtractLabels` — Using `is` Pattern Instead of `switch`

```csharp
if (labelsInVertex is not null) { ... }
if (labelsInEdge is not null) { ... }
```

These could be combined into a single overloaded method group or a discriminated approach.

### m5. `CollectionHelper.ToListOrSingle` — Reflection-Based Return Type

The method returns `T?` but handles `List<T>`, `T[]`, and single `T` internally. The `T` is a `QueryableAsyncExtensions` marker type in the async case, not the actual element type.

**Fix:** Add XML doc explaining the dual-role of the generic parameter.

### m6. Missing XML Docs on Internal Members

Analysis shows ~30% of public/internal methods lack proper `<summary>` documentation. On internal classes this is low severity, but docs help maintenance.

### m7. `CypherQueryScope.StoreHopAliases` — Three Parallel Lists Instead of a Record

```csharp
public void StoreHopAliases(int hopNumber, string sourceAlias, string relationshipAlias, string targetAlias)
```

Stores three separate lists indexed by hop number. This should be a record/struct:

```csharp
internal sealed record HopInfo(string SourceAlias, string RelationshipAlias, string TargetAlias);
```

### m8. `QueryInitializationHandler.SetupComplexProperties` — Parameters Marked as Unused

The parameter `alias` is accepted but not used. Either remove the parameter or implement the intended behavior.

### m9. `AgeGraphStore` — Two Constructors with Near-Duplicate Initialization

The `AgeGraphStore(connectionString)` and `AgeGraphStore(dataSource)` constructors share ~80% of initialization logic (schemaRegistry, loggerFactory, graphInitTask). Only the data source creation differs.

**Fix:** Have the connection-string constructor delegate to the data-source constructor after building the data source:

```csharp
public AgeGraphStore(string? connectionString, ...) 
    : this(BuildDataSource(connectionString ?? GetDefaultConnection()), graphName, loggerFactory, schemaRegistry) 
{ }
```

Or use a shared `init` method.

### m10. `ClosureCaptureHandler` — Empty Catch with Comment Needed

```csharp
catch { }
```

At line 215, the method `TryHandleClosureCountOnRelationship` has an empty catch that swallows evaluation failures. If intentional (fallback to non-closure handling), the catch should have a comment explaining why.

### m11. `EntityResultReader.ReadMultiColumnRowAsync` — Long Method

At 282 lines, this file handles result reading with multiple switch branches for different column counts. Some local functions exist inside the method that could be top-level private methods.

### m12. `ConcernLogger.HasConflictAsync` — `Task.Run` Without Obvious Reason

```csharp
return await Task.Run(async () => { ... }).ConfigureAwait(false);
```

The `Task.Run` wraps synchronous-looking work in an async lambda. This offloads to a thread pool thread unnecessarily. If the operation is truly async, just call the async methods directly.

### m13. `PathSegmentHandler` — Parameter Name Ambiguity

The constructor takes `Visit` as a `Func<Expression, Expression?>` delegate. The name `Visit` is ambiguous in this context — the callback visits a single expression but the name suggests the full visitor pattern.

**Fix:** Rename to `visitExpression` matching the pattern used in `MemberExpressionHandler`.

---

## Recommendations by Priority

### ✅ Completed

| # | Issue | Fix |
|---|-------|-----|
| m2 | Extract `ResolveSegmentAlias` helper in `MemberExpressionHandler` | Extracted `ResolveSegmentAlias()` method, used in 3 TryHandle methods |
| m7 | Create `HopAliases`/`HopTypes` records in `CypherQueryScope` | Created `HopAliases` and `HopTypes` sealed records, updated all callers |
| M4 | Type-dispatch dictionary in `ScalarResultMaterializer` | Replaced 15-line if/else-if try/catch chain with `Dictionary<Type, Func<Agtype, object?>>` |
| M3 | Extract `TryParseJsonCollection` from `AgeEntityMapper.MapVertex` | Extracted ~40-line JSON collection parsing into `TryParseJsonCollection()` |
| M6 | Extract numeric coercion from `ConvertValue` | Extracted `ConvertDictionaryToSimplePoco()` and `CoerceNumericType()` helpers |
| M2 | Refactor delegate dispatch in `AgeCypherQueryVisitor` | Replaced 16 repetitive `Handle*` methods with 2 helpers: `VisitThen()` and `VisitThenSelect()` |
| m13 | Rename ambiguous `Visit` param in `PathSegmentHandler` | Renamed `_visit` → `_visitExpression` |
| m9 | Unify `AgeGraphStore` constructors | Connection-string constructor now delegates to data-source constructor via `BuildDataSource()` |
| M1 | Comments on empty catches | Added explanatory comments to intentional empty catches |
| m3 | Move `ComplexPropertyLoadingFragment` to `ProjectionFragmentVisitor` | Moved fragment emission from `AgeCypherQueryVisitor.VisitThenSelect` into `ProjectionFragmentVisitor.HandleSelect` |
| M8 | Single-pass validation in `PropertyRuleValidator` | Combined two passes into one using `seenProperties` HashSet — eliminates duplicate enumeration of `schema.Properties` |
| M5 | Extract `FragmentBundle` for `AgeFragmentRenderer` | Created `FragmentBundle` record with `Extract()` factory — replaces 12 individual `OfType<T>().ToList()` calls with one |
| m4 | Simplify `LabelsExtractor` duplicate code | Merged two near-identical `ExtractLabels(Vertex)` and `ExtractLabels(Edge)` into a single private helper |
| m5 | Add XML doc to `CollectionHelper` | Added `<typeparam>` and `<param>` docs explaining dual-role of generic parameter |

### Not Completed (Lower Priority)

| # | Issue | Notes |
|---|-------|-------|
| m1 | Remove unused default parameters from `EmitWhereFragment` | **Re-examined**: callers DO pass `alias` and `consumedAliases` — kept original signature |

---

## Baseline

## Baseline

- **Source files:** 54 in `src/Graph.Model.Age/`
- **Total lines:** ~8,500
- **Issues found:** 22 (0 critical, 9 major, 13 minor)
- **Issue density:** ~2.6 per 1,000 lines
