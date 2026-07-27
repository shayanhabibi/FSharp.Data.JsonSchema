module FSharp.Data.JsonSchema.OpenApi.Tests.EndToEndTests

open Expecto
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
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

/// A second, differently-shaped self-recursive type, used to prove two
/// self-recursive types in the same document don't collide on component id.
type LinkedNode =
    | Empty
    | Node of value: int * next: LinkedNode

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

        test "OpenAPI document generation registers distinct components for two different self-recursive types" {
            let app =
                startApp (fun app ->
                    app.MapGet("/tree", System.Func<TreeNode>(fun () -> Leaf 1)) |> ignore
                    app.MapGet("/linked", System.Func<LinkedNode>(fun () -> Empty)) |> ignore
                )
            try
                let (status, body) = getOpenApiDocument app
                Expect.equal status System.Net.HttpStatusCode.OK "200 OK, not the InvalidOperationException from #30"
                use jsonDoc = JsonDocument.Parse body
#if NET10_0_OR_GREATER
                let root = jsonDoc.RootElement
                Expect.isTrue (hasSchema root "TreeNode") "TreeNode root registered as a component schema"
                Expect.isTrue (hasSchema root "Leaf") "Leaf registered as a component schema"
                Expect.isTrue (hasSchema root "Branch") "Branch registered as a component schema"
                Expect.isTrue (hasSchema root "LinkedNode") "LinkedNode root registered as a component schema"
                Expect.isTrue (hasSchema root "Empty") "Empty registered as a component schema"
                Expect.isTrue (hasSchema root "Node") "Node registered as a component schema"
#endif
                ()
            finally
                app.StopAsync().GetAwaiter().GetResult()
        }
    ]
