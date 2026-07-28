# Fix dangling OpenApiSchemaReference on net10 (#30) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix `FSharpSchemaTransformer` so it no longer throws `InvalidOperationException: The input schema must be an OpenApiSchema or OpenApiSchemaReference` when `Microsoft.AspNetCore.OpenApi` 10.0.3+ generates an OpenAPI document for an endpoint whose request/response type is (or contains) an F# discriminated union or a self-recursive type.

**Architecture:** `OpenApiSchemaTranslator` (net10 / `Microsoft.OpenApi` 2.x path) currently builds every schema reference with a `null` host document and `FSharpSchemaTransformer` never registers the component schemas `translate` returns into the live `OpenApiDocument`, so every reference is permanently unresolvable. The fix threads the live `OpenApiDocument` (available via `context.Document` on net10) into the translator so it can register components as it constructs them and bind references directly to the real document, plus fixes a related bug where every self-recursive type's root component was silently registered under the same hardcoded id `"root"`.

**Tech Stack:** F# 8.0+ / .NET SDK 10.0+, `Microsoft.AspNetCore.OpenApi`, `Microsoft.OpenApi` 2.x, Expecto (tests).

## Global Constraints

- Scope is `NET10_0_OR_GREATER` code paths in `FSharp.Data.JsonSchema.OpenApi` only. `net9.0` (`Microsoft.OpenApi.Models` / pre-2.0 API) is not broken and must not be touched functionally, though the shared self-ref id fix (Task 3) benefits it incidentally at zero risk since it's a pure naming change with no new document-registration behavior on that TFM.
- `FSharp.Data.JsonSchema.Core` and `FSharp.Data.JsonSchema` / `FSharp.Data.JsonSchema.NJsonSchema` packages: no changes.
- Existing public API `OpenApiSchemaTranslator.translate (doc: SchemaDocument) : OpenApiSchema * Map<string, OpenApiSchema>` must keep compiling and behaving exactly as today for existing callers (used directly by `TranslatorTests.fs` and `TransformerIntegrationTests.fs`).
- Version: shared `VersionPrefix` across all 4 packages lives in `src/Directory.Build.props`, currently `3.0.1`. This fix is a minor bump to `3.1.0`.
- Reference doc: `docs/superpowers/specs/2026-07-25-openapi-dangling-schema-ref-design.md` (already committed on this branch).
- Root cause, exact stack traces, and OpenAPI.NET source citations backing every claim below are recorded in that spec — do not re-derive them, they're already verified against a real `Microsoft.AspNetCore.OpenApi` 10.0.10 repro.

---

### Task 1: Pin the net10 test/package dependency to a version that actually exhibits the bug

**Why first:** `FSharp.Data.JsonSchema.OpenApi.fsproj`'s net10 target currently references `Microsoft.AspNetCore.OpenApi Version="10.0.0-*"`, a prerelease-only floating pattern that resolves to exactly `10.0.0` in this repo — the one version that predates the breaking strictness change. Every later task's tests need to run against a version that actually reproduces the crash, or they'll pass for the wrong reason.

**Files:**
- Modify: `src/FSharp.Data.JsonSchema.OpenApi/FSharp.Data.JsonSchema.OpenApi.fsproj`

**Interfaces:** None — packaging-only change.

- [ ] **Step 1: Change the net10 package reference**

In `src/FSharp.Data.JsonSchema.OpenApi/FSharp.Data.JsonSchema.OpenApi.fsproj`, find:

```xml
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0-*" />
  </ItemGroup>
```

Replace with:

```xml
  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />
  </ItemGroup>
```

- [ ] **Step 2: Restore and confirm the pinned version resolves**

Run: `dotnet restore src/FSharp.Data.JsonSchema.OpenApi/FSharp.Data.JsonSchema.OpenApi.fsproj`

Then: `grep -A2 '"Microsoft.AspNetCore.OpenApi"' src/FSharp.Data.JsonSchema.OpenApi/obj/project.assets.json | grep version`

Expected: shows `"version": "10.0.10"` (not `10.0.0`).

- [ ] **Step 3: Confirm existing suite still builds and passes at the new version**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0`

Expected: builds and all existing tests PASS (nothing exercises the live ASP.NET pipeline yet, so the version bump alone shouldn't change any existing test's outcome).

- [ ] **Step 4: Commit**

```bash
git add src/FSharp.Data.JsonSchema.OpenApi/FSharp.Data.JsonSchema.OpenApi.fsproj
git commit -m "chore: pin net10 Microsoft.AspNetCore.OpenApi test dependency to 10.0.10

The floating 10.0.0-* reference resolved to exactly 10.0.0, the one
version that predates the breaking schema-resolution strictness change
in #30. Pinning to a current version so tests actually exercise the
code path that broke."
```

---

### Task 2: Add a failing end-to-end test that reproduces #30

**Why before the fix:** Every existing test in this project calls `OpenApiSchemaTranslator.translate` directly — none of them ever go through ASP.NET's real `OpenApiDocumentService`, which is exactly why this bug shipped unnoticed. This task adds the test that would have caught it, confirms it fails against the current (broken) code, and becomes the regression guard the fix must turn green in Task 4.

**Files:**
- Create: `test/FSharp.Data.JsonSchema.OpenApi.Tests/EndToEndTests.fs`
- Modify: `test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj`

**Interfaces:**
- Consumes: `FSharp.Data.JsonSchema.OpenApi.FSharpSchemaTransformer()` (existing, unchanged constructor), `Microsoft.AspNetCore.Builder.WebApplication`, `Microsoft.Extensions.DependencyInjection.OpenApiServiceCollectionExtensions.AddOpenApi`.
- Produces: nothing consumed by later tasks — this is a standalone regression test.

- [ ] **Step 1: Create the test file**

Create `test/FSharp.Data.JsonSchema.OpenApi.Tests/EndToEndTests.fs`:

```fsharp
module FSharp.Data.JsonSchema.OpenApi.Tests.EndToEndTests

open Expecto
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open FSharp.Data.JsonSchema.OpenApi

/// Matches the exact repro from GitHub issue #30.
type Shape =
    | Circle of radius: float
    | Rectangle of width: float * height: float
    | Point

/// Self-recursive DU, matching the existing TreeNode pattern used elsewhere in this suite.
type TreeNode =
    | Leaf of int
    | Branch of TreeNode * TreeNode

let private startApp (mapEndpoints: WebApplication -> unit) : WebApplication =
    let builder = WebApplication.CreateBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
    builder.Services.ConfigureHttpJsonOptions(fun options ->
        JsonFSharpOptions.Default().AddToJsonSerializerOptions options.SerializerOptions
    ) |> ignore
    builder.Services.AddOpenApi(fun options ->
        options.AddSchemaTransformer(FSharpSchemaTransformer()) |> ignore
    ) |> ignore
    let app = builder.Build()
    app.MapOpenApi() |> ignore
    mapEndpoints app
    app.StartAsync().GetAwaiter().GetResult()
    app

let private baseAddress (app: WebApplication) : string =
    let server = app.Services.GetRequiredService<IServer>()
    let feature = server.Features.Get<IServerAddressesFeature>()
    feature.Addresses |> Seq.head

let private getOpenApiDocument (app: WebApplication) : System.Net.HttpStatusCode * string =
    use client = new HttpClient()
    let response = client.GetAsync(baseAddress app + "/openapi/v1.json").GetAwaiter().GetResult()
    let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    (response.StatusCode, body)

let private hasSchema (root: JsonElement) (name: string) : bool =
    match root.TryGetProperty "components" with
    | false, _ -> false
    | true, components ->
        match components.TryGetProperty "schemas" with
        | false, _ -> false
        | true, schemas -> schemas.TryGetProperty name |> fst

[<Tests>]
let endToEndTests =
    testList "endToEnd" [
        test "OpenAPI document generation succeeds for a discriminated union response" {
            let app = startApp (fun app -> app.MapGet("/shape", System.Func<Shape>(fun () -> Point)) |> ignore)
            try
                let (status, body) = getOpenApiDocument app
                Expect.equal status System.Net.HttpStatusCode.OK "200 OK, not the InvalidOperationException from #30"
                use jsonDoc = JsonDocument.Parse body
#if NET10_0_OR_GREATER
                let root = jsonDoc.RootElement
                Expect.isTrue (hasSchema root "Circle") "Circle registered as a component schema"
                Expect.isTrue (hasSchema root "Rectangle") "Rectangle registered as a component schema"
                Expect.isTrue (hasSchema root "Point") "Point registered as a component schema"
#endif
                ()
            finally
                app.StopAsync().GetAwaiter().GetResult()
        }

        test "OpenAPI document generation succeeds for a self-recursive discriminated union" {
            let app = startApp (fun app -> app.MapGet("/tree", System.Func<TreeNode>(fun () -> Leaf 1)) |> ignore)
            try
                let (status, body) = getOpenApiDocument app
                Expect.equal status System.Net.HttpStatusCode.OK "200 OK, not the InvalidOperationException from #30"
                use jsonDoc = JsonDocument.Parse body
#if NET10_0_OR_GREATER
                let root = jsonDoc.RootElement
                Expect.isTrue (hasSchema root "Leaf") "Leaf registered as a component schema"
                Expect.isTrue (hasSchema root "Branch") "Branch registered as a component schema"
                Expect.isTrue (hasSchema root "TreeNode") "TreeNode root registered as a component schema (self-ref target)"
#endif
                ()
            finally
                app.StopAsync().GetAwaiter().GetResult()
        }
    ]
```

Notes on why it's written this way:
- `components/schemas` assertions are wrapped in `#if NET10_0_OR_GREATER` because net9 (pre-2.0 `Microsoft.OpenApi.Models`) has its own different, pre-existing (and out of scope) reference-registration behavior — only the top-level type gets auto-registered there, not our nested case components. The 200-OK / valid-JSON assertion runs on both TFMs unconditionally, so this test also guards net9 from ever regressing.
- Binds to `http://127.0.0.1:0` (OS-assigned port) and discovers the real port via `IServerAddressesFeature` so the test is safe to run concurrently / in CI without port collisions.

- [ ] **Step 2: Register the new file in the test project**

In `test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj`, find:

```xml
    <Compile Include="TranslatorTests.fs" />
    <Compile Include="TransformerIntegrationTests.fs" />
    <Compile Include="Main.fs" />
```

Replace with:

```xml
    <Compile Include="TranslatorTests.fs" />
    <Compile Include="TransformerIntegrationTests.fs" />
    <Compile Include="EndToEndTests.fs" />
    <Compile Include="Main.fs" />
```

- [ ] **Step 3: Run the new tests on net10 and confirm they FAIL with the exact #30 exception**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0 --filter "endToEnd"`

Expected: both tests FAIL. The failure should show `status` was `InternalServerError` (500), not `OK` — this is the same `InvalidOperationException: The input schema must be an OpenApiSchema or OpenApiSchemaReference` from issue #30, just surfaced as a 500 response rather than a thrown exception in-process (the exception happens inside ASP.NET's request pipeline, on the server side).

- [ ] **Step 4: Run the same tests on net9 and confirm they PASS**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net9.0 --filter "endToEnd"`

Expected: both tests PASS. This confirms net9 is genuinely unaffected and the test harness itself is sound (it's not failing on net10 for some unrelated environment reason).

- [ ] **Step 5: Commit**

```bash
git add test/FSharp.Data.JsonSchema.OpenApi.Tests/EndToEndTests.fs test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj
git commit -m "test: add failing end-to-end repro for #30

Real WebApplication + MapOpenApi + HTTP GET /openapi/v1.json, for both
a plain DU and a self-recursive DU. Fails on net10 with the exact #30
exception; passes on net9, confirming the bug and the fix are scoped
to the net10 / Microsoft.OpenApi 2.x path."
```

---

### Task 3: Fix `OpenApiSchemaTranslator` to bind references to a live document

**Files:**
- Modify: `src/FSharp.Data.JsonSchema.OpenApi/OpenApiSchemaTranslator.fs`
- Modify: `test/FSharp.Data.JsonSchema.OpenApi.Tests/TranslatorTests.fs`

**Interfaces:**
- Consumes: `SchemaDocument`, `SchemaNode` (unchanged, from `FSharp.Data.JsonSchema.Core`).
- Produces (new, net10-only, consumed by Task 4):
  `OpenApiSchemaTranslator.translateForDocument (doc: SchemaDocument) (rootTypeId: string) (document: OpenApiDocument) : OpenApiSchema * Map<string, OpenApiSchema>`
- Unchanged (all TFMs, all existing callers keep working):
  `OpenApiSchemaTranslator.translate (doc: SchemaDocument) : OpenApiSchema * Map<string, OpenApiSchema>`

- [ ] **Step 1: Write the failing unit tests**

Append to `test/FSharp.Data.JsonSchema.OpenApi.Tests/TranslatorTests.fs` (after the existing `definitionsTests` list, before the final newline):

```fsharp
#if NET10_0_OR_GREATER
[<Tests>]
let documentBindingTests =
    testList "translator/documentBinding" [
        test "translateForDocument registers component schemas and resolves references" {
            let doc = {
                Root = SchemaNode.AnyOf [SchemaNode.Ref "A"; SchemaNode.Ref "B"]
                Definitions = [
                    "A", SchemaNode.Primitive(PrimitiveType.String, None)
                    "B", SchemaNode.Primitive(PrimitiveType.Integer, Some "int32")
                ]
            }
            let document = OpenApiDocument()
            let (schema: OASchema, _components) =
                OpenApiSchemaTranslator.translateForDocument doc "Root" document
            Expect.isNotNull (document.Components :> obj) "components created"
            Expect.isTrue (document.Components.Schemas.ContainsKey "A") "A registered"
            Expect.isTrue (document.Components.Schemas.ContainsKey "B") "B registered"
            let refA = schema.AnyOf.[0] :?> OpenApiSchemaReference
            Expect.isNotNull (refA.Target :> obj) "ref A resolves to a non-null target"

        test "translateForDocument binds self-ref to the given rootTypeId and registers the root component" {
            let doc = {
                Root = SchemaNode.Object {
                    Properties = [
                        { Name = "next"; Schema = SchemaNode.Nullable(SchemaNode.Ref "#"); Description = None }
                    ]
                    Required = []
                    AdditionalProperties = false
                    TypeId = None
                    Description = None
                    Title = None
                }
                Definitions = [
                    "Unused", SchemaNode.Primitive(PrimitiveType.String, None)
                ]
            }
            let document = OpenApiDocument()
            let (schema: OASchema, _components) =
                OpenApiSchemaTranslator.translateForDocument doc "LinkedNode" document
            Expect.isTrue (document.Components.Schemas.ContainsKey "LinkedNode") "root registered under supplied rootTypeId, not the literal \"root\""
            Expect.isFalse (document.Components.Schemas.ContainsKey "root") "literal \"root\" id is not used"
            let nextWrapper = schema.Properties.["next"] :?> OpenApiSchema
            let selfRef = nextWrapper.AnyOf.[0] :?> OpenApiSchemaReference
            Expect.equal selfRef.Reference.Id "LinkedNode" "self-ref bound to supplied root type id"
            Expect.isNotNull (selfRef.Target :> obj) "self-ref resolves to a non-null target"
        }
    ]
#endif
```

- [ ] **Step 2: Run the new tests to verify they fail to compile**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0 --filter "documentBinding"`

Expected: build FAILS — `OpenApiSchemaTranslator.translateForDocument` is not defined yet.

- [ ] **Step 3: Replace `OpenApiSchemaTranslator.fs` with the fixed implementation**

Replace the full contents of `src/FSharp.Data.JsonSchema.OpenApi/OpenApiSchemaTranslator.fs` with:

```fsharp
namespace FSharp.Data.JsonSchema.OpenApi

open System
open FSharp.Data.JsonSchema.Core

#if NET10_0_OR_GREATER
open Microsoft.OpenApi
open System.Text.Json.Nodes
#else
open Microsoft.OpenApi.Models
open Microsoft.OpenApi.Any
#endif

// Alias to avoid collision with Microsoft.OpenApi.Any.PrimitiveType on net9.0
type private CorePrimitiveType = FSharp.Data.JsonSchema.Core.PrimitiveType

/// Translates a SchemaDocument (Core IR) to OpenApiSchema.
module OpenApiSchemaTranslator =

    // ── Schema creation helper ──
    // In OpenApi 2.0, the parameterless constructor does not auto-initialize collections.

    let private mkSchema () =
        let s = OpenApiSchema()
#if NET10_0_OR_GREATER
        s.Properties <- Collections.Generic.Dictionary<string, IOpenApiSchema>()
        s.Required <- Collections.Generic.HashSet<string>()
        s.AnyOf <- Collections.Generic.List<IOpenApiSchema>()
        s.OneOf <- Collections.Generic.List<IOpenApiSchema>()
        s.AllOf <- Collections.Generic.List<IOpenApiSchema>()
        s.Enum <- Collections.Generic.List<JsonNode>()
#endif
        s

    // ── Version-abstracted helpers ──

#if NET10_0_OR_GREATER
    let private setType (schema: OpenApiSchema) (pt: CorePrimitiveType) =
        schema.Type <-
            Nullable(
                match pt with
                | CorePrimitiveType.String -> JsonSchemaType.String
                | CorePrimitiveType.Integer -> JsonSchemaType.Integer
                | CorePrimitiveType.Number -> JsonSchemaType.Number
                | CorePrimitiveType.Boolean -> JsonSchemaType.Boolean
            )

    let private setObjectType (schema: OpenApiSchema) =
        schema.Type <- Nullable(JsonSchemaType.Object)

    let private setArrayType (schema: OpenApiSchema) =
        schema.Type <- Nullable(JsonSchemaType.Array)

    let private makeNullable (schema: OpenApiSchema) =
        match schema.Type with
        | t when t.HasValue -> schema.Type <- Nullable(t.Value ||| JsonSchemaType.Null)
        | _ -> schema.Type <- Nullable(JsonSchemaType.Null)

    let private addEnumValue (schema: OpenApiSchema) (value: string) =
        schema.Enum.Add(JsonValue.Create(value))

    let private setDefault (schema: OpenApiSchema) (value: string) =
        schema.Default <- JsonValue.Create(value)

    /// Builds a schema wrapping a single reference. When `document` is supplied, the
    /// reference is bound to it so `.Target` resolves once the id is registered via
    /// `document.AddComponent` — see `translateCore`. When `document` is `None` (the
    /// document-agnostic `translate` entry point), the reference is left unbound, matching
    /// today's behavior for callers that only inspect the translated shape.
    let private mkRefSchema (document: OpenApiDocument option) (typeId: string) : OpenApiSchema =
        let s = mkSchema ()
        let reference =
            match document with
            | Some doc -> OpenApiSchemaReference(typeId, doc)
            | None -> OpenApiSchemaReference(typeId, null)
        s.AnyOf.Add(reference)
        s
#else
    let private setType (schema: OpenApiSchema) (pt: CorePrimitiveType) =
        schema.Type <-
            match pt with
            | CorePrimitiveType.String -> "string"
            | CorePrimitiveType.Integer -> "integer"
            | CorePrimitiveType.Number -> "number"
            | CorePrimitiveType.Boolean -> "boolean"

    let private setObjectType (schema: OpenApiSchema) =
        schema.Type <- "object"

    let private setArrayType (schema: OpenApiSchema) =
        schema.Type <- "array"

    let private makeNullable (schema: OpenApiSchema) =
        schema.Nullable <- true

    let private addEnumValue (schema: OpenApiSchema) (value: string) =
        schema.Enum.Add(OpenApiString(value))

    let private setDefault (schema: OpenApiSchema) (value: string) =
        schema.Default <- OpenApiString(value)

    let private mkRefSchema (typeId: string) : OpenApiSchema =
        let schema = OpenApiSchema()
        schema.Reference <- OpenApiReference(Type = Nullable(ReferenceType.Schema), Id = typeId)
        schema
#endif

    // ── Core translation ──

    /// Shared translation implementation. `rootTypeId` names the component a self-ref
    /// ("#") binds to. `document`, when supplied (net10 only), is the live OpenApiDocument
    /// to register component schemas into and bind references against, so they actually
    /// resolve once ASP.NET walks the tree. When `None`, references are left unbound,
    /// which is correct for the document-agnostic `translate` entry point.
    let private translateCore
        (doc: SchemaDocument)
        (rootTypeId: string)
        (document: OpenApiDocument option)
        : OpenApiSchema * Map<string, OpenApiSchema> =
        let componentSchemas = Collections.Generic.Dictionary<string, OpenApiSchema>()
        let rootSchema = mkSchema ()

        let rec translateNode (node: SchemaNode) : OpenApiSchema =
            match node with
            | SchemaNode.Object obj ->
                let schema = mkSchema ()
                setObjectType schema
                for prop in obj.Properties do
                    let propSchema = translateNode prop.Schema
                    schema.Properties.[prop.Name] <- propSchema
                for req in obj.Required do
                    schema.Required.Add(req) |> ignore
                schema.AdditionalPropertiesAllowed <- obj.AdditionalProperties
                schema

            | SchemaNode.Array items ->
                let schema = mkSchema ()
                setArrayType schema
                schema.Items <- translateNode items
                schema

            | SchemaNode.AnyOf schemas ->
                let schema = mkSchema ()
                for s in schemas do
                    schema.AnyOf.Add(translateNode s)
                schema

            | SchemaNode.OneOf (schemas, discriminator) ->
                let schema = mkSchema ()
                for s in schemas do
                    schema.OneOf.Add(translateNode s)
                match discriminator with
                | Some disc ->
                    let d = OpenApiDiscriminator()
                    d.PropertyName <- disc.PropertyName
                    schema.Discriminator <- d
                | None -> ()
                schema

            | SchemaNode.Nullable inner ->
                let innerSchema = translateNode inner
                makeNullable innerSchema
                innerSchema

            | SchemaNode.Primitive (pt, fmt) ->
                let schema = mkSchema ()
                setType schema pt
                match fmt with
                | Some f -> schema.Format <- f
                | None -> ()
                schema

            | SchemaNode.Enum (values, _pt) ->
                let schema = mkSchema ()
                setType schema CorePrimitiveType.String
                for v in values do
                    addEnumValue schema v
                schema

            | SchemaNode.Ref typeId ->
                let resolvedId = if typeId = "#" then rootTypeId else typeId
#if NET10_0_OR_GREATER
                mkRefSchema document resolvedId
#else
                mkRefSchema resolvedId
#endif

            | SchemaNode.Map valueSchema ->
                let schema = mkSchema ()
                setObjectType schema
                schema.AdditionalProperties <- translateNode valueSchema
                schema

            | SchemaNode.Const (value, _pt) ->
                let schema = mkSchema ()
                setType schema CorePrimitiveType.String
                addEnumValue schema value
                setDefault schema value
                schema

            | SchemaNode.Any ->
                mkSchema ()

        // Translate definitions into component schemas, registering each into the live
        // document (when supplied) as it's produced.
        for (key, value) in doc.Definitions do
            let componentSchema = translateNode value
            componentSchemas.[key] <- componentSchema
#if NET10_0_OR_GREATER
            document |> Option.iter (fun d -> d.AddComponent(key, componentSchema) |> ignore)
#endif

        // Translate root
        let translated = translateNode doc.Root

        // If no definitions, return translated directly.
        // Otherwise copy into rootSchema (which is pre-allocated for self-references),
        // and register it under rootTypeId so a "#" self-ref elsewhere resolves to it.
        let result =
            if List.isEmpty doc.Definitions then
                translated
            else
                rootSchema.Type <- translated.Type
                rootSchema.Format <- translated.Format
                rootSchema.Items <- translated.Items
                rootSchema.AdditionalPropertiesAllowed <- translated.AdditionalPropertiesAllowed
                rootSchema.AdditionalProperties <- translated.AdditionalProperties
                rootSchema.Discriminator <- translated.Discriminator
                rootSchema.Default <- translated.Default
#if !NET10_0_OR_GREATER
                rootSchema.Nullable <- translated.Nullable
#endif
                for kv in translated.Properties do
                    rootSchema.Properties.[kv.Key] <- kv.Value
                for req in translated.Required do
                    rootSchema.Required.Add(req) |> ignore
                for s in translated.AnyOf do
                    rootSchema.AnyOf.Add(s)
                for s in translated.OneOf do
                    rootSchema.OneOf.Add(s)
                for e in translated.Enum do
                    rootSchema.Enum.Add(e)
#if NET10_0_OR_GREATER
                document |> Option.iter (fun d -> d.AddComponent(rootTypeId, rootSchema) |> ignore)
#endif
                rootSchema

        (result, componentSchemas |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

    /// Translate a SchemaDocument to an OpenApiSchema and component schemas.
    /// Self-refs and component references are unbound (no host document) — suitable for
    /// structural inspection but not for live OpenAPI document generation, where
    /// `translateForDocument` must be used instead so references actually resolve.
    let translate (doc: SchemaDocument) : OpenApiSchema * Map<string, OpenApiSchema> =
        translateCore doc "root" None

#if NET10_0_OR_GREATER
    /// Translate a SchemaDocument, binding component and self-ref references to a live
    /// OpenApiDocument so they resolve correctly, and registering component schemas
    /// — including the root schema itself, under `rootTypeId`, whenever there are any
    /// definitions — into `document.Components.Schemas`.
    let translateForDocument (doc: SchemaDocument) (rootTypeId: string) (document: OpenApiDocument) : OpenApiSchema * Map<string, OpenApiSchema> =
        translateCore doc rootTypeId (Some document)
#endif
```

- [ ] **Step 4: Run the new unit tests to verify they pass**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0 --filter "documentBinding"`

Expected: both tests PASS.

- [ ] **Step 5: Run the full OpenApi test project on both TFMs to confirm no regressions**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net9.0`
Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0`

Expected: all tests PASS on both, **except** the `endToEnd` tests from Task 2 on net10, which are still expected to FAIL here — the translator is fixed, but `FSharpSchemaTransformer` hasn't been wired to use `translateForDocument` yet (that's Task 4).

- [ ] **Step 6: Commit**

```bash
git add src/FSharp.Data.JsonSchema.OpenApi/OpenApiSchemaTranslator.fs test/FSharp.Data.JsonSchema.OpenApi.Tests/TranslatorTests.fs
git commit -m "fix: bind OpenApiSchemaTranslator references to a live document (#30)

Adds translateForDocument, which registers component schemas (including
the root schema itself under a supplied rootTypeId) into a live
OpenApiDocument and binds references to it via document.AddComponent /
OpenApiSchemaReference(id, document), so .Target actually resolves.
Also fixes self-ref (\"#\") ids: they previously always resolved to the
literal string \"root\" (rootSchema.Title was never set), which would
have collided across different self-recursive types sharing a document
once references were actually registered. translate(doc) is unchanged
for existing callers."
```

---

### Task 4: Wire `FSharpSchemaTransformer` to the live document

**Files:**
- Modify: `src/FSharp.Data.JsonSchema.OpenApi/FSharpSchemaTransformer.fs`

**Interfaces:**
- Consumes: `OpenApiSchemaTranslator.translateForDocument` (from Task 3), `OpenApiSchemaTranslator.translate` (unchanged), `SchemaGeneratorConfig.TypeIdResolver: Type -> string` (existing, from `FSharp.Data.JsonSchema.Core`).
- Produces: no new public surface — `FSharpSchemaTransformer`'s public constructors and `IOpenApiSchemaTransformer` implementation are unchanged.

- [ ] **Step 1: Replace the `TransformAsync` member**

In `src/FSharp.Data.JsonSchema.OpenApi/FSharpSchemaTransformer.fs`, find:

```fsharp
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync(schema, context, _cancellationToken) =
            let ty = context.JsonTypeInfo.Type
            if isFSharpType ty then
                let doc = SchemaAnalyzer.analyze config ty
                let (translatedRoot, componentSchemas) = OpenApiSchemaTranslator.translate doc

                // Mutate the provided schema in-place
                copySchemaInto translatedRoot schema

                // Register component schemas
                // The transformer context doesn't expose document components directly,
                // so we attach definitions as nested anyOf references.
                // In a real integration, the document transformer or middleware
                // would register these in components/schemas.

                Task.CompletedTask
            else
                Task.CompletedTask
```

Replace with:

```fsharp
    interface IOpenApiSchemaTransformer with
        member _.TransformAsync(schema, context, _cancellationToken) =
            let ty = context.JsonTypeInfo.Type
            if isFSharpType ty then
                let doc = SchemaAnalyzer.analyze config ty
#if NET10_0_OR_GREATER
                let rootTypeId = config.TypeIdResolver ty
                let (translatedRoot, _componentSchemas) =
                    match context.Document with
                    | null -> OpenApiSchemaTranslator.translate doc
                    | document -> OpenApiSchemaTranslator.translateForDocument doc rootTypeId document
#else
                let (translatedRoot, _componentSchemas) = OpenApiSchemaTranslator.translate doc
#endif

                // Mutate the provided schema in-place
                copySchemaInto translatedRoot schema

                Task.CompletedTask
            else
                Task.CompletedTask
```

(`context.Document` is only ever `null` when ASP.NET invokes schema generation outside the real document-build flow — an internal-only edge case, out of scope; that path keeps today's behavior.)

- [ ] **Step 2: Run the Task 2 end-to-end tests and confirm they now PASS on net10**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0 --filter "endToEnd"`

Expected: both tests PASS — the `Circle`/`Rectangle`/`Point` and `Leaf`/`Branch`/`TreeNode` component-schema assertions now succeed, and the request that used to 500 now returns 200.

- [ ] **Step 3: Confirm they still PASS on net9**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net9.0 --filter "endToEnd"`

Expected: both tests still PASS (net9 path untouched).

- [ ] **Step 4: Run the full OpenApi test project on both TFMs**

Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net9.0`
Run: `dotnet test test/FSharp.Data.JsonSchema.OpenApi.Tests/FSharp.Data.JsonSchema.OpenApi.Tests.fsproj -f net10.0`

Expected: all tests PASS on both.

- [ ] **Step 5: Commit**

```bash
git add src/FSharp.Data.JsonSchema.OpenApi/FSharpSchemaTransformer.fs
git commit -m "fix: register OpenAPI component schemas into the live document (#30)

FSharpSchemaTransformer now calls translateForDocument with the live
context.Document and the root type's id (via SchemaGeneratorConfig's
existing TypeIdResolver) on net10, so component schemas are actually
registered and references resolve instead of dangling. This is what
made Microsoft.AspNetCore.OpenApi 10.0.3+ throw on any DU or
self-recursive type."
```

---

### Task 5: Version bump and release notes

**Files:**
- Modify: `src/Directory.Build.props`
- Modify: `RELEASE_NOTES.md`

**Interfaces:** None.

- [ ] **Step 1: Bump the shared version**

In `src/Directory.Build.props`, find:

```xml
    <VersionPrefix>3.0.1</VersionPrefix>
```

Replace with:

```xml
    <VersionPrefix>3.1.0</VersionPrefix>
```

- [ ] **Step 2: Add release notes**

In `RELEASE_NOTES.md`, insert at the very top of the file, above the existing `### FSharp.Data.JsonSchema.NJsonSchema 3.0.1` section:

```markdown
### FSharp.Data.JsonSchema.OpenApi 3.1.0

* Fix `InvalidOperationException: The input schema must be an OpenApiSchema or OpenApiSchemaReference` thrown by `Microsoft.AspNetCore.OpenApi` 10.0.3+ for any endpoint whose request/response type is (or contains) an F# discriminated union or a self-recursive type (#30)
* `FSharpSchemaTransformer` now registers component schemas into the live `OpenApiDocument` on net10 instead of leaving them unregistered with dangling references
* Fix self-recursive types (e.g. a tree or linked-list shaped DU) all resolving their self-reference to the same hardcoded component id; each now gets its own correctly-named component

### FSharp.Data.JsonSchema.NJsonSchema 3.1.0

* No changes (version bump for consistency)

### FSharp.Data.JsonSchema.Core 3.1.0

* No changes (version bump for consistency)

```

- [ ] **Step 3: Verify the version flows through a clean build**

Run: `dotnet build src/FSharp.Data.JsonSchema.OpenApi/FSharp.Data.JsonSchema.OpenApi.fsproj -c Release`

Then: `find src/FSharp.Data.JsonSchema.OpenApi/bin/Release -iname "*.nupkg"`

Expected: a file named `FSharp.Data.JsonSchema.OpenApi.3.1.0.nupkg` (or the repo owner's fork-prefixed equivalent) exists.

- [ ] **Step 4: Commit**

```bash
git add src/Directory.Build.props RELEASE_NOTES.md
git commit -m "chore: bump version to 3.1.0 for #30 fix"
```

---

### Task 6: Full solution verification

**Files:** None modified — verification only.

- [ ] **Step 1: Run the full solution test suite on both target frameworks**

Run: `dotnet test -f net9.0`
Run: `dotnet test -f net10.0`

Expected: all tests pass across `FSharp.Data.JsonSchema.Core.Tests`, `FSharp.Data.JsonSchema.NJsonSchema.Tests` (or equivalently-named main test project), and `FSharp.Data.JsonSchema.OpenApi.Tests` — this must include the pre-existing 573-test baseline plus the new tests added in Tasks 2 and 3, all green, on both TFMs.

- [ ] **Step 2: Build the full solution in Release configuration**

Run: `dotnet build -c Release`

Expected: builds cleanly with no new warnings introduced by this change (pre-existing `NU1903` advisory warnings on transitive `Microsoft.OpenApi`/`System.Text.Json` versions are expected and out of scope).

- [ ] **Step 3: Push the branch**

```bash
git push -u origin 30-openapi-dangling-schema-ref
```

- [ ] **Step 4: Open a pull request referencing #30**

```bash
gh pr create --title "Fix dangling OpenApiSchemaReference on net10 (#30)" --body "$(cat <<'EOF'
## Summary
- FSharpSchemaTransformer never registered its component schemas into the live OpenApiDocument on net10, and built every reference with a null host document, so references never resolved once Microsoft.AspNetCore.OpenApi 10.0.3+ started walking and resolving the schema tree strictly after transformers run.
- Also fixes self-recursive types all resolving their self-reference to the same hardcoded component id ("root").
- Scoped to the net10 / Microsoft.OpenApi 2.x path; net9 is unaffected and untouched.

Closes #30.

## Test plan
- [x] New end-to-end test reproduces the exact #30 exception on net10 before the fix, passes after
- [x] New unit tests on OpenApiSchemaTranslator.translateForDocument verify references actually resolve (.Target is non-null) and self-refs bind to the supplied root type id, not "root"
- [x] Full existing suite green on net9.0 and net10.0

Design doc: docs/superpowers/specs/2026-07-25-openapi-dangling-schema-ref-design.md
EOF
)"
```

## Self-Review Notes

- **Spec coverage:** translator fix (Task 3), transformer wiring (Task 4), package version pin (Task 1), end-to-end test coverage (Task 2), self-ref id collision fix (Task 3), version bump (Task 5) — all covered. Net9-untouched constraint verified empirically (Task 2 Step 4) rather than assumed.
- **Deviation from the spec's literal wording:** the spec described a post-hoc "walk the schema tree and rewrite dangling references" approach (`registerComponents`). This plan instead threads the live `OpenApiDocument` directly into `translateCore` so references are built already-bound and components are registered as they're produced — same requirements satisfied (components registered, references resolve, self-ref id fixed), with less code and no need for a separate recursive tree-walker. Also adds one thing the spec didn't spell out explicitly: the root schema itself must also be registered as a component under `rootTypeId` (gated on `doc.Definitions` being non-empty) — without this, a `"#"` self-ref would point at a component id that was never actually registered, which would still leave `.Target` null after just the mkRefSchema fix.
- **Type consistency:** `translateForDocument`'s signature (`SchemaDocument -> string -> OpenApiDocument -> OpenApiSchema * Map<string, OpenApiSchema>`) is identical between its Task 3 definition and Task 4's call site. `translate`'s signature is unchanged everywhere.
