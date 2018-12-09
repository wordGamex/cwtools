namespace CWTools.Games.EU4
open CWTools.Localisation
open CWTools.Validation.ValidationCore
open CWTools.Games.Files
open CWTools.Games
open CWTools.Common
open FSharp.Collections.ParallelSeq
open CWTools.Localisation.EU4Localisation
open CWTools.Utilities.Utils
open CWTools.Utilities.Position
open System.IO
open CWTools.Validation.Common.CommonValidation
// open CWTools.Validation.Rules
open CWTools.Parser.ConfigParser
open CWTools.Common.EU4Constants
open CWTools.Validation.EU4.EU4Rules
open CWTools.Validation.Rules
open CWTools.Process.EU4Scopes
open CWTools.Common
open CWTools.Process.Scopes
open CWTools.Validation.EU4
open System.Text
open CWTools.Validation.Rules
open CWTools.Validation.EU4.EU4LocalisationValidation
open CWTools.Games.LanguageFeatures

type EmbeddedSettings = {
    embeddedFiles : (string * string) list
    modifiers : Modifier list
    cachedResourceData : (Resource * Entity) list
}

type ValidationSettings = {
    langs : Lang list
    validateVanilla : bool
}
type RulesSettings = {
    ruleFiles : (string * string) list
    validateRules : bool
}
type EU4Settings = {
    rootDirectory : string
    embedded : EmbeddedSettings
    validation : ValidationSettings
    rules : RulesSettings option
    scriptFolders : string list option
}

type EU4Game(settings : EU4Settings) =

    let scriptFolders = settings.scriptFolders |> Option.defaultValue scriptFolders

    let fileManager = FileManager(settings.rootDirectory, None, FilesScope.All, scriptFolders, "europa universalis iv", Encoding.GetEncoding(1252))

    // let computeEU4Data (e : Entity) = EU4ComputedData()
    let mutable infoService : FoldRules<_> option = None
    let mutable completionService : CompletionService<_> option = None
    let resourceManager = ResourceManager(EU4Compute.computeEU4Data (fun () -> infoService))
    let resources = resourceManager.Api
    let validatableFiles() = resources.ValidatableFiles
    let mutable localisationAPIs : (bool * ILocalisationAPI) list = []
    let allLocalisation() = localisationAPIs |> List.map snd
    let validatableLocalisation() = localisationAPIs |> List.choose (fun (validate, api) -> if validate then Some api else None)
    let mutable localisationErrors : CWError list option = None
    let mutable localisationKeys = []

    let getEmbeddedFiles() = settings.embedded.embeddedFiles |> List.map (fun (fn, f) -> "embedded", "embeddedfiles/" + fn, f)

    let updateLocalisation() =
        localisationAPIs <-
            let locs = resources.GetResources()
                        |> List.choose (function |FileWithContentResource (_, e) -> Some e |_ -> None)
                        |> List.filter (fun f -> f.overwrite <> Overwritten && f.extension = ".yml")
                        |> List.groupBy (fun f -> f.validate)
                        |> List.map (fun (b, fs) -> b, fs |> List.map (fun f -> f.filepath, f.filetext) |> EU4LocalisationService)
            let allLocs = locs |> List.collect (fun (b,l) -> (STL STLLang.Default :: settings.validation.langs)|> List.map (fun lang -> b, l.Api(lang)))
            allLocs
        localisationKeys <-allLocalisation() |> List.groupBy (fun l -> l.GetLang) |> List.map (fun (k, g) -> k, g |>List.collect (fun ls -> ls.GetKeys) |> Set.ofList )
        //taggedLocalisationKeys <- allLocalisation() |> List.groupBy (fun l -> l.GetLang) |> List.map (fun (k, g) -> k, g |> List.collect (fun ls -> ls.GetKeys) |> List.fold (fun (s : LocKeySet) v -> s.Add v) (LocKeySet.Empty(InsensitiveStringComparer())) )
        //let validatableEntries = validatableLocalisation() |> List.groupBy (fun l -> l.GetLang) |> List.map (fun (k, g) -> k, g |> List.collect (fun ls -> ls.ValueMap |> Map.toList) |> Map.ofList)
        //lookup.proccessedLoc <- validatableEntries |> List.map (fun f -> processLocalisation lookup.scriptedEffects lookup.scriptedLoc lookup.definedScriptVariables (EntitySet (resources.AllEntities())) f taggedLocalisationKeys)
        //lookup.proccessedLoc <- validatableEntries |> List.map (fun f -> processLocalisation lookup.scriptedEffects lookup.scriptedLoc lookup.definedScriptVariables (EntitySet (resources.AllEntities())) f taggedKeys)
        //TODO: Add processed loc bacck
    let lookup = Lookup<Scope>()
    let mutable ruleApplicator : RuleApplicator<Scope> option = None
    let validationSettings = {
        validators = [ validateMixedBlocks, "mixed"; ]
        experimentalValidators = []
        heavyExperimentalValidators = []
        experimental = false
        fileValidators = []
        resources = resources
        lookup = lookup
        lookupValidators = []
        ruleApplicator = None
        useRules = true
        debugRulesOnly = false
        localisationKeys = (fun () -> localisationKeys)
        localisationValidators = [valOpinionModifierLocs; valStaticModifierLocs; valTimedModifierLocs;
                            valEventModifierLocs; valTriggeredModifierLocs; valProvinceTriggeredModifierLocs;
                            valUnitTypeLocs; valAdvisorTypeLocs; valTradeGoodLocs; valTradeCompanyLocs;
                            valTradeCompanyInvestmentLocs; valTradeNodeLocs; valTradingPolicyLocs;
                            valTradeCenterLocs; valCasusBelliLocs; valWarGoalLocs ]
    }

    let mutable validationManager = ValidationManager(validationSettings)
    let validateAll shallow newEntities = validationManager.Validate(shallow, newEntities)

    let localisationCheck (entities : struct (Entity * Lazy<EU4ComputedData>) list) = validationManager.ValidateLocalisation(entities)

    let updateModifiers() =
        lookup.coreEU4Modifiers <- settings.embedded.modifiers
    let updateScriptedEffects(rules :RootRule<Scope> list) =
        let effects =
            rules |> List.choose (function |AliasRule("effect", r) -> Some r |_ -> None)
        let ruleToEffect(r,o) =
            let name =
                match r with
                |LeafRule(ValueField(Specific n),_) -> n
                |NodeRule(ValueField(Specific n),_) -> n
                |_ -> ""
            DocEffect(name, o.requiredScopes, EffectType.Effect, o.description |> Option.defaultValue "", "")
        (effects |> List.map ruleToEffect  |> List.map (fun e -> e :> Effect)) @ (scopedEffects |> List.map (fun e -> e :> Effect))

    let updateScriptedTriggers(rules :RootRule<Scope> list) =
        let effects =
            rules |> List.choose (function |AliasRule("trigger", r) -> Some r |_ -> None)
        let ruleToTrigger(r,o) =
            let name =
                match r with
                |LeafRule(ValueField(Specific n),_) -> n
                |NodeRule(ValueField(Specific n),_) -> n
                |_ -> ""
            DocEffect(name, o.requiredScopes, EffectType.Trigger, o.description |> Option.defaultValue "", "")
        (effects |> List.map ruleToTrigger |> List.map (fun e -> e :> Effect)) @ (scopedEffects |> List.map (fun e -> e :> Effect))

    let updateTypeDef =
        let mutable simpleEnums = []
        let mutable complexEnums = []
        let mutable tempTypes = []
        let mutable tempTypeMap = [("", StringSet.Empty(InsensitiveStringComparer()))] |> Map.ofList
        let mutable tempEnumMap = [("", StringSet.Empty(InsensitiveStringComparer()))] |> Map.ofList
        (fun rulesSettings ->
            let timer = new System.Diagnostics.Stopwatch()
            timer.Start()
            match rulesSettings with
            |Some rulesSettings ->
                let rules, types, enums, complexenums = rulesSettings.ruleFiles |> List.fold (fun (rs, ts, es, ces) (fn, ft) -> let r2, t2, e2, ce2 = parseConfig parseScope allScopes Scope.Any fn ft in rs@r2, ts@t2, es@e2, ces@ce2) ([], [], [], [])
                lookup.scriptedEffects <- updateScriptedEffects rules
                lookup.scriptedTriggers <- updateScriptedTriggers rules
                lookup.typeDefs <- types
                let rulesWithMod = rules @ (lookup.coreEU4Modifiers |> List.map (fun c -> AliasRule ("modifier", NewRule(LeafRule(specificField c.tag, ValueField (ValueType.Float (-1E+12, 1E+12))), {min = 0; max = 100; leafvalue = false; description = None; pushScope = None; replaceScopes = None; severity = None; requiredScopes = []}))))
                lookup.configRules <- rulesWithMod
                simpleEnums <- enums
                complexEnums <- complexenums
                tempTypes <- types
                eprintfn "Update config rules def: %i" timer.ElapsedMilliseconds; timer.Restart()
            |None -> ()
            let complexEnumDefs = CWTools.Validation.Rules.getEnumsFromComplexEnums complexEnums (resources.AllEntities() |> List.map (fun struct(e,_) -> e))
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            let allEnums = simpleEnums @ complexEnumDefs
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            lookup.enumDefs <- allEnums |> Map.ofList
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            tempEnumMap <- lookup.enumDefs |> Map.toSeq |> PSeq.map (fun (k, s) -> k, StringSet.Create(InsensitiveStringComparer(), (s))) |> Map.ofSeq
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            let loc = localisationKeys
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            let files = resources.GetFileNames() |> Set.ofList
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            let tempRuleApplicator = RuleApplicator<Scope>(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, Collections.Map.empty, loc, files, lookup.scriptedTriggersMap, lookup.scriptedEffectsMap, Scope.Any, changeScope, defaultContext, EU4 EU4Lang.Default)
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            let allentities = resources.AllEntities() |> List.map (fun struct(e,_) -> e)
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            lookup.typeDefInfo <- CWTools.Validation.Rules.getTypesFromDefinitions tempRuleApplicator tempTypes allentities
            let tempFoldRules = (FoldRules<Scope>(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, Collections.Map.empty, loc, files, lookup.scriptedTriggersMap, lookup.scriptedEffectsMap, tempRuleApplicator, changeScope, defaultContext, Scope.Any, STL STLLang.Default))
            lookup.varDefInfo <- getDefinedVariables tempFoldRules (resources.AllEntities() |> List.map (fun struct(e,_) -> e))
            let varMap = lookup.varDefInfo |> Map.toSeq |> PSeq.map (fun (k, s) -> k, StringSet.Create(InsensitiveStringComparer(), (List.map fst s))) |> Map.ofSeq

            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            tempTypeMap <- lookup.typeDefInfo |> Map.toSeq |> PSeq.map (fun (k, s) -> k, StringSet.Create(InsensitiveStringComparer(), (s |> List.map fst))) |> Map.ofSeq
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            completionService <- Some (CompletionService(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, varMap, loc, files, lookup.scriptedTriggersMap, lookup.scriptedEffectsMap, changeScope, defaultContext, Scope.Any, oneToOneScopesNames, EU4 EU4Lang.Default))
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            ruleApplicator <- Some (RuleApplicator<Scope>(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, varMap, loc, files, lookup.scriptedTriggersMap, lookup.scriptedEffectsMap, Scope.Any, changeScope, defaultContext, EU4 EU4Lang.Default))
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            infoService <- Some (FoldRules<Scope>(lookup.configRules, lookup.typeDefs, tempTypeMap, tempEnumMap, varMap, loc, files, lookup.scriptedTriggersMap, lookup.scriptedEffectsMap, ruleApplicator.Value, changeScope, defaultContext, Scope.Any, EU4 EU4Lang.Default))
            // eprintfn "Refresh rule caches time: %i" timer.ElapsedMilliseconds; timer.Restart()
            validationManager <- ValidationManager({validationSettings with ruleApplicator = ruleApplicator})
        )
    let refreshRuleCaches(rules) =
        updateTypeDef(rules)

    let parseErrors() =
        resources.GetResources()
            |> List.choose (function |EntityResource (_, e) -> Some e |_ -> None)
            |> List.choose (fun r -> r.result |> function |(Fail (result)) when r.validate -> Some (r.filepath, result.error, result.position)  |_ -> None)
    let mutable errorCache = Map.empty

    let updateFile (shallow : bool) filepath (filetext : string option) =
        eprintfn "%s" filepath
        let timer = new System.Diagnostics.Stopwatch()
        timer.Start()
        let res =
            match filepath with
            |x when x.EndsWith (".yml") ->
                updateLocalisation()
                let les = (localisationCheck (resources.ValidatableEntities())) //@ globalLocalisation()
                localisationErrors <- Some les
                //globalLocalisation()
                []
            | _ ->
                let filepath = Path.GetFullPath(filepath)
                let file = filetext |> Option.defaultWith (fun () -> File.ReadAllText(filepath, Encoding.GetEncoding(1252)))
                let rootedpath = filepath.Substring(filepath.IndexOf(fileManager.ScopeDirectory) + (fileManager.ScopeDirectory.Length))
                let logicalpath = fileManager.ConvertPathToLogicalPath rootedpath
                let resource = makeEntityResourceInput fileManager filepath file

                //eprintfn "%s %s" logicalpath filepath
                let newEntities = resources.UpdateFile (resource) |> List.map snd
                // match filepath with
                // |x when x.Contains("scripted_triggers") -> updateScriptedTriggers()
                // |x when x.Contains("scripted_effects") -> updateScriptedEffects()
                // |x when x.Contains("static_modifiers") -> updateStaticodifiers()
                // |_ -> ()
                // updateDefinedVariables()
                // validateAll true newEntities @ localisationCheck newEntities
                match shallow with
                    |true ->
                        let (shallowres, _) = (validateAll shallow newEntities)
                        let shallowres = shallowres @ (localisationCheck newEntities)
                        let deep = errorCache |> Map.tryFind filepath |> Option.defaultValue []
                        shallowres @ deep
                    |false ->
                        let (shallowres, deepres) = (validateAll shallow newEntities)
                        let shallowres = shallowres @ (localisationCheck newEntities)
                        errorCache <- errorCache.Add(filepath, deepres)
                        shallowres @ deepres


        eprintfn "Update Time: %i" timer.ElapsedMilliseconds
        res

    do
        eprintfn "Parsing %i files" (fileManager.AllFilesByPath().Length)
        let files = fileManager.AllFilesByPath()
        let filteredfiles = if settings.validation.validateVanilla then files else files |> List.choose (function |FileResourceInput f -> Some (FileResourceInput f) |EntityResourceInput f -> (if f.scope = "vanilla" then Some (EntityResourceInput {f with validate = false}) else Some (EntityResourceInput f) )|_ -> None)
        resources.UpdateFiles(filteredfiles) |> ignore
        let embeddedFiles =
            settings.embedded.embeddedFiles
            |> List.map (fun (f, ft) -> f.Replace("\\","/"), ft)
            |> List.choose (fun (f, ft) -> if ft = "" then Some (FileResourceInput { scope = "embedded"; filepath = f; logicalpath = (fileManager.ConvertPathToLogicalPath f) }) else None)
        let disableValidate (r, e) : Resource * Entity =
            match r with
            |EntityResource (s, er) -> EntityResource (s, { er with validate = false; scope = "embedded" })
            |x -> x
            , {e with validate = false}

        // let embeddedFiles = settings.embedded.embeddedFiles |> List.map (fun (f, ft) -> if ft = "" then FileResourceInput { scope = "embedded"; filepath = f; logicalpath = (fileManager.ConvertPathToLogicalPath f) } else EntityResourceInput {scope = "embedded"; filepath = f; logicalpath = (fileManager.ConvertPathToLogicalPath f); filetext = ft; validate = false})
        let cached = settings.embedded.cachedResourceData |> List.map (fun (r, e) -> CachedResourceInput (disableValidate (r, e)))
        let embedded = embeddedFiles @ cached
        if fileManager.ShouldUseEmbedded then resources.UpdateFiles(embedded) |> ignore else ()

        // updateScriptedTriggers()
        // updateScriptedEffects()
        // updateStaticodifiers()
        // updateScriptedLoc()
        // updateDefinedVariables()
        updateModifiers()
        // updateTechnologies()
        updateLocalisation()
        updateTypeDef(settings.rules)
    interface IGame<EU4ComputedData, Scope> with
    //member __.Results = parseResults
        member __.ParserErrors() = parseErrors()
        member __.ValidationErrors() = let (s, d) = (validateAll false (resources.ValidatableEntities())) in s @ d
        member __.LocalisationErrors(force : bool) =
                let generate =
                    let les = (localisationCheck (resources.ValidatableEntities())) //@ globalLocalisation()
                    localisationErrors <- Some les
                    les
                match localisationErrors with
                |Some les -> if force then generate else les
                |None -> generate

        //member __.ValidationWarnings = warningsAll
        member __.Folders() = fileManager.AllFolders()
        member __.AllFiles() =
            resources.GetResources()
            // |> List.map
            //     (function
            //         |EntityResource (f, r) ->  r.result |> function |(Fail (result)) -> (r.filepath, false, result.parseTime) |Pass(result) -> (r.filepath, true, result.parseTime)
            //         |FileResource (f, r) ->  (r.filepath, false, 0L))
            //|> List.map (fun r -> r.result |> function |(Fail (result)) -> (r.filepath, false, result.parseTime) |Pass(result) -> (r.filepath, true, result.parseTime))
        member __.ScriptedTriggers() = lookup.scriptedTriggers
        member __.ScriptedEffects() = lookup.scriptedEffects
        member __.StaticModifiers() = [] //lookup.staticModifiers
        member __.UpdateFile shallow file text = updateFile shallow file text
        member __.AllEntities() = resources.AllEntities()
        member __.References() = References<_, Scope>(resources, Lookup(), (localisationAPIs |> List.map snd))
        member __.Complete pos file text = completion fileManager completionService resourceManager pos file text
        member __.ScopesAtPos pos file text = scopesAtPos fileManager resourceManager infoService Scope.Any pos file text |> Option.map (fun sc -> { OutputScopeContext.From = sc.From; Scopes = sc.Scopes; Root = sc.Root})
        member __.GoToType pos file text = getInfoAtPos fileManager resourceManager infoService lookup pos file text
        member __.FindAllRefs pos file text = findAllRefsFromPos fileManager resourceManager infoService pos file text
        member __.ReplaceConfigRules rules = refreshRuleCaches(Some { ruleFiles = rules; validateRules = true})
        member __.RefreshCaches() = refreshRuleCaches None
        member __.ForceRecompute() = resources.ForceRecompute()

            //member __.ScriptedTriggers = parseResults |> List.choose (function |Pass(f, p, t) when f.Contains("scripted_triggers") -> Some p |_ -> None) |> List.map (fun t -> )