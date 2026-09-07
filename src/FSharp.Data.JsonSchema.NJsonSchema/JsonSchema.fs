namespace FSharp.Data.JsonSchema

open System
open System.Collections.Generic
open Microsoft.FSharp.Reflection
open Namotion.Reflection
open NJsonSchema
open NJsonSchema.Generation

/// Microsoft.FSharp.Reflection helpers
/// see https://github.com/baronfel/Newtonsoft.Json.FSharp.Idiomatic/blob/master/src/Newtonsoft.Json.FSharp.Idiomatic/Newtonsoft.Json.FSharp.Idiomatic.fs#L52-L54
module Reflection =
    let allCasesEmpty (y: Type) =
        y
        |> FSharpType.GetUnionCases
        |> Array.forall (fun case -> case.GetFields() |> Array.isEmpty)

    let isList (y: Type) =
        y.IsGenericType
        && (typedefof<List<_>>.Equals(y.GetGenericTypeDefinition())
            || typedefof<list<_>>.Equals(y.GetGenericTypeDefinition()))

    let isOption (y: Type) =
        y.IsGenericType
        &&
        let def = y.GetGenericTypeDefinition()
        def = typedefof<_ option> || def = typedefof<voption<_>>

    let isObjOption (y: Type) =
        y = typedefof<_ option> || y = typedefof<voption<_>>

    let isPrimitive (ty: Type) =
        ty.IsPrimitive || ty = typeof<String> || ty = typeof<Decimal>

    let isIntegerEnum (ty: Type) =
        ty.IsEnum && ty.GetEnumUnderlyingType() = typeof<int>

[<Sealed>]
type internal SchemaNameGenerator() =
    inherit DefaultSchemaNameGenerator()

    override this.Generate(ty: Type) =
        let cachedType = ty.ToCachedType()

        if Reflection.isObjOption cachedType.Type then
            "Any"
        elif Reflection.isOption cachedType.Type then
            this.Generate(cachedType.GenericArguments[0].OriginalType)
        else
            base.Generate(ty)

[<AbstractClass; Sealed>]
type Generator private () =
    static let cache =
        Collections.Concurrent.ConcurrentDictionary<(string * Core.UnionEncodingStyle) * Type, JsonSchema>()

    // Namotion.Reflection (used by SchemaNameGenerator.Generate below, and internally by
    // NJsonSchema's own base generator) keeps global, non-thread-safe type-metadata caches.
    // Concurrent schema generation for different types — e.g. under a parallel test runner —
    // can corrupt them and throw "_type is not initialized" from CachedType.get_Type(). Since
    // we don't control that dependency's internals, serialize our own entry point into it.
    static let generationLock = obj ()

    static member internal CreateInternal(config: Core.SchemaGeneratorConfig) =
        let nameGen = SchemaNameGenerator()
        // Collect all types referenced from a root type, keyed by their typeId.
        let collectTypeMap (rootType: Type) =
            let visited = HashSet<Type>()
            let typeByName = Dictionary<string, Type>()
            let rec walk (t: Type) =
                if visited.Add t then
                    let typeId = config.TypeIdResolver t
                    if not (String.IsNullOrEmpty typeId) then
                        typeByName[typeId] <- t
                    if FSharpType.IsRecord(t, true) then
                        for f in FSharpType.GetRecordFields(t, true) do walk f.PropertyType
                    elif FSharpType.IsUnion(t, true) then
                        for c in FSharpType.GetUnionCases(t, true) do
                            for f in c.GetFields() do walk f.PropertyType
                    elif t.IsArray then
                        walk (t.GetElementType())
                    elif t.IsGenericType then
                        for a in t.GetGenericArguments() do walk a
            walk rootType
            typeByName

        fun (ty: Type) ->
            lock generationLock (fun () ->
                let doc = Core.SchemaAnalyzer.analyze config ty
                let schema = NJsonSchemaTranslator.translate doc
                // Set title using the same logic as the old SchemaNameGenerator
                // Don't set title for bare option/voption types (they produce empty schemas)
                match doc.Root with
                | Core.SchemaNode.Any -> ()
                | _ ->
                    let title = nameGen.Generate(ty) |> config.TypeNamingPolicy
                    if not (String.IsNullOrEmpty title) then
                        schema.Title <- title
                // Add empty description for .NET enums (matching NJsonSchema behavior)
                if Reflection.isIntegerEnum ty then
                    schema.Description <- ""
                // Set additionalProperties = false for fieldless DU enums
                if FSharpType.IsUnion(ty) && Reflection.allCasesEmpty ty then
                    schema.AllowAdditionalProperties <- false
                // Apply post-processing to definitions based on their F# types
                let typeMap = collectTypeMap ty
                for kv in schema.Definitions do
                    match typeMap.TryGetValue(kv.Key) with
                    | true, defTy ->
                        if Reflection.isIntegerEnum defTy then
                            kv.Value.Description <- ""
                        elif FSharpType.IsUnion(defTy, true) && Reflection.allCasesEmpty defTy then
                            kv.Value.AllowAdditionalProperties <- false
                    | _ -> ()
                // Apply DataAnnotation attributes from record fields
                let applyAnnotations (recordTy: Type) (targetSchema: JsonSchema) =
                    if FSharpType.IsRecord(recordTy, true) then
                        for field in FSharpType.GetRecordFields(recordTy, true) do
                            let propName = config.PropertyNamingPolicy field.Name
                            match targetSchema.Properties.TryGetValue(propName) with
                            | true, prop ->
                                for attr in field.GetCustomAttributes(true) do
                                    match attr with
                                    | :? System.ComponentModel.DataAnnotations.RequiredAttribute ->
                                        prop.MinLength <- 1
                                    | :? System.ComponentModel.DataAnnotations.MaxLengthAttribute as ml ->
                                        prop.MaxLength <- Nullable ml.Length
                                    | :? System.ComponentModel.DataAnnotations.RangeAttribute as r ->
                                        prop.Minimum <- Nullable (Convert.ToDecimal r.Minimum)
                                        prop.Maximum <- Nullable (Convert.ToDecimal r.Maximum)
                                    | _ -> ()
                            | _ -> ()
                applyAnnotations ty schema
                for kv in schema.Definitions do
                    match typeMap.TryGetValue(kv.Key) with
                    | true, defTy -> applyAnnotations defTy kv.Value
                    | _ -> ()
                schema)
    static member internal CreateInternal(?casePropertyName, ?unionEncoding) =
        let casePropertyName' = defaultArg casePropertyName FSharp.Data.Json.DefaultCasePropertyName
        let config =
            { Core.SchemaGeneratorConfig.defaults with
                DiscriminatorPropertyName = casePropertyName'
                UnionEncoding = defaultArg unionEncoding Core.SchemaGeneratorConfig.defaults.UnionEncoding }
        Generator.CreateInternal config

    /// Creates a generator using the specified casePropertyName and unionEncoding.
    static member Create(?casePropertyName, ?unionEncoding) =
        Generator.CreateInternal(?casePropertyName = casePropertyName, ?unionEncoding = unionEncoding)
    static member Create(config) = Generator.CreateInternal(config)

    /// Creates a memoized generator that stores generated schemas in a global cache by Type and casePropertyName.
    static member CreateMemoized(config: Core.SchemaGeneratorConfig) =
        let casePropertyName = config.DiscriminatorPropertyName
        let unionEncoding = config.UnionEncoding
        fun ty ->
            cache.GetOrAdd(
                ((casePropertyName, unionEncoding), ty),
                let generator =
                    Generator.CreateInternal(casePropertyName, unionEncoding)

                generator ty
            )
    static member CreateMemoized(?casePropertyName, ?unionEncoding) =
        let casePropertyName =
            defaultArg casePropertyName FSharp.Data.Json.DefaultCasePropertyName
        let unionEncoding =
            defaultArg unionEncoding Core.SchemaGeneratorConfig.defaults.UnionEncoding
        {
            Core.SchemaGeneratorConfig.defaults with
                DiscriminatorPropertyName = casePropertyName
                UnionEncoding = unionEncoding
        }

module Validation =

    let validate schema (json: string) =
        let validator = Validation.JsonSchemaValidator()
        let errors = validator.Validate(json, schema)

        if errors.Count > 0 then
            Error(Seq.toArray errors)
        else
            Ok()

    type FSharp.Data.Json with

        static member DeserializeWithValidation<'T>(json, schema) =
            validate schema json
            |> Result.map (fun _ -> FSharp.Data.Json.Deserialize<'T> json)

        static member DeserializeWithValidation<'T>(json, schema, casePropertyName) =
            validate schema json
            |> Result.map (fun _ -> FSharp.Data.Json.Deserialize<'T>(json, casePropertyName))
