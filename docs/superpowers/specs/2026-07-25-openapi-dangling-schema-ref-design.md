# Design: Fix dangling OpenApiSchemaReference on net10 / Microsoft.OpenApi 2.x (#30)

**Created**: 2026-07-25
**Status**: Draft
**Input**: GitHub issue [#30](https://github.com/fsprojects/FSharp.Data.JsonSchema/issues/30) — "OpenApi: The input schema must be an OpenApiSchema or OpenApiSchemaReference"

## Problem

`FSharp.Data.JsonSchema.OpenApi`'s `FSharpSchemaTransformer` throws when
`Microsoft.AspNetCore.OpenApi` 10.0.3+ generates an OpenAPI document for any
endpoint whose request/response type is (or contains) an F# discriminated
union, or any self-recursive record/DU:

```
System.InvalidOperationException: The input schema must be an OpenApiSchema or OpenApiSchemaReference.
   at Microsoft.AspNetCore.OpenApi.OpenApiSchemaService.UnwrapOpenApiSchema(IOpenApiSchema sourceSchema)
   at Microsoft.AspNetCore.OpenApi.OpenApiSchemaService.ResolveReferenceForSchema(...)
```

Confirmed via local repro: building a minimal ASP.NET Core app targeting
net10.0 with `Microsoft.AspNetCore.OpenApi` 10.0.10, registering
`FSharpSchemaTransformer`, and exposing an endpoint returning a 3-case DU
reproduces the exact exception and stack shape reported in the issue and its
comments.

## Root Cause

`OpenApiSchemaTranslator.mkRefSchema` (the net10 / `Microsoft.OpenApi` 2.x
code path) builds every schema reference as:

```fsharp
let private mkRefSchema (typeId: string) : OpenApiSchema =
    let s = mkSchema ()
    s.AnyOf.Add(OpenApiSchemaReference(typeId, null))
    s
```

The `null` is the reference's host `OpenApiDocument`. Separately,
`FSharpSchemaTransformer.TransformAsync` calls `OpenApiSchemaTranslator.translate`,
copies the translated root schema into the schema object ASP.NET provides,
and **discards** the `componentSchemas` map that `translate` also returns —
the existing code comment says as much:

```fsharp
// Register component schemas
// The transformer context doesn't expose document components directly,
// so we attach definitions as nested anyOf references.
// In a real integration, the document transformer or middleware
// would register these in components/schemas.
```

Neither the null host document nor the missing registration crashed under
`Microsoft.AspNetCore.OpenApi` 10.0.0, because that version's document
builder didn't walk and resolve `AnyOf`/`OneOf` reference trees as strictly
after transformers ran. Starting at 10.0.3, `OpenApiSchemaService`'s
`ResolveReferenceForSchema` recursively walks the whole schema tree
(including `AnyOf`) and calls `UnwrapOpenApiSchema` on every node found.

`UnwrapOpenApiSchema` requires its input to be either a concrete
`OpenApiSchema` or an `OpenApiSchemaReference` whose `.Target` resolves to a
concrete `OpenApiSchema`. Tracing into `Microsoft.OpenApi` 2.0.0's
`BaseOpenApiReferenceHolder<T, U, V>.Target`:

```csharp
public virtual U? Target
{
    get
    {
        if (Reference.HostDocument is null) return default;
        return Reference.HostDocument.ResolveReferenceTo<U>(Reference, this as IOpenApiSchema);
    }
}
```

Because every `OpenApiSchemaReference` we construct has `HostDocument = null`,
`.Target` always resolves to `null`, so `UnwrapOpenApiSchema` always falls
through to its `throw` branch for these references — exactly the reported
exception.

This is not a new defect introduced by ASP.NET 10.0.3 — the references were
always dangling and the component schemas were always unregistered. The
newer `Microsoft.AspNetCore.OpenApi` release simply started walking and
resolving the tree strictly enough to notice.

`net9.0` (targeting `Microsoft.OpenApi.Models` / the pre-2.0 API surface) is
unaffected: `OpenApiSchemaTransformerContext` on net9 has no `Document`
property at all, and that generation's `OpenApiReference` is plain metadata
on the schema object that doesn't require host-document resolution to
serialize. The bug and its fix are confined to the `NET10_0_OR_GREATER` code
path.

### Secondary defect: self-ref component id collision

Self-recursive refs (`SchemaNode.Ref "#"`, produced by
`SchemaAnalyzer.getOrAnalyzeRef` for a type that recursively contains
itself) are translated via:

```fsharp
| SchemaNode.Ref typeId ->
    if typeId = "#" then
        mkRefSchema (rootSchema.Title |> Option.ofObj |> Option.defaultValue "root")
    else
        mkRefSchema typeId
```

`rootSchema.Title` is never set anywhere in `OpenApiSchemaTranslator.fs` or
`FSharpSchemaTransformer.fs`, so this always evaluates to the literal string
`"root"`. This is harmless today only because the resulting reference is
already dangling and never resolved. Once component registration is fixed
(below), every self-recursive type used in the same `OpenApiDocument` (e.g.
`TreeNode` and `LinkedNode` both appearing across different endpoints) would
register under the same component id `"root"` and silently resolve to
whichever type registered first — a correctness regression traded for the
crash fix, unless corrected in the same change.

### Contributing factor: untested version range

`FSharp.Data.JsonSchema.OpenApi.fsproj`'s net10 target references:

```xml
<PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0-*" />
```

This is a prerelease-only floating pattern. In this repo's restore it
resolves to exactly `10.0.0` — the one version that predates the breaking
strictness change. The package has never been built or tested against
10.0.3+, which is why this shipped and went unnoticed through a full release
cycle.

## Fix

Scope: `NET10_0_OR_GREATER` code paths only, in
`FSharp.Data.JsonSchema.OpenApi`. `net9.0` is unaffected and out of scope.

### `OpenApiSchemaTranslator.fs`

1. `translate` gains an optional parameter:

   ```fsharp
   let translate (doc: SchemaDocument) (?rootTypeId: string) : OpenApiSchema * Map<string, OpenApiSchema> =
       let rootTypeId = defaultArg rootTypeId "root"
       ...
   ```

   `rootTypeId` replaces the dead `rootSchema.Title` lookup as the id used
   for self-refs (`Ref "#"`). Existing call sites (`translate doc`) keep
   compiling unchanged; the default value only matters when no root type
   name is supplied, preserving current unit-test behavior.

2. New function, net10-only:

   ```fsharp
   let registerComponents
       (document: OpenApiDocument)
       (rootSchema: OpenApiSchema)
       (componentSchemas: Map<string, OpenApiSchema>) : unit
   ```

   Behavior:
   - Registers every entry of `componentSchemas` into the document via
     `document.AddComponent(id, schema)` (public API on `OpenApiDocument`,
     idempotent — uses `TryAdd` internally).
   - Recursively walks `rootSchema` and each registered component schema's
     `AnyOf`, `OneOf`, `AllOf`, `Properties`, `Items`, and
     `AdditionalProperties`. Every `OpenApiSchemaReference` found (all of
     which are null-hosted, since `mkRefSchema` is the only place that
     constructs one) is replaced with a freshly constructed
     `OpenApiSchemaReference(id, document)` bound to the real document, so
     `.Target` resolves correctly.
   - No cycle-detection is needed: the schema object graph produced by
     `translate` is acyclic by construction — a reference is always a leaf
     node (`OpenApiSchemaReference`), never an inlined copy of the type it
     points to, so the walk terminates on its own.

### `FSharpSchemaTransformer.fs`

- Compute `rootTypeId = config.TypeIdResolver ty` (the same resolver
  `SchemaAnalyzer` already uses internally) and pass it to `translate`.
- After `copySchemaInto translatedRoot schema`, if `context.Document` is
  non-null, call
  `OpenApiSchemaTranslator.registerComponents context.Document schema componentSchemas`.
  `context.Document` is only ever null when ASP.NET invokes schema
  generation outside the real document-build flow (an internal-only edge
  case); that path keeps today's behavior and is out of scope.

### Test project (`FSharp.Data.JsonSchema.OpenApi.Tests`)

- Change the net10 `Microsoft.AspNetCore.OpenApi` package reference from
  the floating `10.0.0-*` to a pinned, current version (`10.0.10` at time
  of writing) so the test suite actually exercises the code path that
  broke.
- Add a real end-to-end integration test: build a minimal `WebApplication`,
  register `FSharpSchemaTransformer`, call `MapOpenApi`, issue an in-process
  HTTP request for `/openapi/v1.json`, and assert:
  - The response is 200 with a well-formed OpenAPI document.
  - For a 3-case DU (e.g. `Shape`), `components/schemas` contains an entry
    per case and the operation's response schema's `anyOf` refs resolve to
    them.
  - For a self-recursive DU (`TreeNode`), the document builds without
    throwing and the recursive case's ref resolves to a schema registered
    under a stable, unique id (not the literal `"root"`).

This directly exercises the exact code path that broke (ASP.NET's
`OpenApiDocumentService` walking the real `OpenApiDocument`), which the
existing tests — unit tests against `OpenApiSchemaTranslator.translate` in
isolation — do not.

## Out of Scope

- `net9.0` / `Microsoft.OpenApi.Models` path: not broken, not touched.
- `FSharp.Data.JsonSchema.Core` and `FSharp.Data.JsonSchema` (NJsonSchema)
  packages: unaffected, no changes.
- General overhaul of `SchemaGeneratorConfig.TypeIdResolver` or DU case id
  generation: existing behavior (`case.Name` for case-level definitions) is
  unchanged; this fix only supplies a previously-missing id for the
  self-ref (root) case.

## Versioning

Minor version bump for `FSharp.Data.JsonSchema.OpenApi` — this is a bug fix,
but it changes the transformer's runtime behavior (it now mutates the live
`OpenApiDocument` by registering components, which it never did before).

## Testing Plan

1. Unit: `OpenApiSchemaTranslator.translate` with an explicit `rootTypeId`
   produces the expected self-ref id instead of `"root"`.
2. Unit: `registerComponents` registers all component schemas into a test
   `OpenApiDocument` and rewrites all dangling refs to resolve via
   `.Target`.
3. Integration (new): end-to-end `WebApplication` + `MapOpenApi` +
   `/openapi/v1.json` for a plain DU and for `TreeNode`, run against the
   pinned current `Microsoft.AspNetCore.OpenApi` version, asserting the
   request succeeds and the document is structurally correct.
4. Full existing suite (573 tests across Core/main/OpenApi per current
   baseline) must remain green.
