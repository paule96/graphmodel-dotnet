---
title: "Unused Parameter Analysis"
description: "Identifies method parameters that are declared but not used in their method bodies across the AGE Graph Model provider"
ms.date: 2026-06-02
---

# Unused Parameter Analysis

## Why This Matters

After the refactoring, several methods inherited parameters from their original locations that are no longer needed. Cleaning these up reduces cognitive load, eliminates compiler warnings in strict modes, and makes the code easier to read and maintain.

## Detection Method

Parameters were flagged when:
1. The parameter name does not appear anywhere in the method body (excluding the signature line)
2. The parameter is not `cancellationToken` (which is conventionally passed-through)
3. Single-character variable names (`p`, `n`, `r`, `n1`, `n2`, etc.) used in delegates were excluded to avoid false positives

## Identified Issues

### Unused Parameters in Extracted Handlers

| File | Method | Unused Parameter | Notes |
|------|--------|-----------------|-------|
| `MemberExpressionHandler.cs` | `MemberExpressionHandler(...)` | `context` | Constructor param only passed to handler, not used directly |
| `MemberExpressionHandler.cs` | `TryHandleParameterMember()` | `param` | `param` is used for type checks but could be simplified |
| `CollectionExpressionHandler.cs` | `HandleGroupingAggregation()` | `aggFn` | Used via ToLowerInvariant — actually used |
| `SearchHandler.cs` | `IsIncludedInFullTextSearch()` | `entityType` | Only `propertyName` used — `entityType` is only passed to schema lookup |
| `AgeCypherQueryVisitor.cs` | `HandleOrderBy()` | `descending` | Delegates to `_filteringVisitor` which has its own `descending` |
| `AgeCypherQueryVisitor.cs` | `HandleThenBy()` | `descending` | Same delegation pattern |
| `QueryInitializationHandler.cs` | `SetupComplexProperties()` | `alias` | Used — false positive |
| `QueryInitializationHandler.cs` | `AddRelationshipTypeFilter()` | `relationshipAlias` | Used — false positive |
| `AgeEntityMapper.cs` | `MapVertex()` | `targetType` | Used via `EntityTypeResolver.ResolveType` — actually used |
| `AgeEntityMapper.cs` | `MapEdge()` | `targetType` | Same pattern |
| `FragmentEmittingVisitorBase.cs` | `EmitFragment()` | `fragmentType` | Logged but not used for logic |

### Suspicious Parameters (Review Recommended)

| File | Method | Parameter | Reason |
|------|--------|-----------|--------|
| `FilteringFragmentVisitor.cs` | `HandleOrderBy(node, descending, isThenBy)` | `isThenBy` | Looks like it could be used for THEN BY vs ORDER BY distinction but may just be a forwarder |
| `TraversalFragmentVisitor.cs` | `BuildMatchPattern(srcType, relType, tgtType, ...)` | All three types | May be passed for logging only; check if they're needed |
| `NestedCollectHandler.cs` | `TryHandleNestedCollect(propertyExpr, propertyName, cypherAlias, returns)` | `propertyName` | Only used in logging — if logging is trimmed, it becomes unused |

## Recommended Actions

### Tier 1 — Remove or Make Optional
1. **`FragmentEmittingVisitorBase.EmitFragment(fragmentType)`** — Remove `fragmentType` param (only logged)
2. **`MemberExpressionHandler` constructor `context`** — Remove if not referenced internally after handler extraction
3. **`SearchHandler.IsIncludedInFullTextSearch()`** — Remove `entityType` param, `Labels.GetLabelFromType` can be called inside

### Tier 2 — Check After Logging Reduction
1. **`NestedCollectHandler.TryHandleNestedCollect()`** `propertyName` — Only used in logging
2. **`TraversalFragmentVisitor.BuildMatchPattern()`** type params — Verify if needed beyond logging

### Not Dead Code (False Positives)
- `descending` in `HandleOrderBy`/`HandleThenBy` — Actually passed to `_filteringVisitor`
- `alias` in `SetupComplexProperties` — Used in the OPTIONAL MATCH pattern
- `aggFn` in `HandleGroupingAggregation` — Used via `ToLowerInvariant()`; parameter name different
