module internal Fantomas.CodePrinter

open System
open System.Text.RegularExpressions
open FSharp.Compiler.Text
open FSharp.Compiler.SourceCodeServices
open FSharp.Compiler.SyntaxTree
open Fantomas
open Fantomas.FormatConfig
open Fantomas.SourceParser
open Fantomas.SourceTransformer
open Fantomas.Context
open Fantomas.TriviaTypes
open Fantomas.TriviaContext
open Fantomas.AstExtensions

/// This type consists of contextual information which is important for formatting
/// Please avoid using this record as it can be the cause of unexpected behavior when used incorrectly
type ASTContext =
    { /// Current node is the first child of its parent
      IsFirstChild: bool
      /// Current node is a subnode deep down in an interface
      InterfaceRange: Range option
      /// This pattern matters for formatting extern declarations
      IsCStylePattern: bool
      /// Range operators are naked in 'for..in..do' constructs
      IsNakedRange: bool
      /// A field is rendered as union field or not
      IsUnionField: bool
      /// First type param might need extra spaces to avoid parsing errors on `<^`, `<'`, etc.
      IsFirstTypeParam: bool
      /// Inside a SynPat of MatchClause
      IsInsideMatchClausePattern: bool }
    static member Default =
        { IsFirstChild = false
          InterfaceRange = None
          IsCStylePattern = false
          IsNakedRange = false
          IsUnionField = false
          IsFirstTypeParam = false
          IsInsideMatchClausePattern = false }

let rec addSpaceBeforeParensInFunCall functionOrMethod arg (ctx: Context) =
    match functionOrMethod, arg with
    | SynExpr.TypeApp (e, _, _, _, _, _, _), _ -> addSpaceBeforeParensInFunCall e arg ctx
    | SynExpr.Paren _, _ -> true
    | SynExpr.Const _, _ -> true
    | UppercaseSynExpr, ConstUnitExpr -> ctx.Config.SpaceBeforeUppercaseInvocation
    | LowercaseSynExpr, ConstUnitExpr -> ctx.Config.SpaceBeforeLowercaseInvocation
    | SynExpr.Ident _, SynExpr.Ident _ -> true
    | UppercaseSynExpr, Paren _ -> ctx.Config.SpaceBeforeUppercaseInvocation
    | LowercaseSynExpr, Paren _ -> ctx.Config.SpaceBeforeLowercaseInvocation
    | _ -> true

let addSpaceBeforeParensInFunDef (spaceBeforeSetting: bool) (functionOrMethod: string) args =
    let isLastPartUppercase =
        let parts = functionOrMethod.Split '.'
        Char.IsUpper parts.[parts.Length - 1].[0]

    match functionOrMethod, args with
    | "new", _ -> false
    | _, PatParen _ -> spaceBeforeSetting
    | _, PatNamed _
    | _, SynPat.Wild _ -> true
    | _: string, _ -> not isLastPartUppercase
    | _ -> true

let rec genParsedInput astContext ast =
    match ast with
    | ImplFile im -> genImpFile astContext im
    | SigFile si -> genSigFile astContext si
    +> ifElseCtx lastWriteEventIsNewline sepNone sepNln

(*
    See https://github.com/fsharp/FSharp.Compiler.Service/blob/master/src/fsharp/ast.fs#L1518
    hs = hashDirectives : ParsedHashDirective list
    mns = modules : SynModuleOrNamespace list
*)
and genImpFile astContext (ParsedImplFileInput (hs, mns)) =
    col sepNone hs genParsedHashDirective
    +> (if hs.IsEmpty then sepNone else sepNln)
    +> col sepNln mns (genModuleOrNamespace astContext)

and genSigFile astContext (ParsedSigFileInput (hs, mns)) =
    col sepNone hs genParsedHashDirective
    +> (if hs.IsEmpty then sepNone else sepNln)
    +> col sepNln mns (genSigModuleOrNamespace astContext)

and genParsedHashDirective (ParsedHashDirective (h, s, r)) =
    let printArgument arg =
        match arg with
        | "" -> sepNone
        // Use verbatim string to escape '\' correctly
        | _ when arg.Contains("\\") -> !-(sprintf "@\"%O\"" arg)
        | _ -> !-(sprintf "\"%O\"" arg)

    let printIdent (ctx: Context) =
        Map.tryFind ParsedHashDirective_ ctx.TriviaMainNodes
        |> Option.defaultValue []
        |> List.tryFind (fun t -> RangeHelpers.rangeEq t.Range r)
        |> Option.bind
            (fun t ->
                match t.ContentItself with
                | Some (KeywordString c) -> Some c
                | _ -> None)
        |> function
            | Some kw -> !-kw
            | None -> col sepSpace s printArgument
        <| ctx

    !- "#" -- h +> sepSpace +> printIdent
    |> genTriviaFor ParsedHashDirective_ r

and genModuleOrNamespaceKind (kind: SynModuleOrNamespaceKind) =
    match kind with
    | SynModuleOrNamespaceKind.DeclaredNamespace -> !- "namespace "
    | SynModuleOrNamespaceKind.NamedModule -> !- "module "
    | SynModuleOrNamespaceKind.GlobalNamespace -> !- "namespace global"
    | SynModuleOrNamespaceKind.AnonModule -> sepNone

and genModuleOrNamespace astContext (ModuleOrNamespace (ats, px, ao, lids, mds, isRecursive, moduleKind)) =
    let sepModuleAndFirstDecl =
        let firstDecl = List.tryHead mds

        match firstDecl with
        | None -> sepNone
        | Some mdl ->
            let attrs =
                getRangesFromAttributesFromModuleDeclaration mdl

            sepNln
            +> sepNlnConsideringTriviaContentBeforeWithAttributesFor (synModuleDeclToFsAstType mdl) mdl.Range attrs

    let lidsFullRange =
        match lids with
        | [] -> range.Zero
        | (_, r) :: _ -> Range.unionRanges r (List.last lids |> snd)

    let moduleOrNamespace =
        genModuleOrNamespaceKind moduleKind
        +> opt sepSpace ao genAccess
        +> ifElse isRecursive (!- "rec ") sepNone
        +> col (!- ".") lids (fun (lid, r) -> genTriviaFor Ident_ r (!-lid))
        |> genTriviaFor LongIdent_ lidsFullRange

    // Anonymous module do have a single (fixed) ident in the LongIdent
    // We don't print the ident but it could have trivia assigned to it.
    let genTriviaForAnonModuleIdent =
        match lids with
        | [ (_, r) ] -> genTriviaFor Ident_ r sepNone
        | _ -> sepNone
        |> genTriviaFor LongIdent_ lidsFullRange

    genPreXmlDoc px
    +> genAttributes astContext ats
    +> ifElse (moduleKind = AnonModule) genTriviaForAnonModuleIdent moduleOrNamespace
    +> sepModuleAndFirstDecl
    +> genModuleDeclList astContext mds

and genSigModuleOrNamespace astContext (SigModuleOrNamespace (ats, px, ao, lids, mds, isRecursive, moduleKind)) =
    let sepModuleAndFirstDecl =
        let firstDecl = List.tryHead mds

        match firstDecl with
        | None -> sepNone
        | Some mdl ->
            match mdl with
            | SynModuleSigDecl.Types _ ->
                let attrs =
                    getRangesFromAttributesFromSynModuleSigDeclaration mdl

                sepNlnConsideringTriviaContentBeforeWithAttributesFor SynModuleSigDecl_Types mdl.Range attrs
            | SynModuleSigDecl.Val _ -> sepNlnConsideringTriviaContentBeforeForMainNode ValSpfn_ mdl.Range
            | _ -> sepNone
            +> sepNln

    let lidsFullRange =
        match lids with
        | [] -> range.Zero
        | (_, r) :: _ -> Range.unionRanges r (List.last lids |> snd)

    let moduleOrNamespace =
        genModuleOrNamespaceKind moduleKind
        +> opt sepSpace ao genAccess
        +> ifElse isRecursive (!- "rec ") sepNone
        +> col (!- ".") lids (fun (lid, r) -> genTriviaFor Ident_ r (!-lid))
        |> genTriviaFor LongIdent_ lidsFullRange

    genPreXmlDoc px
    +> genAttributes astContext ats
    +> ifElse (moduleKind = AnonModule) sepNone moduleOrNamespace
    +> sepModuleAndFirstDecl
    +> genSigModuleDeclList astContext mds

and genModuleDeclList astContext e =
    let rec collectItems
        (e: SynModuleDecl list)
        (finalContinuation: ColMultilineItem list -> ColMultilineItem list)
        : ColMultilineItem list =
        match e with
        | [] -> finalContinuation []
        | OpenL (xs, ys) ->
            let expr = col sepNln xs (genModuleDecl astContext)

            let r = List.head xs |> fun mdl -> mdl.Range
            // SynModuleDecl.Open cannot have attributes
            let sepNln =
                sepNlnConsideringTriviaContentBeforeForMainNode SynModuleDecl_Open r

            collectItems
                ys
                (fun ysItems ->
                    ColMultilineItem(expr, sepNln) :: ysItems
                    |> finalContinuation)

        | HashDirectiveL (xs, ys) ->
            let expr = col sepNln xs (genModuleDecl astContext)

            let r = List.head xs |> fun mdl -> mdl.Range
            // SynModuleDecl.HashDirective cannot have attributes
            let sepNln =
                sepNlnConsideringTriviaContentBeforeForMainNode SynModuleDecl_HashDirective r

            collectItems
                ys
                (fun ysItems ->
                    ColMultilineItem(expr, sepNln) :: ysItems
                    |> finalContinuation)

        | AttributesL (xs, y :: rest) ->
            let attrs =
                getRangesFromAttributesFromModuleDeclaration y

            let expr =
                col sepNln xs (genModuleDecl astContext)
                +> sepNlnConsideringTriviaContentBeforeWithAttributesFor (synModuleDeclToFsAstType y) y.Range attrs
                +> genModuleDecl astContext y

            let r = List.head xs |> fun mdl -> mdl.Range

            let sepNln =
                sepNlnConsideringTriviaContentBeforeForMainNode SynModuleDecl_Attributes r

            collectItems
                rest
                (fun restItems ->
                    ColMultilineItem(expr, sepNln) :: restItems
                    |> finalContinuation)

        | m :: rest ->
            let attrs =
                getRangesFromAttributesFromModuleDeclaration m

            let sepNln =
                sepNlnConsideringTriviaContentBeforeWithAttributesFor (synModuleDeclToFsAstType m) m.Range attrs

            let expr = genModuleDecl astContext m

            collectItems
                rest
                (fun restItems ->
                    ColMultilineItem(expr, sepNln) :: restItems
                    |> finalContinuation)

    collectItems e id |> colWithNlnWhenItemIsMultiline

and genSigModuleDeclList astContext (e: SynModuleSigDecl list) =
    let rec collectItems
        (e: SynModuleSigDecl list)
        (finalContinuation: ColMultilineItem list -> ColMultilineItem list)
        : ColMultilineItem list =
        match e with
        | [] -> finalContinuation []
        | SigOpenL (xs, ys) ->
            let expr =
                col sepNln xs (genSigModuleDecl astContext)

            let r = List.head xs |> fun mdl -> mdl.Range
            // SynModuleSigDecl.Open cannot have attributes
            let sepNln =
                sepNlnConsideringTriviaContentBeforeForMainNode SynModuleSigDecl_Open r

            collectItems
                ys
                (fun ysItems ->
                    ColMultilineItem(expr, sepNln) :: ysItems
                    |> finalContinuation)
        | s :: rest ->
            let attrs =
                getRangesFromAttributesFromSynModuleSigDeclaration s

            let sepNln =
                sepNlnConsideringTriviaContentBeforeWithAttributesFor (synModuleSigDeclToFsAstType s) s.Range attrs

            let expr = genSigModuleDecl astContext s

            collectItems
                rest
                (fun restItems ->
                    ColMultilineItem(expr, sepNln) :: restItems
                    |> finalContinuation)

    collectItems e id |> colWithNlnWhenItemIsMultiline

and genModuleDecl astContext (node: SynModuleDecl) =
    match node with
    | Attributes ats ->
        fun ctx ->
            let attributesExpr =
                // attributes can have trivia content before or after
                // we do extra detection to ensure no additional newline is introduced
                // first attribute should not have a newline anyway
                List.fold
                    (fun (prevContentAfterPresent, prevExpr) (a: SynAttributeList) ->
                        let expr =
                            ifElse
                                prevContentAfterPresent
                                sepNone
                                (sepNlnConsideringTriviaContentBeforeForMainNode SynModuleDecl_Attributes a.Range)
                            +> ((col sepNln a.Attributes (genAttribute astContext))
                                |> genTriviaFor SynAttributeList_ a.Range)

                        let hasContentAfter =
                            TriviaHelpers.``has content after after that matches``
                                (fun tn -> RangeHelpers.rangeEq tn.Range a.Range)
                                (function
                                | Newline
                                | Comment (LineCommentOnSingleLine _)
                                | Directive _ -> true
                                | _ -> false)
                                (Map.tryFindOrEmptyList SynAttributeList_ ctx.TriviaMainNodes)

                        (hasContentAfter, prevExpr +> expr))
                    (true, sepNone)
                    ats
                |> snd

            attributesExpr ctx
    | DoExpr e -> genExprKeepIndentInBranch astContext e
    | Exception ex -> genException astContext ex
    | HashDirective p -> genParsedHashDirective p
    | Extern (ats, px, ao, t, s, ps) ->
        genPreXmlDoc px +> genAttributes astContext ats
        -- "extern "
        +> genType
            { astContext with
                  IsCStylePattern = true }
            false
            t
        +> sepSpace
        +> opt sepSpace ao genAccess
        -- s
        +> sepOpenT
        +> col
            sepComma
            ps
            (genPat
                { astContext with
                      IsCStylePattern = true })
        +> sepCloseT
    // Add a new line after module-level let bindings
    | Let b -> genLetBinding { astContext with IsFirstChild = true } "let " b
    | LetRec (b :: bs) ->
        let sepBAndBs =
            match List.tryHead bs with
            | Some b' ->
                let r = b'.RangeOfBindingAndRhs

                sepNln
                +> sepNlnConsideringTriviaContentBeforeForMainNode (synBindingToFsAstType b) r
            | None -> id

        genLetBinding { astContext with IsFirstChild = true } "let rec " b
        +> sepBAndBs
        +> colEx
            (fun (b': SynBinding) ->
                let r = b'.RangeOfBindingAndRhs

                sepNln
                +> sepNlnConsideringTriviaContentBeforeForMainNode (synBindingToFsAstType b) r)
            bs
            (fun andBinding ->
                enterNodeFor (synBindingToFsAstType b) andBinding.RangeOfBindingAndRhs
                +> genLetBinding { astContext with IsFirstChild = false } "and " andBinding)

    | ModuleAbbrev (s1, s2) -> !- "module " -- s1 +> sepEq +> sepSpace -- s2
    | NamespaceFragment m -> failwithf "NamespaceFragment hasn't been implemented yet: %O" m
    | NestedModule (ats, px, ao, s, isRecursive, mds) ->
        genPreXmlDoc px
        +> genAttributes astContext ats
        +> (!- "module ")
        +> opt sepSpace ao genAccess
        +> ifElse isRecursive (!- "rec ") sepNone
        -- s
        +> sepEq
        +> indent
        +> sepNln
        +> genModuleDeclList astContext mds
        +> unindent

    | Open s -> !-(sprintf "open %s" s)
    | OpenType s -> !-(sprintf "open type %s" s)
    // There is no nested types and they are recursive if there are more than one definition
    | Types (t :: ts) ->
        let items =
            ColMultilineItem(genTypeDefn { astContext with IsFirstChild = true } t, sepNone)
            :: (List.map
                    (fun t ->
                        ColMultilineItem(
                            genTypeDefn { astContext with IsFirstChild = false } t,
                            sepNlnConsideringTriviaContentBeforeForMainNode TypeDefn_ t.Range
                        ))
                    ts)

        colWithNlnWhenItemIsMultilineUsingConfig items
    | md -> failwithf "Unexpected module declaration: %O" md
    |> genTriviaFor (synModuleDeclToFsAstType node) node.Range

and genSigModuleDecl astContext node =
    match node with
    | SigException ex -> genSigException astContext ex
    | SigHashDirective p -> genParsedHashDirective p
    | SigVal v -> genVal astContext v
    | SigModuleAbbrev (s1, s2) -> !- "module " -- s1 +> sepEq +> sepSpace -- s2
    | SigNamespaceFragment m -> failwithf "NamespaceFragment is not supported yet: %O" m
    | SigNestedModule (ats, px, ao, s, mds) ->
        genPreXmlDoc px +> genAttributes astContext ats
        -- "module "
        +> opt sepSpace ao genAccess
        -- s
        +> sepEq
        +> indent
        +> sepNln
        +> genSigModuleDeclList astContext mds
        +> unindent

    | SigOpen s -> !-(sprintf "open %s" s)
    | SigOpenType s -> !-(sprintf "open type %s" s)
    | SigTypes (t :: ts) ->
        let items =
            ColMultilineItem(genSigTypeDefn { astContext with IsFirstChild = true } t, sepNone)
            :: (List.map
                    (fun t ->
                        let sepNln =
                            let attributeRanges =
                                getRangesFromAttributesFromSynTypeDefnSig t

                            sepNlnConsideringTriviaContentBeforeWithAttributesFor
                                TypeDefnSig_
                                t.FullRange
                                attributeRanges

                        ColMultilineItem(genSigTypeDefn { astContext with IsFirstChild = false } t, sepNln))
                    ts)

        colWithNlnWhenItemIsMultilineUsingConfig items
    | md -> failwithf "Unexpected module signature declaration: %O" md
    |> (match node with
        | SynModuleSigDecl.Types _ -> genTriviaFor SynModuleSigDecl_Types node.Range
        | SynModuleSigDecl.NestedModule _ -> genTriviaFor SynModuleSigDecl_NestedModule node.Range
        | SynModuleSigDecl.Open (SynOpenDeclTarget.ModuleOrNamespace _, _) ->
            genTriviaFor SynModuleSigDecl_Open node.Range
        | SynModuleSigDecl.Open (SynOpenDeclTarget.Type _, _) -> genTriviaFor SynModuleSigDecl_OpenType node.Range
        | SynModuleSigDecl.Exception _ -> genTriviaFor SynModuleSigDecl_Exception node.Range
        | _ -> id)

and genAccess (Access s) = !-s

and genAttribute astContext (Attribute (s, e, target)) =
    match e with
    // Special treatment for function application on attributes
    | ConstUnitExpr -> !- "[<" +> opt sepColon target (!-) -- s -- ">]"
    | e ->
        let argSpacing =
            if hasParenthesis e then
                id
            else
                sepSpace

        !- "[<" +> opt sepColon target (!-) -- s
        +> argSpacing
        +> genExpr astContext e
        -- ">]"

and genAttributesCore astContext (ats: SynAttribute seq) =
    let genAttributeExpr astContext (Attribute (s, e, target) as attr) =
        match e with
        | ConstUnitExpr -> opt sepColon target (!-) -- s
        | e ->
            let argSpacing =
                if hasParenthesis e then
                    id
                else
                    sepSpace

            opt sepColon target (!-) -- s
            +> argSpacing
            +> genExpr astContext e
        |> genTriviaFor SynAttribute_ attr.Range

    let shortExpression =
        !- "[<"
        +> atCurrentColumn (col sepSemi ats (genAttributeExpr astContext))
        -- ">]"

    let longExpression =
        !- "[<"
        +> atCurrentColumn (col (sepSemi +> sepNln) ats (genAttributeExpr astContext))
        -- ">]"

    ifElse (Seq.isEmpty ats) sepNone (expressionFitsOnRestOfLine shortExpression longExpression)

and genOnelinerAttributes astContext ats =
    let ats = List.collect (fun a -> a.Attributes) ats
    ifElse (Seq.isEmpty ats) sepNone (genAttributesCore astContext ats +> sepSpace)

/// Try to group attributes if they are on the same line
/// Separate same-line attributes by ';'
/// Each bucket is printed in a different line
and genAttributes astContext (ats: SynAttributes) =
    ats
    |> List.fold
        (fun acc a (ctx: Context) ->
            let dontAddNewline =
                TriviaHelpers.``has content after that ends with``
                    (fun t -> RangeHelpers.rangeEq t.Range a.Range)
                    (function
                    | Directive _
                    | Newline
                    | Comment (LineCommentOnSingleLine _) -> true
                    | _ -> false)
                    (Map.tryFindOrEmptyList SynAttributeList_ ctx.TriviaMainNodes)

            let chain =
                acc
                +> (genAttributesCore astContext a.Attributes
                    |> genTriviaFor SynAttributeList_ a.Range)
                +> ifElse dontAddNewline sepNone sepNln

            chain ctx)
        sepNone

and genPreXmlDoc (PreXmlDoc lines) ctx =
    if ctx.Config.StrictMode then
        colPost sepNln sepNln lines (sprintf "///%s" >> (!-)) ctx
    else
        ctx

and genExprSepEqPrependType (astContext: ASTContext) (e: SynExpr) =
    match e with
    | TypedExpr (Typed, e, t) ->
        sepColon
        +> genType astContext false t
        +> sepEq
        +> sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)
    | _ ->
        sepEq
        +> sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)

and genTyparList astContext tps =
    ifElse
        (List.atMostOne tps)
        (col wordOr tps (genTypar astContext))
        (sepOpenT
         +> col wordOr tps (genTypar astContext)
         +> sepCloseT)

and genTypeAndParam astContext typeName tds tcs preferPostfix =
    let types openSep closeSep =
        (!-openSep
         +> coli
             sepComma
             tds
             (fun i ->
                 genTyparDecl
                     { astContext with
                           IsFirstTypeParam = i = 0 })
         +> colPre (!- " when ") wordAnd tcs (genTypeConstraint astContext)
         -- closeSep)

    if List.isEmpty tds then
        !-typeName
    elif preferPostfix then
        !-typeName +> types "<" ">"
    elif List.atMostOne tds then
        !-typeName +> types "<" ">"
    else
        types "(" ")" -- " " -- typeName

and genTypeParamPostfix astContext tds tcs =
    genTypeAndParam astContext "" tds tcs true

and genLetBinding astContext pref b =
    let genPref = !-pref

    match b with
    | LetBinding (ats, px, ao, isInline, isMutable, p, e, valInfo) ->
        match e, p with
        | TypedExpr (Typed, e, t), PatLongIdent (ao, s, ps, tpso) when (List.isNotEmpty ps) ->
            genSynBindingFunctionWithReturnType
                astContext
                false
                px
                ats
                genPref
                ao
                isInline
                isMutable
                s
                p.Range
                ps
                tpso
                t
                valInfo
                e
        | e, PatLongIdent (ao, s, ps, tpso) when (List.isNotEmpty ps) ->
            genSynBindingFunction astContext false px ats genPref ao isInline isMutable s p.Range ps tpso e
        | TypedExpr (Typed, e, t), pat ->
            genSynBindingValue astContext px ats genPref ao isInline isMutable pat (Some t) e
        | _, PatTuple _ -> genLetBindingDestructedTuple astContext px ats pref ao isInline isMutable p e
        | _, pat -> genSynBindingValue astContext px ats genPref ao isInline isMutable pat None e
        | _ -> sepNone
    | DoBinding (ats, px, e) ->
        let prefix =
            if pref.Contains("let") then
                pref.Replace("let", "do")
            else
                "do "

        genPreXmlDoc px +> genAttributes astContext ats
        -- prefix
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)

    | b -> failwithf "%O isn't a let binding" b
    +> leaveNodeFor (synBindingToFsAstType b) b.RangeOfBindingAndRhs

and genProperty astContext prefix ao propertyKind ps e =
    let tuplerize ps =
        let rec loop acc =
            function
            | [ p ] -> (List.rev acc, p)
            | p1 :: ps -> loop (p1 :: acc) ps
            | [] -> invalidArg "p" "Patterns should not be empty"

        loop [] ps

    match ps with
    | [ PatTuple ps ] ->
        let ps, p = tuplerize ps

        !-prefix +> opt sepSpace ao genAccess
        -- propertyKind
        +> ifElse
            (List.atMostOne ps)
            (col sepComma ps (genPat astContext) +> sepSpace)
            (sepOpenT
             +> col sepComma ps (genPat astContext)
             +> sepCloseT
             +> sepSpace)
        +> genPat astContext p
        +> genExprSepEqPrependType astContext e

    | ps ->
        !-prefix +> opt sepSpace ao genAccess
        -- propertyKind
        +> col sepSpace ps (genPat astContext)
        +> genExprSepEqPrependType astContext e

and genPropertyWithGetSet astContext (b1, b2) rangeOfMember =
    match b1, b2 with
    | PropertyBinding (ats, px, ao, isInline, mf1, PatLongIdent (ao1, s1, ps1, _), e1),
      PropertyBinding (_, _, _, _, _, PatLongIdent (ao2, _, ps2, _), e2) ->
        let prefix =
            genPreXmlDoc px
            +> genAttributes astContext ats
            +> genMemberFlags astContext mf1
            +> ifElse isInline (!- "inline ") sepNone
            +> opt sepSpace ao genAccess

        assert (ps1 |> Seq.map fst |> Seq.forall Option.isNone)
        assert (ps2 |> Seq.map fst |> Seq.forall Option.isNone)
        let ps1 = List.map snd ps1
        let ps2 = List.map snd ps2

        prefix
        +> !-s1
        +> indent
        +> sepNln
        +> optSingle (fun rom -> enterNodeTokenByName rom WITH) rangeOfMember
        +> genProperty astContext "with " ao1 "get " ps1 e1
        +> sepNln
        +> genProperty astContext "and " ao2 "set " ps2 e2
        +> unindent
    | _ -> sepNone

and genMemberBindingList astContext node =
    let rec collectItems
        (node: SynBinding list)
        (finalContinuation: ColMultilineItem list -> ColMultilineItem list)
        : ColMultilineItem list =
        match node with
        | [] -> finalContinuation []
        | mb :: rest ->
            let expr = genMemberBinding astContext mb
            let r = mb.RangeOfBindingAndRhs

            let sepNln =
                sepNlnConsideringTriviaContentBeforeForMainNode (synBindingToFsAstType mb) r

            collectItems
                rest
                (fun restItems ->
                    ColMultilineItem(expr, sepNln) :: restItems
                    |> finalContinuation)

    collectItems node id
    |> colWithNlnWhenItemIsMultiline

and genMemberBinding astContext b =
    match b with
    | PropertyBinding (ats, px, ao, isInline, mf, p, e) ->
        let prefix =
            genPreXmlDoc px
            +> genAttributes astContext ats
            +> genMemberFlags astContext mf
            +> ifElse isInline (!- "inline ") sepNone
            +> opt sepSpace ao genAccess

        let propertyKind =
            match mf with
            | MFProperty PropertyGet -> "get "
            | MFProperty PropertySet -> "set "
            | mf -> failwithf "Unexpected member flags: %O" mf

        match p with
        | PatLongIdent (ao, s, ps, _) ->
            assert (ps |> Seq.map fst |> Seq.forall Option.isNone)

            match ao, propertyKind, ps with
            | None, "get ", [ _, PatParen PatUnitConst ] ->
                // Provide short-hand notation `x.Member = ...` for `x.Member with get()` getters
                prefix -- s
                +> genExprSepEqPrependType astContext e
            | _ ->
                let ps = List.map snd ps

                prefix -- s
                +> indent
                +> sepNln
                +> genProperty astContext "with " ao propertyKind ps e
                +> unindent
        | p -> failwithf "Unexpected pattern: %O" p

    | MemberBinding (ats, px, ao, isInline, mf, p, e, synValInfo) ->
        let prefix =
            genMemberFlagsForMemberBinding astContext mf b.RangeOfBindingAndRhs

        match e, p with
        | TypedExpr (Typed, e, t), PatLongIdent (ao, s, ps, tpso) when (List.isNotEmpty ps) ->
            genSynBindingFunctionWithReturnType
                astContext
                true
                px
                ats
                prefix
                ao
                isInline
                false
                s
                p.Range
                ps
                tpso
                t
                synValInfo
                e
        | e, PatLongIdent (ao, s, ps, tpso) when (List.isNotEmpty ps) ->
            genSynBindingFunction astContext true px ats prefix ao isInline false s p.Range ps tpso e
        | TypedExpr (Typed, e, t), pat -> genSynBindingValue astContext px ats prefix ao isInline false pat (Some t) e
        | _, pat -> genSynBindingValue astContext px ats prefix ao isInline false pat None e

    | ExplicitCtor (ats, px, ao, p, e, so) ->
        let genPatCtor pat =
            match pat with
            | PatParen p ->
                match p with
                | SynPat.Const _ ->
                    genPat astContext p
                    +> enterNodeTokenByName pat.Range RPAREN
                | _ ->
                    sepOpenT
                    +> genPat astContext p
                    +> enterNodeTokenByName pat.Range RPAREN
                    +> sepCloseT
            | _ -> genPat astContext pat

        let prefix =
            let genPat ctx =
                match p with
                | PatExplicitCtor (ao, pat) ->
                    (opt sepSpace ao genAccess
                     +> !- "new"
                     +> sepSpaceBeforeClassConstructor
                     +> genPatCtor pat)
                        ctx
                | _ -> genPat astContext p ctx

            genPreXmlDoc px
            +> genAttributes astContext ats
            +> opt sepSpace ao genAccess
            +> genPat
            +> opt sepNone so (sprintf " as %s" >> (!-))

        match e with
        // Handle special "then" block i.e. fake sequential expressions in constructors
        | Sequential (e1, e2, false) ->
            prefix
            +> sepEq
            +> indent
            +> sepNln
            +> genExpr astContext e1
            ++ "then "
            +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2)
            +> unindent

        | e ->
            prefix
            +> sepEq
            +> sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)

    | b -> failwithf "%O isn't a member binding" b
    |> genTriviaFor (synBindingToFsAstType b) b.RangeOfBindingAndRhs

and genMemberFlags astContext (mf: MemberFlags) =
    match mf with
    | MFMember _ -> !- "member "
    | MFStaticMember _ -> !- "static member "
    | MFConstructor _ -> sepNone
    | MFOverride _ -> ifElse astContext.InterfaceRange.IsSome (!- "member ") (!- "override ")

and genMemberFlagsForMemberBinding astContext (mf: MemberFlags) (rangeOfBindingAndRhs: Range) =
    fun ctx ->
        let keywordFromTrivia =
            [ yield! (Map.tryFindOrEmptyList SynMemberDefn_Member ctx.TriviaMainNodes)
              yield! (Map.tryFindOrEmptyList SynMemberSig_Member ctx.TriviaMainNodes)
              yield! (Map.tryFindOrEmptyList MEMBER ctx.TriviaTokenNodes) ]
            |> List.tryFind
                (fun { Type = t; Range = r } ->
                    match t with
                    | MainNode SynMemberDefn_Member
                    | MainNode SynMemberSig_Member -> // trying to get AST trivia
                        RangeHelpers.``range contains`` r rangeOfBindingAndRhs

                    | Token (MEMBER, _) -> // trying to get token trivia
                        r.StartLine = rangeOfBindingAndRhs.StartLine

                    | _ -> false)
            |> Option.bind
                (fun tn ->
                    tn.ContentItself
                    |> Option.bind
                        (fun tc ->
                            match tc with
                            | Keyword { Content = "override" | "default" | "member" | "abstract" | "abstract member" as kw } ->
                                Some(!-(kw + " "))
                            | _ -> None))

        match mf with
        | MFStaticMember _
        | MFConstructor _ -> genMemberFlags astContext mf
        | MFMember _ ->
            keywordFromTrivia
            |> Option.defaultValue (genMemberFlags astContext mf)
        | MFOverride _ ->
            keywordFromTrivia
            |> Option.defaultValue (!- "override ")
        <| ctx

and genVal astContext (Val (ats, px, ao, s, identRange, t, vi, isInline, _) as node) =
    let range, synValTyparDecls =
        match node with
        | ValSpfn (_, _, synValTyparDecls, _, _, _, _, _, _, _, range) -> range, synValTyparDecls

    let genericParams =
        match synValTyparDecls with
        | SynValTyparDecls ([], _, _) -> sepNone
        | SynValTyparDecls (tpd, _, cst) -> genTypeParamPostfix astContext tpd cst

    let (FunType namedArgs) = (t, vi)

    genPreXmlDoc px
    +> genAttributes astContext ats
    +> (!- "val "
        +> onlyIf isInline (!- "inline ")
        +> opt sepSpace ao genAccess
        -- s
        +> genericParams
        |> genTriviaFor Ident_ identRange)
    +> sepColonWithSpacesFixed
    +> ifElse
        (List.isNotEmpty namedArgs)
        (autoIndentAndNlnIfExpressionExceedsPageWidth (genTypeList astContext namedArgs))
        (genConstraints astContext t vi)
    |> genTriviaFor ValSpfn_ range

and genRecordFieldName astContext (RecordFieldName (s, eo) as node) =
    let rfn, _, _ = node
    let range = (fst rfn).Range

    opt
        sepNone
        eo
        (fun e ->
            let expr =
                sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)

            !-s +> sepEq +> expr)
    |> genTriviaFor RecordField_ range

and genAnonRecordFieldName astContext (AnonRecordFieldName (s, e)) =
    let expr =
        sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)

    !-s +> sepEq +> expr

and genTuple astContext es =
    let genShortExpr astContext e =
        addParenForTupleWhen (genExpr astContext) e

    let shortExpression =
        col sepComma es (genShortExpr astContext)

    let longExpression =
        let containsLambdaOrMatchExpr =
            es
            |> List.pairwise
            |> List.exists
                (function
                | SynExpr.Match _, _
                | SynExpr.Lambda _, _
                | InfixApp (_, _, _, SynExpr.Lambda _), _ -> true
                | _ -> false)

        let sep =
            if containsLambdaOrMatchExpr then
                (sepNln +> sepComma)
            else
                (sepComma +> sepNln)

        let lastIndex = List.length es - 1

        let genExpr astContext idx e =
            match e with
            | SynExpr.IfThenElse _ when (idx < lastIndex) ->
                autoParenthesisIfExpressionExceedsPageWidth (genExpr astContext e)
            | _ -> genExpr astContext e

        coli sep es (genExpr astContext)

    atCurrentColumn (expressionFitsOnRestOfLine shortExpression longExpression)

and genNamedArgumentExpr (astContext: ASTContext) operatorExpr e1 e2 =
    let short =
        genExpr astContext e1
        +> sepSpace
        +> genInfixOperator "=" operatorExpr
        +> sepSpace
        +> genExpr astContext e2

    let long =
        genExpr astContext e1
        +> sepSpace
        +> genInfixOperator "=" operatorExpr
        +> indent
        +> sepNln
        +> genExpr astContext e2
        +> unindent

    expressionFitsOnRestOfLine short long

and genExpr astContext synExpr ctx =
    let kw tokenName f = tokN synExpr.Range tokenName f

    let expr =
        match synExpr with
        | ElmishReactWithoutChildren (identifier, isArray, children, childrenRange) when
            (not ctx.Config.DisableElmishSyntax)
            ->
            fun (ctx: Context) ->
                let tokenSize = if isArray then 2 else 1

                let openingTokenRange, openTokenType =
                    ctx.MkRangeWith
                        (childrenRange.Start.Line, childrenRange.Start.Column)
                        (childrenRange.Start.Line, (childrenRange.Start.Column + tokenSize)),
                    (if isArray then LBRACK_BAR else LBRACK)

                let closingTokenRange, closingTokenType =
                    ctx.MkRangeWith
                        (childrenRange.End.Line, (childrenRange.End.Column - tokenSize))
                        (childrenRange.End.Line, childrenRange.End.Column),
                    (if isArray then BAR_RBRACK else RBRACK)

                let shortExpression =
                    let noChildren =
                        tokN openingTokenRange openTokenType (ifElse isArray sepOpenAFixed sepOpenLFixed)
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseAFixed sepCloseLFixed)

                    let genChildren =
                        tokN openingTokenRange openTokenType (ifElse isArray sepOpenA sepOpenL)
                        +> col sepSemi children (genExpr astContext)
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseA sepCloseL)

                    !-identifier
                    +> sepSpace
                    +> ifElse (List.isEmpty children) noChildren genChildren

                let elmishExpression =
                    !-identifier
                    +> sepSpace
                    +> tokN openingTokenRange openTokenType (ifElse isArray sepOpenA sepOpenL)
                    +> atCurrentColumn (
                        col sepNln children (genExpr astContext)
                        +> onlyIf
                            (TriviaHelpers.``has content before that matches``
                                (fun tn -> RangeHelpers.rangeEq tn.Range closingTokenRange)
                                (function
                                | Comment (BlockComment _) -> true
                                | _ -> false)
                                (Map.tryFindOrEmptyList closingTokenType ctx.TriviaTokenNodes))
                            sepNln
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseA sepCloseL)
                    )

                let felizExpression =
                    let hasBlockCommentBeforeClosingToken =
                        TriviaHelpers.``has content before that matches``
                            (fun tn -> RangeHelpers.rangeEq tn.Range closingTokenRange)
                            (function
                            | Comment (BlockComment _) -> true
                            | _ -> false)
                            (Map.tryFindOrEmptyList closingTokenType ctx.TriviaTokenNodes)

                    let hasChildren = List.isNotEmpty children

                    atCurrentColumn (
                        !-identifier
                        +> sepSpace
                        +> tokN openingTokenRange openTokenType (ifElse isArray sepOpenAFixed sepOpenLFixed)
                        +> onlyIf hasChildren (indent +> sepNln)
                        +> col sepNln children (genExpr astContext)
                        +> onlyIf hasBlockCommentBeforeClosingToken (sepNln +> unindent)
                        +> enterNodeTokenByName closingTokenRange closingTokenType
                        +> onlyIfNot hasBlockCommentBeforeClosingToken unindent
                        +> onlyIf hasChildren sepNlnUnlessLastEventIsNewline
                        +> ifElse isArray sepCloseAFixed sepCloseLFixed
                        +> leaveNodeTokenByName closingTokenRange closingTokenType
                    )

                let multilineExpression =
                    ifElse ctx.Config.SingleArgumentWebMode felizExpression elmishExpression

                let size =
                    getListOrArrayExprSize ctx ctx.Config.MaxElmishWidth children

                let smallExpression =
                    isSmallExpression size shortExpression multilineExpression

                isShortExpression ctx.Config.MaxElmishWidth smallExpression multilineExpression ctx

        | ElmishReactWithChildren ((identifier, _, _), attributes, (isArray, children, childrenRange)) when
            (not ctx.Config.DisableElmishSyntax)
            ->
            let genChildren isShort =
                let tokenSize = if isArray then 2 else 1

                let openingTokenRange, openTokenType =
                    ctx.MkRangeWith
                        (childrenRange.Start.Line, childrenRange.Start.Column)
                        (childrenRange.Start.Line, (childrenRange.Start.Column + tokenSize)),
                    (if isArray then LBRACK_BAR else LBRACK)

                let closingTokenRange, closingTokenType =
                    ctx.MkRangeWith
                        (childrenRange.End.Line, (childrenRange.End.Column - tokenSize))
                        (childrenRange.End.Line, childrenRange.End.Column),
                    (if isArray then BAR_RBRACK else RBRACK)

                match children with
                | [] ->
                    tokN openingTokenRange openTokenType (ifElse isArray sepOpenAFixed sepOpenLFixed)
                    +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseAFixed sepCloseLFixed)
                | [ singleChild ] ->
                    if isShort then
                        tokN openingTokenRange openTokenType (ifElse isArray sepOpenA sepOpenL)
                        +> genExpr astContext singleChild
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseA sepCloseL)
                    else
                        tokN openingTokenRange openTokenType (ifElse isArray sepOpenA sepOpenL)
                        +> indent
                        +> sepNln
                        +> genExpr astContext singleChild
                        +> unindent
                        +> sepNln
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseAFixed sepCloseLFixed)

                | children ->
                    if isShort then
                        tokN openingTokenRange openTokenType (ifElse isArray sepOpenA sepOpenL)
                        +> col sepSemi children (genExpr astContext)
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseA sepCloseL)
                    else
                        tokN openingTokenRange openTokenType (ifElse isArray sepOpenA sepOpenL)
                        +> indent
                        +> sepNln
                        +> col sepNln children (genExpr astContext)
                        +> unindent
                        +> sepNln
                        +> tokN closingTokenRange closingTokenType (ifElse isArray sepCloseAFixed sepCloseLFixed)

            let shortExpression =
                !-identifier
                +> sepSpace
                +> genExpr astContext attributes
                +> sepSpace
                +> genChildren true

            let longExpression =
                atCurrentColumn (
                    !-identifier
                    +> sepSpace
                    +> atCurrentColumn (genExpr astContext attributes)
                    +> sepSpace
                    +> genChildren false
                )

            fun ctx ->
                let size =
                    getListOrArrayExprSize ctx ctx.Config.MaxElmishWidth children

                let smallExpression =
                    isSmallExpression size shortExpression longExpression

                isShortExpression ctx.Config.MaxElmishWidth smallExpression longExpression ctx

        | SingleExpr (Lazy, e) ->
            // Always add braces when dealing with lazy
            let hasParenthesis = hasParenthesis e

            let isInfixExpr =
                match e with
                | InfixApp _ -> true
                | _ -> false

            let genInfixExpr (ctx: Context) =
                isShortExpression
                    ctx.Config.MaxInfixOperatorExpression
                    // if this fits on the rest of line right after the lazy keyword, it should be wrapped in parenthesis.
                    (sepOpenT +> genExpr astContext e +> sepCloseT)
                    // if it is multiline there is no need for parenthesis, because of the indentation
                    (indent
                     +> sepNln
                     +> genExpr astContext e
                     +> unindent)
                    ctx

            let genNonInfixExpr =
                autoIndentAndNlnIfExpressionExceedsPageWidth (
                    onlyIfNot hasParenthesis sepOpenT
                    +> genExpr astContext e
                    +> onlyIfNot hasParenthesis sepCloseT
                )

            str "lazy "
            +> ifElse isInfixExpr genInfixExpr genNonInfixExpr

        | SingleExpr (kind, e) ->
            str kind
            +> (match kind with
                | YieldFrom
                | Yield
                | Return
                | ReturnFrom
                | Do
                | DoBang -> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)
                | _ -> genExpr astContext e)

        | ConstExpr (c, r) -> genConst c r
        | NullExpr -> !- "null"
        // Not sure about the role of e1
        | Quote (_, e2, isRaw) ->
            let e = genExpr astContext e2
            ifElse isRaw (!- "<@@ " +> e -- " @@>") (!- "<@ " +> e -- " @>")
        | TypedExpr (TypeTest, e, t) ->
            genExpr astContext e -- " :? "
            +> genType astContext false t
        | TypedExpr (Downcast, e, t) ->
            let shortExpr =
                genExpr astContext e -- " :?> "
                +> genType astContext false t

            let longExpr =
                genExpr astContext e +> sepNln -- ":?> "
                +> genType astContext false t

            expressionFitsOnRestOfLine shortExpr longExpr
        | TypedExpr (Upcast, e, t) ->
            let shortExpr =
                genExpr astContext e -- " :> "
                +> genType astContext false t

            let longExpr =
                genExpr astContext e +> sepNln -- ":> "
                +> genType astContext false t

            expressionFitsOnRestOfLine shortExpr longExpr
        | TypedExpr (Typed, e, t) ->
            genExpr astContext e
            +> sepColon
            +> genType astContext false t
        | NewTuple (t, px) ->
            let sepSpace (ctx: Context) =
                match t with
                | UppercaseSynType -> onlyIf ctx.Config.SpaceBeforeUppercaseInvocation sepSpace ctx
                | LowercaseSynType -> onlyIf ctx.Config.SpaceBeforeLowercaseInvocation sepSpace ctx

            let short =
                !- "new "
                +> genType astContext false t
                +> sepSpace
                +> genExpr astContext px

            let long =
                !- "new "
                +> genType astContext false t
                +> sepSpace
                +> genMultilineFunctionApplicationArguments astContext px

            expressionFitsOnRestOfLine short long
        | SynExpr.New (_, t, e, _) ->
            !- "new "
            +> genType astContext false t
            +> sepSpace
            +> genExpr astContext e
        | Tuple (es, _) -> genTuple astContext es
        | StructTuple es ->
            !- "struct "
            +> sepOpenT
            +> genTuple astContext es
            +> sepCloseT
        | ArrayOrList (isArray, [], _) ->
            ifElse
                isArray
                (enterNodeTokenByName synExpr.Range LBRACK_BAR
                 +> sepOpenAFixed
                 +> leaveNodeTokenByName synExpr.Range LBRACK_BAR
                 +> enterNodeTokenByName synExpr.Range BAR_RBRACK
                 +> sepCloseAFixed
                 +> leaveNodeTokenByName synExpr.Range BAR_RBRACK)
                (enterNodeTokenByName synExpr.Range LBRACK
                 +> sepOpenLFixed
                 +> leaveNodeTokenByName synExpr.Range LBRACK
                 +> enterNodeTokenByName synExpr.Range RBRACK
                 +> sepCloseLFixed
                 +> leaveNodeTokenByName synExpr.Range RBRACK)
        | ArrayOrList (isArray, xs, _) as alNode ->
            let tokenSize = if isArray then 2 else 1

            let openingTokenRange =
                ctx.MkRangeWith
                    (alNode.Range.Start.Line, alNode.Range.Start.Column)
                    (alNode.Range.Start.Line, (alNode.Range.Start.Column + tokenSize))

            let closingTokenRange =
                ctx.MkRangeWith
                    (alNode.Range.End.Line, (alNode.Range.End.Column - tokenSize))
                    (alNode.Range.End.Line, alNode.Range.End.Column)

            let smallExpression =
                ifElse isArray (tokN openingTokenRange LBRACK_BAR sepOpenA) (tokN openingTokenRange LBRACK sepOpenL)
                +> col sepSemi xs (genExpr astContext)
                +> ifElse
                    isArray
                    (tokN closingTokenRange BAR_RBRACK sepCloseA)
                    (tokN closingTokenRange RBRACK sepCloseL)

            let multilineExpression =
                ifAlignBrackets
                    (genMultiLineArrayOrListAlignBrackets isArray xs openingTokenRange closingTokenRange astContext)
                    (genMultiLineArrayOrList isArray xs openingTokenRange closingTokenRange astContext)

            fun ctx ->
                if List.exists isIfThenElseWithYieldReturn xs
                   || List.forall isSynExprLambda xs then
                    multilineExpression ctx
                else
                    let size =
                        getListOrArrayExprSize ctx ctx.Config.MaxArrayOrListWidth xs

                    isSmallExpression size smallExpression multilineExpression ctx

        | Record (inheritOpt, xs, eo) ->
            let smallRecordExpr =
                sepOpenS
                +> leaveLeftBrace synExpr.Range
                +> optSingle
                    (fun (inheritType, inheritExpr) ->
                        !- "inherit "
                        +> genType astContext false inheritType
                        +> addSpaceBeforeClassConstructor inheritExpr
                        +> genExpr astContext inheritExpr
                        +> onlyIf (List.isNotEmpty xs) sepSemi)
                    inheritOpt
                +> optSingle (fun e -> genExpr astContext e +> !- " with ") eo
                +> col sepSemi xs (genRecordFieldName astContext)
                +> sepCloseS
                +> leaveNodeTokenByName synExpr.Range RBRACE

            let multilineRecordExpr =
                ifAlignBrackets
                    (genMultilineRecordInstanceAlignBrackets inheritOpt xs eo synExpr astContext)
                    (genMultilineRecordInstance inheritOpt xs eo synExpr astContext)

            fun ctx ->
                let size = getRecordSize ctx xs
                isSmallExpression size smallRecordExpr multilineRecordExpr ctx

        | AnonRecord (isStruct, fields, copyInfo) ->
            let smallExpression =
                onlyIf isStruct !- "struct "
                +> sepOpenAnonRecd
                +> optSingle (fun e -> genExpr astContext e +> !- " with ") copyInfo
                +> col sepSemi fields (genAnonRecordFieldName astContext)
                +> sepCloseAnonRecd

            let longExpression =
                ifAlignBrackets
                    (genMultilineAnonRecordAlignBrackets isStruct fields copyInfo astContext)
                    (genMultilineAnonRecord isStruct fields copyInfo astContext)

            fun (ctx: Context) ->
                let size = getRecordSize ctx fields
                isSmallExpression size smallExpression longExpression ctx

        | ObjExpr (t, eio, bd, ims, range) ->
            if List.isEmpty bd then
                // Check the role of the second part of eio
                let param =
                    opt sepNone (Option.map fst eio) (genExpr astContext)

                // See https://devblogs.microsoft.com/dotnet/announcing-f-5/#default-interface-member-consumption
                sepOpenS
                +> !- "new "
                +> genType astContext false t
                +> param
                +> sepCloseS
            else
                ifAlignBrackets
                    (genObjExprAlignBrackets t eio bd ims range astContext)
                    (genObjExpr t eio bd ims range astContext)

        | While (e1, e2) ->
            atCurrentColumn (
                !- "while " +> genExpr astContext e1 -- " do"
                +> indent
                +> sepNln
                +> genExpr astContext e2
                +> unindent
            )

        | For (s, e1, e2, e3, isUp) ->
            atCurrentColumn (
                !-(sprintf "for %s = " s)
                +> genExpr astContext e1
                +> ifElse isUp (!- " to ") (!- " downto ")
                +> genExpr astContext e2
                -- " do"
                +> indent
                +> sepNln
                +> genExpr astContext e3
                +> unindent
            )

        // Handle the form 'for i in e1 -> e2'
        | ForEach (p, e1, e2, isArrow) ->
            atCurrentColumn (
                !- "for " +> genPat astContext p -- " in "
                +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr { astContext with IsNakedRange = true } e1)
                +> ifElse
                    isArrow
                    (sepArrow
                     +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2))
                    (!- " do"
                     +> indent
                     +> sepNln
                     +> genExpr astContext e2
                     +> unindent)
            )

        | CompExpr (isArrayOrList, e) ->
            ifElse
                isArrayOrList
                (genExpr astContext e)
                // The opening { of the CompExpr is being added at the App(_,_,Ident(_),CompExr(_)) level
                (expressionFitsOnRestOfLine
                    (genExpr astContext e +> sepCloseS)
                    (genExpr astContext e
                     +> unindent
                     +> (fun ctx ->
                         let closingBraceRange =
                             ctx.MkRangeWith
                                 (synExpr.Range.EndLine, synExpr.Range.EndColumn - 1)
                                 (synExpr.Range.EndLine, synExpr.Range.EndColumn)

                         enterNodeTokenByName closingBraceRange RBRACE ctx)
                     +> sepNlnUnlessLastEventIsNewline
                     +> sepCloseSFixed))

        | CompExprBody statements ->
            let genCompExprStatement astContext ces =
                match ces with
                | LetOrUseStatement (prefix, binding) ->
                    enterNodeFor (synBindingToFsAstType binding) binding.RangeOfBindingAndRhs
                    +> genLetBinding astContext prefix binding
                | LetOrUseBangStatement (isUse, pat, expr, r) ->
                    enterNodeFor SynExpr_LetOrUseBang r // print Trivia before entire LetBang expression
                    +> ifElse isUse (!- "use! ") (!- "let! ")
                    +> genPat astContext pat
                    -- " = "
                    +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext expr)
                | AndBangStatement (pat, expr, andRange) ->
                    enterNodeTokenByName andRange AND_BANG
                    +> !- "and! "
                    +> genPat astContext pat
                    -- " = "
                    +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext expr)
                | OtherStatement expr -> genExpr astContext expr

            let getRangeOfCompExprStatement ces =
                match ces with
                | LetOrUseStatement (_, binding) -> binding.RangeOfBindingAndRhs
                | LetOrUseBangStatement (_, _, _, r) -> r
                | AndBangStatement (_, _, r) -> r
                | OtherStatement expr -> expr.Range

            let getSepNln ces r =
                match ces with
                | LetOrUseStatement (_, b) ->
                    sepNlnConsideringTriviaContentBeforeForMainNode (synBindingToFsAstType b) r
                | LetOrUseBangStatement _ -> sepNlnConsideringTriviaContentBeforeForMainNode SynExpr_LetOrUseBang r
                | AndBangStatement _ -> sepNlnConsideringTriviaContentBeforeForToken AND_BANG r
                | OtherStatement e ->
                    let t, r = synExprToFsAstType e
                    sepNlnConsideringTriviaContentBeforeForMainNode t r

            statements
            |> List.map
                (fun ces ->
                    let expr = genCompExprStatement astContext ces
                    let r = getRangeOfCompExprStatement ces
                    let sepNln = getSepNln ces r
                    ColMultilineItem(expr, sepNln))
            |> colWithNlnWhenItemIsMultilineUsingConfig

        | ArrayOrListOfSeqExpr (isArray, e) as alNode ->
            let astContext = { astContext with IsNakedRange = true }
            let tokenSize = if isArray then 2 else 1

            let openingTokenRange =
                ctx.MkRangeWith
                    (alNode.Range.Start.Line, alNode.Range.Start.Column)
                    (alNode.Range.Start.Line, (alNode.Range.Start.Column + tokenSize))

            let closingTokenRange =
                ctx.MkRangeWith
                    (alNode.Range.End.Line, (alNode.Range.End.Column - tokenSize))
                    (alNode.Range.End.Line, alNode.Range.End.Column)


            let shortExpression =
                ifElse
                    isArray
                    ((tokN openingTokenRange LBRACK_BAR sepOpenA)
                     +> atCurrentColumnIndent (genExpr astContext e)
                     +> (tokN closingTokenRange BAR_RBRACK sepCloseA))
                    ((tokN openingTokenRange LBRACK sepOpenL)
                     +> atCurrentColumnIndent (genExpr astContext e)
                     +> (tokN closingTokenRange RBRACK sepCloseL))

            let bracketsOnSameColumn =
                ifElse
                    isArray
                    (tokN openingTokenRange LBRACK_BAR sepOpenAFixed)
                    (tokN openingTokenRange LBRACK sepOpenLFixed)
                +> indent
                +> sepNln
                +> genExpr astContext e
                +> unindent
                +> sepNln
                +> ifElse
                    isArray
                    (tokN closingTokenRange BAR_RBRACK sepCloseAFixed)
                    (tokN closingTokenRange RBRACK sepCloseLFixed)

            let multilineExpression =
                ifAlignBrackets bracketsOnSameColumn shortExpression

            fun ctx -> isShortExpression ctx.Config.MaxArrayOrListWidth shortExpression multilineExpression ctx

        | JoinIn (e1, e2) ->
            genExpr astContext e1 -- " in "
            +> genExpr astContext e2
        | Paren (lpr, Lambda (pats, expr, lambdaRange), rpr, pr) ->
            fun (ctx: Context) ->
                let arrowRange =
                    List.last pats
                    |> fun lastPat -> ctx.MkRange lastPat.Range.End expr.Range.Start

                let hasLineCommentAfterArrow =
                    findTriviaTokenFromName RARROW arrowRange ctx
                    |> Option.isSome

                let body =
                    genExprKeepIndentInBranch astContext expr

                let expr =
                    let triviaOfLambda f (ctx: Context) =
                        (Map.tryFindOrEmptyList SynExpr_Lambda ctx.TriviaMainNodes
                         |> List.tryFind (fun tn -> RangeHelpers.rangeEq tn.Range lambdaRange)
                         |> optSingle f)
                            ctx

                    sepOpenTFor lpr
                    +> triviaOfLambda printContentBefore
                    -- "fun "
                    +> col sepSpace pats (genPat astContext)
                    +> indent
                    +> triviaAfterArrow arrowRange
                    +> ifElse
                        hasLineCommentAfterArrow
                        (body
                         +> triviaOfLambda printContentAfter
                         +> sepNlnWhenWriteBeforeNewlineNotEmpty id
                         +> sepCloseTFor rpr pr)
                        (autoNlnIfExpressionExceedsPageWidth (
                            body
                            +> triviaOfLambda printContentAfter
                            +> sepNlnWhenWriteBeforeNewlineNotEmpty id
                            +> sepCloseTFor rpr pr
                        ))
                    +> unindent

                expr ctx

        // When there are parentheses, most likely lambda will appear in function application
        | Lambda (pats, expr, _range) ->
            atCurrentColumn (
                !- "fun "
                +> col sepSpace pats (genPat astContext)
                +> sepArrow
                +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext expr)
            )
        | MatchLambda (keywordRange, cs) ->
            (!- "function "
             |> genTriviaFor SynExpr_MatchLambda_Function keywordRange)
            +> sepNln
            +> genClauses astContext cs
        | Match (e, cs) ->
            let withRange =
                ctx.MkRange e.Range.Start (List.head cs).Range.Start

            let genMatchExpr =
                !- "match "
                +> expressionFitsOnRestOfLine
                    (genExpr astContext e
                     +> genWithAfterMatch withRange)
                    (genExprInIfOrMatch astContext e
                     +> (sepNlnUnlessLastEventIsNewline
                         +> (genWithAfterMatch withRange)))

            atCurrentColumn (genMatchExpr +> sepNln +> genClauses astContext cs)
        | MatchBang (e, cs) ->
            let withRange =
                ctx.MkRange e.Range.Start (List.head cs).Range.Start

            let genMatchExpr =
                !- "match! "
                +> expressionFitsOnRestOfLine
                    (genExpr astContext e
                     +> genWithAfterMatch withRange)
                    (genExprInIfOrMatch astContext e
                     +> (sepNlnUnlessLastEventIsNewline
                         +> (genWithAfterMatch withRange)))

            atCurrentColumn (genMatchExpr +> sepNln +> genClauses astContext cs)
        | TraitCall (tps, msg, e) ->
            genTyparList astContext tps
            +> sepColon
            +> sepOpenT
            +> genMemberSig astContext msg
            +> sepCloseT
            +> sepSpace
            +> genExpr astContext e

        | Paren (_, ILEmbedded r, rpr, _) ->
            fun ctx ->
                let expr =
                    Map.tryFindOrEmptyList SynExpr_LibraryOnlyILAssembly ctx.TriviaMainNodes
                    |> List.choose
                        (fun tn ->
                            if RangeHelpers.rangeEq r tn.Range then
                                match tn.ContentItself with
                                | Some (EmbeddedIL eil) -> Some eil
                                | _ -> None
                            else
                                None)
                    |> List.tryHead
                    |> Option.map (!-)
                    |> Option.defaultValue sepNone

                (expr
                 +> optSingle (fun r -> leaveNodeTokenByName r RPAREN) rpr)
                    ctx
        | Paren (lpr, e, rpr, pr) ->
            match e with
            | LetOrUses _
            | Sequential (_, LetOrUses _, _) ->
                sepOpenTFor lpr
                +> atCurrentColumn (genExpr astContext e)
                +> sepCloseTFor rpr pr
            | _ ->
                sepOpenTFor lpr
                +> genExpr astContext e
                +> sepCloseTFor rpr pr
        | CompApp (s, e) ->
            !-s
            +> sepSpace
            +> sepOpenS
            +> genExpr { astContext with IsNakedRange = true } e
            +> sepCloseS
        // This supposes to be an infix function, but for some reason it isn't picked up by InfixApps
        | App (Var "?", e :: es) ->
            match es with
            | SynExpr.Const (SynConst.String _, _) :: _ ->
                genExpr astContext e -- "?"
                +> col sepSpace es (genExpr astContext)
            | _ ->
                genExpr astContext e -- "?"
                +> sepOpenT
                +> col sepSpace es (genExpr astContext)
                +> sepCloseT

        | App (Var "..", [ e1; e2 ]) ->
            let expr =
                genExpr astContext e1 +> sepSpace -- ".."
                +> sepSpace
                +> genExpr astContext e2

            ifElse astContext.IsNakedRange expr (sepOpenS +> expr +> sepCloseS)
        | App (Var ".. ..", [ e1; e2; e3 ]) ->
            let expr =
                genExpr astContext e1 +> sepSpace -- ".."
                +> sepSpace
                +> genExpr astContext e2
                +> sepSpace
                -- ".."
                +> sepSpace
                +> genExpr astContext e3

            ifElse astContext.IsNakedRange expr (sepOpenS +> expr +> sepCloseS)
        // Separate two prefix ops by spaces
        | PrefixApp (s1, PrefixApp (s2, e)) -> !-(sprintf "%s %s" s1 s2) +> genExpr astContext e
        | PrefixApp (s, App (e, [ Paren _ as p ]))
        | PrefixApp (s, App (e, [ ConstExpr (SynConst.Unit _, _) as p ])) ->
            !-s
            +> sepSpace
            +> genExpr astContext e
            +> genExpr astContext p
        | PrefixApp (s, e) ->
            let extraSpaceBeforeString =
                match e with
                | String _
                | SynExpr.InterpolatedString _ -> sepSpace
                | _ -> sepNone

            !-s
            +> extraSpaceBeforeString
            +> genExpr astContext e

        | NewlineInfixApp (operatorText, operatorExpr, (Lambda _ as e1), e2)
        | NewlineInfixApp (operatorText, operatorExpr, (IfThenElse _ as e1), e2) ->
            genMultilineInfixExpr astContext e1 operatorText operatorExpr e2

        | NewlineInfixApps (e, es) ->
            let shortExpr =
                genExpr astContext e
                +> sepSpace
                +> col
                    sepSpace
                    es
                    (fun (s, oe, e) ->
                        genInfixOperator s oe
                        +> sepSpace
                        +> genExpr astContext e)

            let multilineExpr =
                match es with
                | [] -> genExpr astContext e
                | (s, oe, e2) :: es ->
                    genMultilineInfixExpr astContext e s oe e2
                    +> sepNln
                    +> col
                        sepNln
                        es
                        (fun (s, oe, e) ->
                            genInfixOperator s oe
                            +> sepSpace
                            +> genExprInMultilineInfixExpr astContext e)

            fun ctx ->
                atCurrentColumn (isShortExpression ctx.Config.MaxInfixOperatorExpression shortExpr multilineExpr) ctx

        | SameInfixApps (e, es) ->
            let shortExpr =
                genExpr astContext e
                +> sepSpace
                +> col
                    sepSpace
                    es
                    (fun (s, oe, e) ->
                        genInfixOperator s oe
                        +> sepSpace
                        +> genExpr astContext e)

            let multilineExpr =
                genExpr astContext e
                +> sepNln
                +> col
                    sepNln
                    es
                    (fun (s, oe, e) ->
                        genInfixOperator s oe
                        +> sepSpace
                        +> genExprInMultilineInfixExpr astContext e)

            fun ctx ->
                atCurrentColumn (isShortExpression ctx.Config.MaxInfixOperatorExpression shortExpr multilineExpr) ctx

        | InfixApp (operatorText, operatorExpr, e1, e2) ->
            fun ctx ->
                isShortExpression
                    ctx.Config.MaxInfixOperatorExpression
                    (genOnelinerInfixExpr astContext e1 operatorText operatorExpr e2)
                    (ifElse
                        (noBreakInfixOps.Contains(operatorText))
                        (genOnelinerInfixExpr astContext e1 operatorText operatorExpr e2)
                        (genMultilineInfixExpr astContext e1 operatorText operatorExpr e2))
                    ctx

        | TernaryApp (e1, e2, e3) ->
            atCurrentColumn (
                genExpr astContext e1
                +> !- "?"
                +> genExpr astContext e2
                +> sepSpace
                +> !- "<-"
                +> sepSpace
                +> genExpr astContext e3
            )

        // Result<int, string>.Ok 42
        | App (DotGet (TypeApp (e, lt, ts, gt), lids), es) ->
            let s = List.map fst lids |> String.concat "."

            genExpr astContext e
            +> genGenericTypeParameters astContext lt ts gt
            +> !-(sprintf ".%s" s)
            +> sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (col sepSpace es (genExpr astContext))

        // Foo(fun x -> x).Bar().Meh
        | DotGetAppDotGetAppParenLambda (e, px, appLids, es, lids) ->
            let short =
                genExpr astContext e
                +> genExpr astContext px
                +> genLidsWithDots appLids
                +> col sepComma es (genExpr astContext)
                +> genLidsWithDots lids

            let long =
                let functionName =
                    match e with
                    | LongIdentPiecesExpr lids when (List.moreThanOne lids) -> genFunctionNameWithMultilineLids id lids
                    | TypeApp (LongIdentPiecesExpr lids, lt, ts, gt) when (List.moreThanOne lids) ->
                        genFunctionNameWithMultilineLids (genGenericTypeParameters astContext lt ts gt) lids
                    | _ -> genExpr astContext e

                functionName
                +> indent
                +> genExpr astContext px
                +> sepNln
                +> genLidsWithDotsAndNewlines appLids
                +> col sepComma es (genExpr astContext)
                +> sepNln
                +> genLidsWithDotsAndNewlines lids
                +> unindent

            fun ctx -> isShortExpression ctx.Config.MaxDotGetExpressionWidth short long ctx

        // Foo().Bar
        | DotGetAppParen (e, px, lids) ->
            let shortAppExpr =
                genExpr astContext e +> genExpr astContext px

            let longAppExpr =
                let functionName argFn =
                    match e with
                    | LongIdentPiecesExpr lids when (List.moreThanOne lids) ->
                        genFunctionNameWithMultilineLids argFn lids
                    | TypeApp (LongIdentPiecesExpr lids, lt, ts, gt) when (List.moreThanOne lids) ->
                        genFunctionNameWithMultilineLids
                            (genGenericTypeParameters astContext lt ts gt
                             +> argFn)
                            lids
                    | DotGetAppDotGetAppParenLambda _ ->
                        leadingExpressionIsMultiline
                            (genExpr astContext e)
                            (fun isMultiline ->
                                if isMultiline then
                                    indent +> argFn +> unindent
                                else
                                    argFn)
                    | _ -> genExpr astContext e +> argFn

                let arguments =
                    genMultilineFunctionApplicationArguments astContext px

                functionName arguments

            let shortDotGetExpr = genLidsWithDots lids

            let longDotGetExpr =
                indent
                +> sepNln
                +> genLidsWithDotsAndNewlines lids
                +> unindent

            fun ctx ->
                isShortExpression
                    ctx.Config.MaxDotGetExpressionWidth
                    (shortAppExpr +> shortDotGetExpr)
                    (longAppExpr +> longDotGetExpr)
                    ctx

        // Foo(fun x -> x).Bar()
        | DotGetApp (App (e, [ Paren (_, Lambda _, _, _) as px ]), es) ->
            let genLongFunctionName f =
                match e with
                | LongIdentPiecesExpr lids when (List.moreThanOne lids) -> genFunctionNameWithMultilineLids f lids
                | TypeApp (LongIdentPiecesExpr lids, lt, ts, gt) when (List.moreThanOne lids) ->
                    genFunctionNameWithMultilineLids (genGenericTypeParameters astContext lt ts gt +> f) lids
                | _ -> genExpr astContext e +> f

            let lastEsIndex = es.Length - 1

            let genApp
                (idx: int)
                ((lids, e, t): (string * range) list * SynExpr * (range * SynType list * range) option)
                : Context -> Context =
                let short =
                    genLidsWithDots lids
                    +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                    +> genSpaceBeforeLids idx lastEsIndex lids e
                    +> genExpr astContext e

                let long =
                    genLidsWithDotsAndNewlines lids
                    +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                    +> genSpaceBeforeLids idx lastEsIndex lids e
                    +> genMultilineFunctionApplicationArguments astContext e

                expressionFitsOnRestOfLine short long

            let short =
                genExpr astContext e
                +> genExpr astContext px
                +> coli
                    sepNone
                    es
                    (fun idx (lids, e, t) ->
                        genLidsWithDots lids
                        +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                        +> genSpaceBeforeLids idx lastEsIndex lids e
                        +> genExpr astContext e)

            let long =
                genLongFunctionName (genExpr astContext px)
                +> indent
                +> sepNln
                +> coli sepNln es genApp
                +> unindent

            fun ctx -> isShortExpression ctx.Config.MaxDotGetExpressionWidth short long ctx

        // Foo().Bar().Meh()
        | DotGetApp (e, es) ->
            let genLongFunctionName =
                match e with
                | AppOrTypeApp (LongIdentPiecesExpr lids, t, [ Paren _ as px ]) when (List.moreThanOne lids) ->
                    genFunctionNameWithMultilineLids
                        (optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                         +> expressionFitsOnRestOfLine
                             (genExpr astContext px)
                             (genMultilineFunctionApplicationArguments astContext px))
                        lids
                | AppOrTypeApp (LongIdentPiecesExpr lids, t, [ e2 ]) when (List.moreThanOne lids) ->
                    genFunctionNameWithMultilineLids
                        (optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                         +> genExpr astContext e2)
                        lids
                | AppOrTypeApp (SimpleExpr e, t, [ ConstExpr (SynConst.Unit, r) ]) ->
                    genExpr astContext e
                    +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                    +> genConst SynConst.Unit r
                | AppOrTypeApp (SimpleExpr e, t, [ Paren _ as px ]) ->
                    let short =
                        genExpr astContext e
                        +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                        +> genExpr astContext px

                    let long =
                        genExpr astContext e
                        +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                        +> genMultilineFunctionApplicationArguments astContext px

                    expressionFitsOnRestOfLine short long
                | _ -> genExpr astContext e

            let lastEsIndex = es.Length - 1

            let genApp
                (idx: int)
                ((lids, e, t): (string * range) list * SynExpr * (range * SynType list * range) option)
                : Context -> Context =
                let short =
                    genLidsWithDots lids
                    +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                    +> genSpaceBeforeLids idx lastEsIndex lids e
                    +> genExpr astContext e

                let long =
                    genLidsWithDotsAndNewlines lids
                    +> optSingle (fun (lt, ts, gt) -> genGenericTypeParameters astContext lt ts gt) t
                    +> genSpaceBeforeLids idx lastEsIndex lids e
                    +> genMultilineFunctionApplicationArguments astContext e

                expressionFitsOnRestOfLine short long

            let short =
                match e with
                | App (e, [ px ]) when (hasParenthesis px || isArrayOrList px) ->
                    genExpr astContext e +> genExpr astContext px
                | _ -> genExpr astContext e
                +> coli sepNone es genApp

            let long =
                genLongFunctionName
                +> indent
                +> sepNln
                +> coli sepNln es genApp
                +> unindent

            fun ctx -> isShortExpression ctx.Config.MaxDotGetExpressionWidth short long ctx

        | AppParenArg (Choice1Of2 (Paren _, _, _, _, _, _) as app)
        | AppParenArg (Choice2Of2 (Paren _, _, _, _, _) as app) ->
            let short = genAppWithParenthesis app astContext

            let long =
                genAlternativeAppWithParenthesis app astContext

            expressionFitsOnRestOfLine short long

        | AppSingleParenArg (e, px) ->
            let sepSpace (ctx: Context) =
                match e with
                | Paren _ -> sepSpace ctx
                | UppercaseSynExpr -> onlyIf ctx.Config.SpaceBeforeUppercaseInvocation sepSpace ctx
                | LowercaseSynExpr -> onlyIf ctx.Config.SpaceBeforeLowercaseInvocation sepSpace ctx

            let short =
                genExpr astContext e
                +> sepSpace
                +> genExpr astContext px

            let long =
                genExpr astContext e
                +> sepSpace
                +> genMultilineFunctionApplicationArguments astContext px

            expressionFitsOnRestOfLine short long

        // Always spacing in multiple arguments
        | App (e, es) -> genApp astContext e es
        | TypeApp (e, lt, ts, gt) ->
            genExpr astContext e
            +> genGenericTypeParameters astContext lt ts gt
        | LetOrUses (bs, e) ->
            fun ctx ->
                let items =
                    let inKeywords =
                        Map.tryFindOrEmptyList IN ctx.TriviaTokenNodes

                    collectMultilineItemForLetOrUses
                        astContext
                        inKeywords
                        bs
                        e
                        (collectMultilineItemForSynExpr astContext inKeywords e)

                atCurrentColumn (colWithNlnWhenItemIsMultilineUsingConfig items) ctx
        // Could customize a bit if e is single line
        | TryWith (e, cs) ->
            atCurrentColumn (
                kw TRY !- "try "
                +> indent
                +> sepNln
                +> genExpr astContext e
                +> unindent
                +> kw WITH !+~ "with"
                +> indentOnWith
                +> sepNln
                +> col sepNln cs (genClause astContext true)
                +> unindentOnWith
            )

        | TryFinally (e1, e2) ->
            atCurrentColumn (
                kw TRY !- "try "
                +> indent
                +> sepNln
                +> genExpr astContext e1
                +> unindent
                +> kw FINALLY !+~ "finally"
                +> indent
                +> sepNln
                +> genExpr astContext e2
                +> unindent
            )

        | SequentialSimple es
        | Sequentials es ->
            fun ctx ->
                let inKeywords =
                    Map.tryFindOrEmptyList IN ctx.TriviaTokenNodes

                let items =
                    List.collect (collectMultilineItemForSynExpr astContext inKeywords) es

                atCurrentColumn (colWithNlnWhenItemIsMultilineUsingConfig items) ctx
        // A generalization of IfThenElse
        | ElIf ((e1, e2, _, _, _) :: es, enOpt) ->
            // https://docs.microsoft.com/en-us/dotnet/fsharp/style-guide/formatting#formatting-if-expressions
            fun ctx ->
                let cleanIfExpr e1 =
                    match e1 with
                    | Paren (lpr, exp, rpr, pr) ->
                        match exp with
                        | OptVar (s, isOpt, range) -> if not isOpt then exp else e1
                        | _ -> e1
                    | _ -> e1

                let elfis =
                    es
                    |> List.mapi
                        (fun idx (condition, body, _, _, fullIfThenElseExpr) ->
                            let endOfPreviousBranch =
                                if idx = 0 then
                                    e2.Range.End
                                else
                                    let _, e2, _, _, _ = es.[idx - 1]
                                    e2.Range.End

                            let maxRangeBetween =
                                ctx.MkRange endOfPreviousBranch body.Range.End

                            let elifKeyword =
                                [ yield! Map.tryFindOrEmptyList ELSE ctx.TriviaTokenNodes
                                  yield! Map.tryFindOrEmptyList ELIF ctx.TriviaTokenNodes ]
                                |> List.filter (fun tn -> RangeHelpers.``range contains`` maxRangeBetween tn.Range)
                                |> List.sortBy (fun tn -> tn.Range.StartLine, tn.Range.EndLine)
                                |> List.tryHead

                            // This range spans from the elif keyword to the end of the body expression
                            let correctedRange =
                                match elifKeyword with
                                | Some { Range = range } -> ctx.MkRange range.Start body.Range.End
                                | _ -> fullIfThenElseExpr.Range

                            let elifKeywordRange =
                                ctx.MkRange correctedRange.Start body.Range.Start

                            let thenKeywordRange =
                                ctx.MkRange condition.Range.End body.Range.Start

                            condition, body, elifKeywordRange, thenKeywordRange)

                let hasElfis = not (List.isEmpty elfis)
                let hasElse = Option.isSome enOpt

                let genIf ifElseRange =
                    tokN ifElseRange IF (!- "if ") +> sepSpace

                let genThen ifElseRange = tokN ifElseRange THEN (!- "then ")
                let genElse ifElseRange = tokN ifElseRange ELSE (!- "else ")

                let genElifOneliner (elf1: SynExpr, elf2: SynExpr, elifKeywordRange, thenKeywordRange) =
                    TriviaContext.``else if / elif`` elifKeywordRange
                    +> sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
                    +> genExpr astContext (cleanIfExpr elf1)
                    +> sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
                    +> genThen thenKeywordRange
                    +> sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
                    +> genExpr astContext elf2

                let genElifMultiLine (elf1: SynExpr, elf2, elifKeywordRange, thenKeywordRange) =
                    (TriviaContext.``else if / elif`` elifKeywordRange)
                    +> autoIndentAndNlnWhenWriteBeforeNewlineNotEmpty (genExprInIfOrMatch astContext (cleanIfExpr elf1))
                    +> sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
                    +> genThen thenKeywordRange
                    +> indent
                    +> sepNln
                    +> genExpr astContext elf2
                    +> unindent

                let genShortElse e elseRange =
                    optSingle
                        (fun e ->
                            sepSpace
                            +> genElse elseRange
                            +> genExpr astContext e)
                        e

                let genOneliner enOpt =
                    genIf synExpr.Range
                    +> genExpr astContext (cleanIfExpr e1)
                    +> sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
                    +> genThen synExpr.Range
                    +> genExpr astContext e2
                    +> genShortElse enOpt synExpr.Range

                let isIfThenElse =
                    function
                    | SynExpr.IfThenElse _ -> true
                    | _ -> false

                let longIfThenElse =
                    genIf synExpr.Range
                    // f.ex. if // meh
                    //           x
                    // bool expr x should be indented
                    +> autoIndentAndNlnWhenWriteBeforeNewlineNotEmpty (
                        genExprInIfOrMatch astContext (cleanIfExpr e1)
                        +> sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
                    )
                    +> genThen synExpr.Range
                    +> indent
                    +> sepNln
                    +> genExpr astContext e2
                    +> unindent
                    +> onlyIf (hasElfis || hasElse) sepNln
                    +> col sepNln elfis genElifMultiLine
                    +> opt
                        id
                        enOpt
                        (fun e4 ->
                            let correctedElseRange =
                                match List.tryLast elfis with
                                | Some (_, te, _, _) -> ctx.MkRange te.Range.End synExpr.Range.End
                                | None -> synExpr.Range

                            onlyIf (List.isNotEmpty elfis) sepNln
                            +> genElse correctedElseRange
                            +> indent
                            +> sepNln
                            +> genExpr astContext e4
                            +> unindent)

                let shortIfThenElif (ctx: Context) =
                    // Try and format if each conditional follow the one-liner rules
                    // Abort if something is too long
                    let shortCtx, isShort =
                        let elseExpr =
                            let elseRange =
                                List.last elfis
                                |> fun (_, b, _, _) -> ctx.MkRange b.Range.End synExpr.Range.End

                            enOpt
                            |> Option.map (fun _ -> genShortElse enOpt elseRange)
                            |> Option.toList

                        let exprs =
                            [ genOneliner None
                              yield! (List.map genElifOneliner elfis)
                              yield! elseExpr ]

                        let lastIndex = List.length exprs - 1

                        exprs
                        |> List.indexed
                        |> List.fold
                            (fun (acc, allLinesShort) (idx, expr) ->
                                if allLinesShort then
                                    let lastLine, lastColumn =
                                        acc.WriterModel.Lines.Length, acc.Column

                                    let nextCtx = expr acc

                                    let currentLine, currentColumn =
                                        nextCtx.WriterModel.Lines.Length, nextCtx.Column

                                    let isStillShort =
                                        lastLine = currentLine
                                        && (currentColumn - lastColumn
                                            <= acc.Config.MaxIfThenElseShortWidth)

                                    (ifElse (lastIndex > idx) sepNln sepNone nextCtx, isStillShort)
                                else
                                    ctx, false)
                            (ctx, true)

                    if isShort then
                        shortCtx
                    else
                        longIfThenElse ctx

                let expr =
                    if hasElfis && not (isIfThenElse e2) then
                        shortIfThenElif
                    elif isIfThenElse e2 then
                        // If branch expression is an if/then/else expressions.
                        // Always go with long version in this case
                        longIfThenElse
                    else
                        let shortExpression = genOneliner enOpt
                        let longExpression = longIfThenElse

                        isShortExpression ctx.Config.MaxIfThenElseShortWidth shortExpression longExpression

                atCurrentColumnIndent expr ctx

        // At this stage, all symbolic operators have been handled.
        | OptVar (s, isOpt, ranges) ->
            // In case s is f.ex `onStrongDiscard.IsNone`, last range is the range of `IsNone`
            let lastRange = List.tryLast ranges

            let genS =
                match lastRange with
                | Some r -> infixOperatorFromTrivia r s
                | None -> !-s

            ifElse isOpt (!- "?") sepNone
            +> genS
            +> opt id lastRange (leaveNodeFor Ident_)
        | LongIdentSet (s, e, _) ->
            !-(sprintf "%s <- " s)
            +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e)
        | DotIndexedGet (App (e, [ ConstExpr (SynConst.Unit, _) as ux ]), es) ->
            genExpr astContext e
            +> genExpr astContext ux
            +> !- "."
            +> sepOpenLFixed
            +> genIndexers astContext es
            +> sepCloseLFixed
            +> leaveNodeTokenByName synExpr.Range RBRACK
        | DotIndexedGet (AppSingleParenArg (e, px), es) ->
            let short =
                genExpr astContext e +> genExpr astContext px

            let long =
                genExpr astContext e
                +> genMultilineFunctionApplicationArguments astContext px

            let idx =
                !- "."
                +> sepOpenLFixed
                +> genIndexers astContext es
                +> sepCloseLFixed
                +> leaveNodeTokenByName synExpr.Range RBRACK

            expressionFitsOnRestOfLine (short +> idx) (long +> idx)
        | DotIndexedGet (e, es) ->
            addParenIfAutoNln e (genExpr astContext) -- "."
            +> sepOpenLFixed
            +> genIndexers astContext es
            +> sepCloseLFixed
            +> leaveNodeTokenByName synExpr.Range RBRACK
        | DotIndexedSet (App (e, [ ConstExpr (SynConst.Unit, _) as ux ]), es, e2) ->
            let appExpr =
                genExpr astContext e +> genExpr astContext ux

            let idx =
                !- "."
                +> sepOpenLFixed
                +> genIndexers astContext es
                +> sepCloseLFixed
                +> leaveNodeTokenByName synExpr.Range RBRACK
                +> sepArrowRev

            expressionFitsOnRestOfLine
                (appExpr +> idx +> genExpr astContext e2)
                (appExpr
                 +> idx
                 +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2))
        | DotIndexedSet (AppSingleParenArg (a, px), es, e2) ->
            let short =
                genExpr astContext a +> genExpr astContext px

            let long =
                genExpr astContext a
                +> genMultilineFunctionApplicationArguments astContext px

            let idx =
                !- "."
                +> sepOpenLFixed
                +> genIndexers astContext es
                +> sepCloseLFixed
                +> leaveNodeTokenByName synExpr.Range RBRACK
                +> sepArrowRev

            expressionFitsOnRestOfLine
                (short +> idx +> genExpr astContext e2)
                (long
                 +> idx
                 +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2))

        | DotIndexedSet (e1, es, e2) ->
            addParenIfAutoNln e1 (genExpr astContext) -- ".["
            +> genIndexers astContext es
            -- "] <- "
            +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2)
        | NamedIndexedPropertySet (ident, e1, e2) ->
            !-ident +> genExpr astContext e1 -- " <- "
            +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2)
        | DotNamedIndexedPropertySet (e, ident, e1, e2) ->
            genExpr astContext e -- "." -- ident
            +> genExpr astContext e1
            -- " <- "
            +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e2)

        // typeof<System.Collections.IEnumerable>.FullName
        | DotGet (e, lids) ->
            let shortExpr =
                genExpr astContext e +> genLidsWithDots lids

            let longExpr =
                //genLongIdentWithMultipleFragmentsMultiline astContext e
                genExpr astContext e
                +> indent
                +> sepNln
                +> genLidsWithDotsAndNewlines lids
                +> unindent

            fun ctx -> isShortExpression ctx.Config.MaxDotGetExpressionWidth shortExpr longExpr ctx
        | DotSet (e1, s, e2) ->
            addParenIfAutoNln e1 (genExpr astContext)
            -- sprintf ".%s <- " s
            +> genExpr astContext e2

        | SynExpr.Set (e1, e2, _) ->
            addParenIfAutoNln e1 (genExpr astContext)
            -- sprintf " <- "
            +> genExpr astContext e2

        | ParsingError r ->
            raise
            <| FormatException(
                sprintf
                    "Parsing error(s) between line %i column %i and line %i column %i"
                    r.StartLine
                    (r.StartColumn + 1)
                    r.EndLine
                    (r.EndColumn + 1)
            )

        | LibraryOnlyStaticOptimization (optExpr, constraints, e) ->
            genExpr astContext optExpr
            +> genSynStaticOptimizationConstraint astContext constraints
            +> sepEq
            +> sepSpaceOrNlnIfExpressionExceedsPageWidth (genExpr astContext e)

        | UnsupportedExpr r ->
            raise
            <| FormatException(
                sprintf
                    "Unsupported construct(s) between line %i column %i and line %i column %i"
                    r.StartLine
                    (r.StartColumn + 1)
                    r.EndLine
                    (r.EndColumn + 1)
            )
        | SynExpr.InterpolatedString (parts, _) ->
            fun (ctx: Context) ->
                let stringRanges =
                    List.choose
                        (function
                        | SynInterpolatedStringPart.String (_, r) -> Some r
                        | _ -> None)
                        parts

                // multiline interpolated string will contain the $ and or braces in the triviaContent
                // example: $"""%s{ , } bar
                let stringsFromTrivia =
                    stringRanges
                    |> List.choose
                        (fun range ->
                            Map.tryFindOrEmptyList SynInterpolatedStringPart_String ctx.TriviaMainNodes
                            |> List.choose
                                (fun tn ->
                                    match tn.Type, tn.ContentItself with
                                    | MainNode SynInterpolatedStringPart_String, Some (StringContent sc) when
                                        (RangeHelpers.rangeEq tn.Range range)
                                        ->
                                        Some sc
                                    | _ -> None)
                            |> List.tryHead
                            |> Option.map (fun sc -> range, sc))

                let genInterpolatedFillExpr expr =
                    fun ctx ->
                        let currentConfig = ctx.Config

                        let interpolatedConfig =
                            { currentConfig with
                                  // override the max line length for the interpolated expression.
                                  // this is to avoid scenarios where the long / multiline format of the expresion will be used
                                  // where the construct is this short
                                  // see unit test ``construct url with Fable``
                                  MaxLineLength = ctx.WriterModel.Column + ctx.Config.MaxLineLength }

                        genExpr astContext expr { ctx with Config = interpolatedConfig }
                        // Restore the existing configuration after printing the interpolated expression
                        |> fun ctx -> { ctx with Config = currentConfig }
                    |> atCurrentColumnIndent

                let expr =
                    if List.length stringRanges = List.length stringsFromTrivia then
                        colEx
                            (fun _ -> sepNone)
                            parts
                            (fun part ->
                                match part with
                                | SynInterpolatedStringPart.String (_, range) ->
                                    let stringFromTrivia =
                                        List.find (fun (r, _) -> RangeHelpers.rangeEq range r) stringsFromTrivia
                                        |> snd

                                    !-stringFromTrivia
                                    |> genTriviaFor SynInterpolatedStringPart_String range
                                | SynInterpolatedStringPart.FillExpr (expr, ident) ->
                                    genInterpolatedFillExpr expr
                                    +> optSingle (fun (Ident format) -> !-(sprintf ":%s" format)) ident)
                    else
                        !- "$\""
                        +> colEx
                            (fun _ -> sepNone)
                            parts
                            (fun part ->
                                match part with
                                | SynInterpolatedStringPart.String (s, r) ->
                                    !-s
                                    |> genTriviaFor SynInterpolatedStringPart_String r
                                | SynInterpolatedStringPart.FillExpr (expr, _ident) ->
                                    !- "{" +> genInterpolatedFillExpr expr +> !- "}")
                        +> !- "\""

                expr ctx
        | e -> failwithf "Unexpected expression: %O" e
        |> (match synExpr with
            | SynExpr.App _ -> genTriviaFor SynExpr_App synExpr.Range
            | SynExpr.AnonRecd _ -> genTriviaFor SynExpr_AnonRecd synExpr.Range
            | SynExpr.Record _ -> genTriviaFor SynExpr_Record synExpr.Range
            | SynExpr.Ident _ -> genTriviaFor SynExpr_Ident synExpr.Range
            | SynExpr.IfThenElse _ -> genTriviaFor SynExpr_IfThenElse synExpr.Range
            | SynExpr.Lambda _ -> genTriviaFor SynExpr_Lambda synExpr.Range
            | SynExpr.ForEach _ -> genTriviaFor SynExpr_ForEach synExpr.Range
            | SynExpr.For _ -> genTriviaFor SynExpr_For synExpr.Range
            | SynExpr.Match _ -> genTriviaFor SynExpr_Match synExpr.Range
            | SynExpr.MatchBang _ -> genTriviaFor SynExpr_MatchBang synExpr.Range
            | SynExpr.YieldOrReturn _ -> genTriviaFor SynExpr_YieldOrReturn synExpr.Range
            | SynExpr.YieldOrReturnFrom _ -> genTriviaFor SynExpr_YieldOrReturnFrom synExpr.Range
            | SynExpr.TryFinally _ -> genTriviaFor SynExpr_TryFinally synExpr.Range
            | SynExpr.LongIdentSet _ -> genTriviaFor SynExpr_LongIdentSet synExpr.Range
            | SynExpr.ArrayOrList _ -> genTriviaFor SynExpr_ArrayOrList synExpr.Range
            | SynExpr.ArrayOrListOfSeqExpr _ -> genTriviaFor SynExpr_ArrayOrListOfSeqExpr synExpr.Range
            | SynExpr.Paren _ -> genTriviaFor SynExpr_Paren synExpr.Range
            | SynExpr.InterpolatedString _ -> genTriviaFor SynExpr_InterpolatedString synExpr.Range
            | SynExpr.Tuple _ -> genTriviaFor SynExpr_Tuple synExpr.Range
            | SynExpr.DoBang _ -> genTriviaFor SynExpr_DoBang synExpr.Range
            | SynExpr.TryWith _ -> genTriviaFor SynExpr_TryWith synExpr.Range
            | SynExpr.New _ -> genTriviaFor SynExpr_New synExpr.Range
            | SynExpr.Assert _ -> genTriviaFor SynExpr_Assert synExpr.Range
            | SynExpr.While _ -> genTriviaFor SynExpr_While synExpr.Range
            | SynExpr.MatchLambda _ -> genTriviaFor SynExpr_MatchLambda synExpr.Range
            | SynExpr.LongIdent _ -> genTriviaFor SynExpr_LongIdent synExpr.Range
            | SynExpr.DotGet _ -> genTriviaFor SynExpr_DotGet synExpr.Range
            | SynExpr.Upcast _ -> genTriviaFor SynExpr_Upcast synExpr.Range
            | SynExpr.Downcast _ -> genTriviaFor SynExpr_Downcast synExpr.Range
            | SynExpr.DotIndexedGet _ -> genTriviaFor SynExpr_DotIndexedGet synExpr.Range
            | SynExpr.DotIndexedSet _ -> genTriviaFor SynExpr_DotIndexedSet synExpr.Range
            | SynExpr.ObjExpr _ -> genTriviaFor SynExpr_ObjExpr synExpr.Range
            | SynExpr.JoinIn _ -> genTriviaFor SynExpr_JoinIn synExpr.Range
            | SynExpr.Do _ -> genTriviaFor SynExpr_Do synExpr.Range
            | SynExpr.TypeApp _ -> genTriviaFor SynExpr_TypeApp synExpr.Range
            | SynExpr.Lazy _ -> genTriviaFor SynExpr_Lazy synExpr.Range
            | SynExpr.InferredUpcast _ -> genTriviaFor SynExpr_InferredUpcast synExpr.Range
            | SynExpr.InferredDowncast _ -> genTriviaFor SynExpr_InferredDowncast synExpr.Range
            | SynExpr.AddressOf _ -> genTriviaFor SynExpr_AddressOf synExpr.Range
            | SynExpr.Null _ -> genTriviaFor SynExpr_Null synExpr.Range
            | SynExpr.TraitCall _ -> genTriviaFor SynExpr_TraitCall synExpr.Range
            | SynExpr.DotNamedIndexedPropertySet _ -> genTriviaFor SynExpr_DotNamedIndexedPropertySet synExpr.Range
            | SynExpr.NamedIndexedPropertySet _ -> genTriviaFor SynExpr_NamedIndexedPropertySet synExpr.Range
            | SynExpr.Set _ -> genTriviaFor SynExpr_Set synExpr.Range
            | SynExpr.Quote _ -> genTriviaFor SynExpr_Quote synExpr.Range
            | SynExpr.ArbitraryAfterError _ -> genTriviaFor SynExpr_ArbitraryAfterError synExpr.Range
            | SynExpr.DiscardAfterMissingQualificationAfterDot _ ->
                genTriviaFor SynExpr_DiscardAfterMissingQualificationAfterDot synExpr.Range
            | SynExpr.DotSet _ -> genTriviaFor SynExpr_DotSet synExpr.Range
            | SynExpr.Fixed _ -> genTriviaFor SynExpr_Fixed synExpr.Range
            | SynExpr.FromParseError _ -> genTriviaFor SynExpr_FromParseError synExpr.Range
            | SynExpr.ImplicitZero _ -> genTriviaFor SynExpr_ImplicitZero synExpr.Range
            | SynExpr.LibraryOnlyStaticOptimization _ ->
                genTriviaFor SynExpr_LibraryOnlyStaticOptimization synExpr.Range
            | SynExpr.LibraryOnlyILAssembly _ -> genTriviaFor SynExpr_LibraryOnlyILAssembly synExpr.Range
            | SynExpr.LibraryOnlyUnionCaseFieldGet _ -> genTriviaFor SynExpr_LibraryOnlyUnionCaseFieldGet synExpr.Range
            | SynExpr.LibraryOnlyUnionCaseFieldSet _ -> genTriviaFor SynExpr_LibraryOnlyUnionCaseFieldSet synExpr.Range
            | SynExpr.SequentialOrImplicitYield _ -> genTriviaFor SynExpr_SequentialOrImplicitYield synExpr.Range
            | SynExpr.TypeTest _ -> genTriviaFor SynExpr_TypeTest synExpr.Range
            | SynExpr.Const _ ->
                // SynConst has trivia attached to it
                id
            | SynExpr.LetOrUse _
            | SynExpr.Sequential _
            | SynExpr.CompExpr _ ->
                // first and last nested node has trivia attached to it
                id
            | SynExpr.LetOrUseBang _ ->
                // printed as part of CompBody
                id
            | SynExpr.Typed _ ->
                // child nodes contain trivia
                id)

    expr ctx

and genInfixOperator operatorText (operatorExpr: SynExpr) =
    (!-operatorText
     |> genTriviaFor SynExpr_Ident operatorExpr.Range)
    +> sepNlnWhenWriteBeforeNewlineNotEmpty sepNone

and genOnelinerInfixExpr astContext e1 operatorText operatorExpr e2 =
    genExpr astContext e1
    +> sepSpace
    +> genInfixOperator operatorText operatorExpr
    +> sepSpace
    +> genExpr astContext e2

and genMultilineInfixExpr astContext e1 operatorText operatorExpr e2 =
    let genE1 (ctx: Context) =
        match e1 with
        | SynExpr.IfThenElse _ when (ctx.Config.IndentSize - 1 <= operatorText.Length) ->
            autoParenthesisIfExpressionExceedsPageWidth (genExpr astContext e1) ctx
        | SynExpr.Match _ when (ctx.Config.IndentSize <= operatorText.Length) ->
            let ctxAfterMatch = genExpr astContext e1 ctx

            let lastClauseIsSingleLine =
                Queue.rev ctxAfterMatch.WriterEvents
                |> Seq.skipWhile
                    (fun e ->
                        match e with
                        | RestoreIndent _
                        | RestoreAtColumn _ -> true
                        | _ -> false)
                // In case the last clause was multiline an UnIndent event should follow
                |> Seq.tryHead
                |> fun e ->
                    match e with
                    | Some (UnIndentBy _) -> false
                    | _ -> true

            if lastClauseIsSingleLine then
                ctxAfterMatch
            else
                autoParenthesisIfExpressionExceedsPageWidth (genExpr astContext e1) ctx
        | _ -> genExpr astContext e1 ctx

    atCurrentColumn (
        genE1
        +> sepNln
        +> genInfixOperator operatorText operatorExpr
        +> sepSpace
        +> genExprInMultilineInfixExpr astContext e2
    )

and genExprInMultilineInfixExpr astContext e =
    match e with
    | LetOrUses (xs, e) ->
        atCurrentColumn (
            col sepNln xs (fun (pref, lb) -> genLetBinding astContext pref lb +> !- " in")
            +> sepNln
            +> expressionFitsOnRestOfLine
                (genExpr astContext e)
                (let t, r = synExprToFsAstType e in

                 sepNlnConsideringTriviaContentBeforeForMainNode t r
                 +> genExpr astContext e)
        )
    | Paren (lpr, (Match _ as mex), rpr, pr) ->
        fun ctx ->
            if ctx.Config.MultiLineLambdaClosingNewline then
                (tokN lpr LPAREN sepOpenT
                 +> indent
                 +> sepNln
                 +> genExpr astContext mex
                 +> unindent
                 +> sepNln
                 +> optSingle (fun rpr -> tokN rpr RPAREN sepCloseT) rpr
                 |> genTriviaFor SynExpr_Paren pr)
                    ctx
            else
                (tokN lpr LPAREN sepOpenT
                 +> atCurrentColumnIndent (genExpr astContext mex)
                 +> optSingle (fun rpr -> tokN rpr RPAREN sepCloseT) rpr
                 |> genTriviaFor SynExpr_Paren pr)
                    ctx
    | Paren (_, InfixApp (_, _, DotGet _, _), _, _)
    | Paren (_, DotGetApp _, _, _) -> atCurrentColumnIndent (genExpr astContext e)
    | MatchLambda (keywordRange, cs) ->
        (!- "function "
         |> genTriviaFor SynExpr_MatchLambda_Function keywordRange)
        +> indent
        +> sepNln
        +> genClauses astContext cs
        +> unindent
    | _ -> genExpr astContext e

and genLidsWithDots (lids: (string * range) list) =
    optSingle (fun (_, r) -> enterNodeFor Ident_ r) (List.tryHead lids)
    +> !- "."
    +> col !- "." lids (fun (s, _) -> !-s)

and genLidsWithDotsAndNewlines (lids: (string * range) list) =
    optSingle (fun (_, r) -> enterNodeFor Ident_ r) (List.tryHead lids)
    +> !- "."
    +> col (sepNln +> !- ".") lids (fun (s, _) -> !-s)

and genSpaceBeforeLids
    (currentIndex: int)
    (lastEsIndex: int)
    (lids: (string * range) list)
    (arg: SynExpr)
    (ctx: Context)
    : Context =
    let config =
        let s = fst lids.[0]

        if Char.IsUpper(s.[0]) then
            ctx.Config.SpaceBeforeUppercaseInvocation
        else
            ctx.Config.SpaceBeforeLowercaseInvocation

    if (lastEsIndex = currentIndex)
       && (not (hasParenthesis arg) || config) then
        sepSpace ctx
    else
        ctx

and genFunctionNameWithMultilineLids f lids =
    match lids with
    | (s, r) :: t ->
        enterNodeFor Ident_ r
        +> !-s
        +> indent
        +> sepNln
        +> genLidsWithDotsAndNewlines t
        +> f
        +> unindent
    | _ -> sepNone

and genMultilineFunctionApplicationArguments astContext argExpr =
    let argsInsideParenthesis lpr rpr pr f =
        sepOpenTFor lpr
        +> indent
        +> sepNln
        +> f
        +> unindent
        +> sepNln
        +> sepCloseTFor rpr pr
        |> genTriviaFor SynExpr_Paren pr

    let genExpr astContext e =
        match e with
        | InfixApp (equal, operatorExpr, e1, e2) when (equal = "=") ->
            genNamedArgumentExpr astContext operatorExpr e1 e2
        | _ -> genExpr astContext e

    match argExpr with
    | Paren (_, Lambda _, _, _) -> genExpr astContext argExpr
    | Paren (lpr, Tuple (args, tupleRange), rpr, pr) ->
        (col (sepCommaFixed +> sepNln) args (genExpr astContext))
        |> genTriviaFor SynExpr_Tuple tupleRange
        |> argsInsideParenthesis lpr rpr pr
    | Paren (lpr, singleExpr, rpr, pr) ->
        genExpr astContext singleExpr
        |> argsInsideParenthesis lpr rpr pr
    | _ -> genExpr astContext argExpr

and genGenericTypeParameters astContext lt ts gt =
    match ts with
    | [] -> sepNone
    | ts ->
        tokN lt LESS (!- "<")
        +> coli
            sepComma
            ts
            (fun idx ->
                genType
                    { astContext with
                          IsFirstTypeParam = idx = 0 }
                    false)
        +> indentIfNeeded sepNone
        +> tokN gt GREATER (!- ">")

and genMultilineRecordInstance
    (inheritOpt: (SynType * SynExpr) option)
    (xs: (RecordFieldName * SynExpr option * BlockSeparator option) list)
    (eo: SynExpr option)
    synExpr
    astContext
    (ctx: Context)
    =
    let recordExpr =
        let fieldsExpr =
            col sepSemiNln xs (genRecordFieldName astContext)

        match eo with
        | Some e ->
            genExpr astContext e
            +> !- " with"
            +> indent
            +> sepNln
            +> fieldsExpr
            +> unindent
        | None -> fieldsExpr

    let expr =
        sepOpenS
        +> (fun (ctx: Context) ->
            { ctx with
                  RecordBraceStart = ctx.Column :: ctx.RecordBraceStart })
        +> atCurrentColumnIndent (
            leaveLeftBrace synExpr.Range
            +> opt
                (if xs.IsEmpty then sepNone else sepNln)
                inheritOpt
                (fun (typ, expr) ->
                    !- "inherit "
                    +> genType astContext false typ
                    +> addSpaceBeforeClassConstructor expr
                    +> genExpr astContext expr)
            +> recordExpr
        )
        +> (fun ctx ->
            match ctx.RecordBraceStart with
            | rbs :: rest ->
                if ctx.Column < rbs then
                    let offset =
                        (if ctx.Config.SpaceAroundDelimiter then
                             2
                         else
                             1)
                        + 1

                    let delta = Math.Max((rbs - ctx.Column) - offset, 0)
                    (!- System.String.Empty.PadRight(delta)) { ctx with RecordBraceStart = rest }
                else
                    sepNone { ctx with RecordBraceStart = rest }
            | [] -> sepNone ctx)
        +> sepNlnWhenWriteBeforeNewlineNotEmpty sepNone
        +> enterNodeTokenByName synExpr.Range RBRACE
        +> ifElseCtx lastWriteEventIsNewline sepCloseSFixed sepCloseS

    expr ctx

and genMultilineRecordInstanceAlignBrackets
    (inheritOpt: (SynType * SynExpr) option)
    (xs: (RecordFieldName * SynExpr option * BlockSeparator option) list)
    (eo: SynExpr option)
    synExpr
    astContext
    =
    let fieldsExpr =
        col sepSemiNln xs (genRecordFieldName astContext)

    let hasFields = List.isNotEmpty xs

    match inheritOpt, eo with
    | Some (inheritType, inheritExpr), None ->
        sepOpenS
        +> ifElse hasFields (indent +> sepNln) sepNone
        +> !- "inherit "
        +> genType astContext false inheritType
        +> addSpaceBeforeClassConstructor inheritExpr
        +> genExpr astContext inheritExpr
        +> ifElse
            hasFields
            (sepNln
             +> fieldsExpr
             +> unindent
             +> sepNln
             +> sepCloseSFixed)
            (sepSpace +> sepCloseSFixed)

    | None, Some e ->
        sepOpenS
        +> atCurrentColumnIndent (genExpr astContext e)
        +> (!- " with"
            +> indent
            +> whenShortIndent indent
            +> sepNln
            +> fieldsExpr
            +> unindent
            +> whenShortIndent unindent
            +> sepNln
            +> sepCloseSFixed)

    | _ ->
        (sepOpenSFixed
         +> indent
         +> sepNln
         +> fieldsExpr
         +> unindent
         +> enterNodeTokenByName synExpr.Range RBRACE
         +> ifElseCtx lastWriteEventIsNewline sepNone sepNln
         +> sepCloseSFixed)
    |> atCurrentColumnIndent

and genMultilineAnonRecord (isStruct: bool) fields copyInfo astContext =
    let recordExpr =
        let fieldsExpr =
            col sepSemiNln fields (genAnonRecordFieldName astContext)

        match copyInfo with
        | Some e ->
            genExpr astContext e
            +> (!- " with"
                +> indent
                +> sepNln
                +> fieldsExpr
                +> unindent)
        | None -> fieldsExpr

    onlyIf isStruct !- "struct "
    +> sepOpenAnonRecd
    +> atCurrentColumnIndent recordExpr
    +> sepCloseAnonRecd

and genMultilineAnonRecordAlignBrackets (isStruct: bool) fields copyInfo astContext =
    let fieldsExpr =
        col sepSemiNln fields (genAnonRecordFieldName astContext)

    let copyExpr fieldsExpr e =
        genExpr astContext e
        +> (!- " with"
            +> indent
            +> whenShortIndent indent
            +> sepNln
            +> fieldsExpr
            +> whenShortIndent unindent
            +> unindent)

    let genAnonRecord =
        match copyInfo with
        | Some ci ->
            sepOpenAnonRecd
            +> copyExpr fieldsExpr ci
            +> sepNln
            +> sepCloseAnonRecdFixed
        | None ->
            sepOpenAnonRecdFixed
            +> indent
            +> sepNln
            +> fieldsExpr
            +> unindent
            +> sepNln
            +> sepCloseAnonRecdFixed

    ifElse isStruct !- "struct " sepNone
    +> atCurrentColumnIndent genAnonRecord

and genObjExpr t eio bd ims range (astContext: ASTContext) =
    // Check the role of the second part of eio
    let param =
        opt sepNone (Option.map fst eio) (genExpr astContext)

    sepOpenS
    +> atCurrentColumn (
        !- "new " +> genType astContext false t +> param
        -- " with"
        +> indent
        +> sepNln
        +> genMemberBindingList
            { astContext with
                  InterfaceRange = Some range }
            bd
        +> unindent
        +> colPre sepNln sepNln ims (genInterfaceImpl astContext)
    )
    +> sepCloseS

and genObjExprAlignBrackets t eio bd ims range (astContext: ASTContext) =
    // Check the role of the second part of eio
    let param =
        opt sepNone (Option.map fst eio) (genExpr astContext)

    let genObjExpr =
        atCurrentColumn (
            !- "new " +> genType astContext false t +> param
            -- " with"
            +> indent
            +> sepNln
            +> genMemberBindingList
                { astContext with
                      InterfaceRange = Some range }
                bd
            +> unindent
            +> colPre sepNln sepNln ims (genInterfaceImpl astContext)
        )

    atCurrentColumnIndent (sepOpenS +> genObjExpr +> sepNln +> sepCloseSFixed)

and genMultiLineArrayOrList
    (isArray: bool)
    xs
    (openingTokenRange: Range)
    (closingTokenRange: Range)
    (astContext: ASTContext)
    ctx
    =
    if isArray then
        (tokN openingTokenRange LBRACK_BAR sepOpenA
         +> atCurrentColumn (
             sepNlnWhenWriteBeforeNewlineNotEmpty sepNone
             +> col sepSemiNln xs (genExpr astContext)
             +> tokN closingTokenRange BAR_RBRACK (ifElseCtx lastWriteEventIsNewline sepCloseAFixed sepCloseA)
         ))
            ctx
    else
        (tokN openingTokenRange LBRACK sepOpenL
         +> atCurrentColumn (
             sepNlnWhenWriteBeforeNewlineNotEmpty sepNone
             +> col sepSemiNln xs (genExpr astContext)
             +> tokN closingTokenRange RBRACK (ifElseCtx lastWriteEventIsNewline sepCloseLFixed sepCloseL)
         ))
            ctx

and genMultiLineArrayOrListAlignBrackets (isArray: bool) xs openingTokenRange closingTokenRange astContext =
    let isLastItem (x: SynExpr) =
        List.tryLast xs
        |> Option.map (fun i -> RangeHelpers.rangeEq i.Range x.Range)
        |> Option.defaultValue false

    fun ctx ->
        let innerExpr =
            xs
            |> List.fold
                (fun acc e (ctx: Context) ->
                    let isLastItem = isLastItem e

                    (acc
                     +> genExpr astContext e
                     +> ifElse isLastItem sepNone sepSemiNln)
                        ctx)
                sepNone
            |> atCurrentColumn

        let expr =
            if isArray then
                tokN openingTokenRange LBRACK_BAR sepOpenAFixed
                +> indent
                +> sepNlnUnlessLastEventIsNewline
                +> innerExpr
                +> unindent
                +> sepNlnUnlessLastEventIsNewline
                +> tokN closingTokenRange BAR_RBRACK sepCloseAFixed
            else
                tokN openingTokenRange LBRACK sepOpenLFixed
                +> indent
                +> sepNlnUnlessLastEventIsNewline
                +> innerExpr
                +> unindent
                +> sepNlnUnlessLastEventIsNewline
                +> tokN closingTokenRange RBRACK sepCloseLFixed

        expr ctx

/// Use in indexed set and get only
and genIndexers astContext node =
    // helper to generate the remaining indexer expressions
    // (pulled out due to duplication)
    let inline genRest astContext (es: _ list) =
        ifElse es.IsEmpty sepNone (sepComma +> genIndexers astContext es)

    // helper to generate a single indexer expression with support for the from-end slice marker
    let inline genSingle astContext (isFromEnd: bool) (e: SynExpr) =
        ifElse isFromEnd (!- "^") sepNone
        +> genExpr astContext e

    match node with
    // list.[*]
    | Indexer (Pair ((IndexedVar None, _), (IndexedVar None, _))) :: es -> !- "*" +> genRest astContext es
    // list.[(fromEnd)<idx>..]
    | Indexer (Pair ((IndexedVar (Some e01), e1FromEnd), (IndexedVar None, _))) :: es ->
        genSingle astContext e1FromEnd e01 -- ".."
        +> genRest astContext es
    // list.[..(fromEnd)<idx>]
    | Indexer (Pair ((IndexedVar None, _), (IndexedVar (Some e2), e2FromEnd))) :: es ->
        !- ".."
        +> genSingle astContext e2FromEnd e2
        +> genRest astContext es
    // list.[(fromEnd)<idx>..(fromEnd)<idx>]
    | Indexer (Pair ((IndexedVar (Some e01), e1FromEnd), (IndexedVar (Some eo2), e2FromEnd))) :: es ->
        genSingle astContext e1FromEnd e01 -- ".."
        +> genSingle astContext e2FromEnd eo2
        +> genRest astContext es
    // list.[*]
    | Indexer (Single (IndexedVar None, _)) :: es -> !- "*" +> genRest astContext es
    // list.[(fromEnd)<idx>]
    | Indexer (Single (eo, fromEnd)) :: es ->
        genSingle astContext fromEnd eo
        +> genRest astContext es
    | _ -> sepNone

and genApp astContext e es ctx =
    let shortExpression =
        let addFirstSpace =
            ifElseCtx
                (fun ctx ->
                    match es with
                    | [] -> false
                    | [ h ]
                    | h :: _ -> addSpaceBeforeParensInFunCall e h ctx)
                sepSpace
                sepNone

        let genEx e =
            if isCompExpr e then
                sepSpace
                +> sepOpenSFixed
                +> sepSpace
                +> indent
                +> autoNlnIfExpressionExceedsPageWidth (genExpr astContext e)
                +> unindent
            else
                genExpr astContext e

        atCurrentColumn (
            genExpr astContext e
            +> addFirstSpace
            +> col sepSpace es genEx
        )

    let isParenLambda =
        (function
        | Paren (_, Lambda _, _, _)
        | Paren (_, MatchLambda _, _, _) -> true
        | _ -> false)

    let shouldHaveAlternativeLambdaStyle =
        let hasLambdas = List.exists isParenLambda es

        ctx.Config.MultiLineLambdaClosingNewline
        && hasLambdas

    let longExpression =
        if shouldHaveAlternativeLambdaStyle then
            let hasMultipleArguments = (List.length es) > 1

            let sepSpaceAfterFunctionName ctx =
                match e with
                | UppercaseSynExpr -> onlyIf ctx.Config.SpaceBeforeUppercaseInvocation sepSpace ctx
                | LowercaseSynExpr -> onlyIf ctx.Config.SpaceBeforeLowercaseInvocation sepSpace ctx

            let multipleArguments =
                col
                    sepNln
                    es
                    (fun e ->
                        let genLambda
                            (pats: Context -> Context)
                            (bodyExpr: SynExpr)
                            (lpr: Range)
                            (rpr: Range option)
                            (arrowRange: Range)
                            (pr: Range)
                            : Context -> Context =
                            leadingExpressionIsMultiline
                                (sepOpenTFor lpr -- "fun "
                                 +> pats
                                 +> indent
                                 +> triviaAfterArrow arrowRange
                                 +> autoNlnIfExpressionExceedsPageWidth (genExprKeepIndentInBranch astContext bodyExpr)
                                 +> unindent)
                                (fun isMultiline ->
                                    onlyIf isMultiline sepNln
                                    +> sepCloseTFor rpr e.Range)
                            |> genTriviaFor SynExpr_Paren pr

                        match e with
                        | Paren (lpr, Lambda (pats, expr, _range), rpr, pr) ->
                            let arrowRange =
                                List.last pats
                                |> fun lastPat -> ctx.MkRange lastPat.Range.End expr.Range.Start

                            genLambda (col sepSpace pats (genPat astContext)) expr lpr rpr arrowRange pr
                        | _ -> genExpr astContext e)

            let singleLambdaArgument =
                col
                    sepSpace
                    es
                    (fun e ->
                        let genLambda pats (bodyExpr: SynExpr) lpr rpr arrowRange lambdaRange =
                            sepOpenTFor lpr
                            +> (!- "fun "
                                +> pats
                                +> indent
                                +> triviaAfterArrow arrowRange
                                +> autoNlnIfExpressionExceedsPageWidth (genExprKeepIndentInBranch astContext bodyExpr)
                                |> genTriviaFor SynExpr_Lambda lambdaRange)
                            +> unindent
                            +> sepNln
                            +> sepCloseTFor rpr e.Range

                        match e with
                        | Paren (lpr, Lambda (pats, expr, range), rpr, _) ->
                            let arrowRange =
                                List.last pats
                                |> fun lastPat -> ctx.MkRange lastPat.Range.End expr.Range.Start

                            genLambda (col sepSpace pats (genPat astContext)) expr lpr rpr arrowRange range
                        | Paren (lpr, (MatchLambda _ as me), rpr, pr) ->
                            sepOpenTFor lpr
                            +> indent
                            +> sepNln
                            +> genExpr astContext me
                            +> unindent
                            +> sepNln
                            +> sepCloseTFor rpr e.Range
                            |> genTriviaFor SynExpr_Paren pr
                        | _ -> genExpr astContext e)

            let argExpr =
                if hasMultipleArguments then
                    multipleArguments
                else
                    singleLambdaArgument

            genExpr astContext e
            +> ifElse (not hasMultipleArguments) sepSpaceAfterFunctionName (indent +> sepNln)
            +> argExpr
            +> onlyIf hasMultipleArguments unindent
        else
            atCurrentColumn (
                genExpr astContext e
                +> indent
                +> sepNln
                +> col sepNln es (genExpr astContext)
                +> unindent
            )

    if List.exists
        (function
        | CompExpr _ -> true
        | _ -> false)
        es then
        shortExpression ctx
    else
        expressionFitsOnRestOfLine shortExpression longExpression ctx

and genAppWithTupledArgument (e, lpr, ts, tr, rpr, pr) astContext =
    genExpr astContext e
    +> sepSpace
    +> tokN lpr LPAREN sepOpenT
    +> (col sepComma ts (genExpr astContext)
        |> genTriviaFor SynExpr_Tuple tr)
    +> tokN (Option.defaultValue pr rpr) RPAREN sepCloseT

and genAlternativeAppWithTupledArgument (e, lpr, ts, tr, rpr, pr) astContext =
    genExpr astContext e
    +> indent
    +> sepNln
    +> tokN lpr LPAREN sepOpenT
    +> indent
    +> sepNln
    +> (col (sepComma +> sepNln) ts (genExpr astContext)
        |> genTriviaFor SynExpr_Tuple tr)
    +> unindent
    +> sepNln
    +> tokN (Option.defaultValue pr rpr) RPAREN sepCloseT
    +> unindent

and genAlternativeAppWithSingleParenthesisArgument (e, lpr, a, rpr, pr) astContext =
    genExpr astContext e
    +> sepSpaceWhenOrIndentAndNlnIfExpressionExceedsPageWidth
        (fun ctx ->
            match e with
            | Paren _ -> true
            | UppercaseSynExpr _ -> ctx.Config.SpaceBeforeUppercaseInvocation
            | LowercaseSynExpr _ -> ctx.Config.SpaceBeforeLowercaseInvocation)
        (tokN lpr LPAREN sepOpenT
         +> expressionFitsOnRestOfLine
             (genExpr astContext a)
             (indent
              +> sepNln
              +> genExpr astContext a
              +> unindent
              +> sepNln)
         +> tokN (Option.defaultValue pr rpr) RPAREN sepCloseT)

and genAppWithSingleParenthesisArgument (e, lpr, a, rpr, pr) astContext =
    genExpr astContext e
    +> sepSpace
    +> tokN lpr LPAREN sepOpenT
    +> (genExpr astContext a)
    +> tokN (Option.defaultValue pr rpr) RPAREN sepCloseT

and genExprInIfOrMatch astContext (e: SynExpr) (ctx: Context) : Context =
    let short =
        sepNlnWhenWriteBeforeNewlineNotEmpty sepSpace
        +> genExpr astContext e

    let long =
        let hasCommentBeforeExpr (e: SynExpr) =
            TriviaHelpers.``has content before that matches``
                (fun tn -> RangeHelpers.rangeEq tn.Range e.Range)
                (function
                | Comment (LineCommentOnSingleLine _) -> true
                | _ -> false)
                (Map.tryFindOrEmptyList (synExprToFsAstType e |> fst) ctx.TriviaMainNodes)

        let indentNlnUnindentNln f =
            indent +> sepNln +> f +> unindent +> sepNln

        let fallback =
            if hasCommentBeforeExpr e then
                genExpr astContext e |> indentNlnUnindentNln
            else
                sepNlnWhenWriteBeforeNewlineNotEmpty sepNone
                +> genExpr astContext e

        match e with
        | App (SynExpr.DotGet _, [ (Paren _) ]) -> atCurrentColumn (genExpr astContext e)
        | Paren (lpr, (AppSingleParenArg _ as ate), rpr, pr) ->
            sepOpenTFor lpr
            +> atCurrentColumnIndent (genExpr astContext ate)
            +> sepCloseTFor rpr pr
        | AppParenArg app ->
            genAlternativeAppWithParenthesis app astContext
            |> indentNlnUnindentNln
        | InfixApp (s, e, (AppParenArg app as e1), e2) ->
            (expressionFitsOnRestOfLine (genExpr astContext e1) (genAlternativeAppWithParenthesis app astContext)
             +> ifElse (noBreakInfixOps.Contains(s)) sepSpace sepNln
             +> genInfixOperator s e
             +> sepSpace
             +> genExpr astContext e2)
            |> indentNlnUnindentNln
        | InfixApp (s, e, e1, (AppParenArg app as e2)) ->
            (genExpr astContext e1
             +> sepNln
             +> genInfixOperator s e
             +> sepSpace
             +> expressionFitsOnRestOfLine (genExpr astContext e2) (genAlternativeAppWithParenthesis app astContext))
            |> indentNlnUnindentNln
        // very specific fix for 1380
        | SameInfixApps (Paren (lpr, AppParenArg e, rpr, pr), es) ->
            (sepOpenTFor lpr
             +> genAlternativeAppWithParenthesis e astContext
             +> sepCloseTFor rpr pr
             +> sepNln
             +> col
                 sepNln
                 es
                 (fun (opText, opExpr, e) ->
                     genInfixOperator opText opExpr
                     +> sepSpace
                     +> (match e with
                         | Paren (lpr, AppParenArg app, rpr, pr) ->
                             sepOpenTFor lpr
                             +> genAlternativeAppWithParenthesis app astContext
                             +> sepCloseTFor rpr pr
                         | _ -> genExpr astContext e)))
            |> indentNlnUnindentNln
        | InfixApp _ -> fallback
        | App (SynExpr.Ident _, _)
        | App (SynExpr.LongIdent _, _) ->
            indent
            +> sepNln
            +> genExpr astContext e
            +> unindent
        | SynExpr.Match _
        | SynExpr.MatchBang _
        | SynExpr.TryWith _
        | SynExpr.TryFinally _ -> genExpr astContext e |> indentNlnUnindentNln
        | DotGetAppParen (DotGetAppParen (e1, px1, lids1), px2, lids2) ->
            genExpr astContext e1
            +> genExpr astContext px1
            +> indent
            +> sepNln
            +> genLidsWithDotsAndNewlines lids1
            +> genExpr astContext px2
            +> sepNln
            +> genLidsWithDotsAndNewlines lids2
            +> unindent
            |> genTriviaFor SynExpr_DotGet e.Range
            |> indentNlnUnindentNln
        | _ -> fallback

    expressionFitsOnRestOfLine short long ctx

and genWithAfterMatch (withRange: Range) =
    tokN
        withRange
        WITH
        (fun ctx ->
            let hasContentOnLastLine =
                List.tryHead ctx.WriterModel.Lines
                |> Option.map String.isNotNullOrWhitespace
                |> Option.defaultValue false

            if hasContentOnLastLine then
                // add a space if there is no newline right after the expression
                (!- " with") ctx
            else
                // add the indentation in spaces if there is no content on the current line
                (rep ctx.Config.IndentSize (!- " ") +> !- "with") ctx)

and genAlternativeAppWithParenthesis app astContext =
    match app with
    | Choice1Of2 t -> genAlternativeAppWithTupledArgument t astContext
    | Choice2Of2 s -> genAlternativeAppWithSingleParenthesisArgument s astContext

and genAppWithParenthesis app astContext =
    match app with
    | Choice1Of2 t -> genAppWithTupledArgument t astContext
    | Choice2Of2 s -> genAppWithSingleParenthesisArgument s astContext

and collectMultilineItemForSynExpr
    (astContext: ASTContext)
    (inKeyWordTrivia: TriviaNode list)
    (e: SynExpr)
    : ColMultilineItem list =
    match e with
    | LetOrUses (bs, e) ->
        collectMultilineItemForLetOrUses
            astContext
            inKeyWordTrivia
            bs
            e
            (collectMultilineItemForSynExpr astContext inKeyWordTrivia e)
    | Sequentials s ->
        s
        |> List.collect (collectMultilineItemForSynExpr astContext inKeyWordTrivia)
    | _ ->
        let t, r = synExprToFsAstType e
        [ ColMultilineItem(genExpr astContext e, sepNlnConsideringTriviaContentBeforeForMainNode t r) ]

and collectMultilineItemForLetOrUses
    (astContext: ASTContext)
    (inKeyWordTrivia: TriviaNode list)
    (bs: (string * SynBinding) list)
    (e: SynExpr)
    (itemsForExpr: ColMultilineItem list)
    : ColMultilineItem list =
    // It be nice if the `in` keyword was part of the AST tree as suggested in
    // https://github.com/dotnet/fsharp/issues/10198
    let bindingHasInKeyword (binding: SynBinding) : bool =
        let inRange =
            Range.mkRange binding.RangeOfBindingAndRhs.FileName binding.RangeOfBindingAndRhs.End e.Range.Start

        inKeyWordTrivia
        |> TriviaHelpers.``keyword token after start column and on same line`` inRange
        |> List.isNotEmpty

    let multilineBinding p x =
        let expr =
            enterNodeFor (synBindingToFsAstType x) x.RangeOfBindingAndRhs
            +> genLetBinding
                { astContext with
                      IsFirstChild = p <> "and" }
                p
                x
            +> genInKeyword x e

        let range = x.RangeOfBindingAndRhs

        let sepNln =
            sepNlnConsideringTriviaContentBeforeForMainNode (synBindingToFsAstType x) range

        ColMultilineItem(expr, sepNln)

    let multipleOrLongBs bs =
        bs
        |> List.map (fun (p, x) -> multilineBinding p x)

    match bs, itemsForExpr with
    | [], _ -> itemsForExpr
    | [ p, b ], [ ColMultilineItem (expr, sepNlnForExpr) ] ->
        // This is a trickier case
        // maybe the let binding and expression are short so they form one ColMultilineItem
        // Something like: let a = 1 in ()

        let range = b.RangeOfBindingAndRhs

        let sepNlnForBinding =
            sepNlnConsideringTriviaContentBeforeForMainNode (synBindingToFsAstType b) range

        if bindingHasInKeyword b then
            // single multiline item
            let expr =
                enterNodeFor (synBindingToFsAstType b) b.RangeOfBindingAndRhs
                +> genLetBinding astContext p b
                +> genInKeyword b e
                +> expressionFitsOnRestOfLine expr (sepNln +> sepNlnForExpr +> expr)

            [ ColMultilineItem(expr, sepNlnForBinding) ]
        else
            multipleOrLongBs bs @ itemsForExpr
    | bs, _ -> multipleOrLongBs bs @ itemsForExpr

and genInKeyword (binding: SynBinding) (e: SynExpr) (ctx: Context) =
    let inKeyWordTrivia (binding: SynBinding) =
        let inRange =
            ctx.MkRange binding.RangeOfBindingAndRhs.End e.Range.Start

        Map.tryFindOrEmptyList IN ctx.TriviaTokenNodes
        |> TriviaHelpers.``keyword token after start column and on same line`` inRange
        |> List.tryHead

    match inKeyWordTrivia binding with
    | Some (_, tn) ->
        (printContentBefore tn
         +> !- " in "
         +> printContentAfter tn)
            ctx
    | None -> sepNone ctx

and sepNlnBetweenTypeAndMembers (tdr: SynTypeDefnRepr) (ms: SynMemberDefn list) =
    match List.tryHead ms with
    | Some m ->
        let range, mainNodeType =
            match m with
            | SynMemberDefn.Interface (_, _, r) -> r, SynMemberDefn_Interface
            | SynMemberDefn.Open (_, r) -> r, SynMemberDefn_Open
            | SynMemberDefn.Member (_, r) -> r, SynMemberDefn_Member
            | SynMemberDefn.ImplicitCtor (_, _, _, _, _, r) -> r, SynMemberDefn_ImplicitCtor
            | SynMemberDefn.ImplicitInherit (_, _, _, r) -> r, SynMemberDefn_ImplicitInherit
            | SynMemberDefn.LetBindings (_, _, _, r) -> r, SynMemberDefn_LetBindings
            | SynMemberDefn.AbstractSlot (_, _, r) -> r, SynMemberDefn_AbstractSlot
            | SynMemberDefn.Inherit (_, _, r) -> r, SynMemberDefn_Inherit
            | SynMemberDefn.ValField (_, r) -> r, SynMemberDefn_ValField
            | SynMemberDefn.NestedType (_, _, r) -> r, SynMemberDefn_NestedType
            | SynMemberDefn.AutoProperty (_, _, _, _, _, _, _, _, _, _, r) -> r, SynMemberDefn_AutoProperty

        sepNlnTypeAndMembers tdr.Range.End range mainNodeType
    | None -> sepNone

and genTypeDefn astContext (TypeDef (ats, px, ao, tds, tcs, tdr, ms, s, preferPostfix) as node) =
    let typeName =
        genPreXmlDoc px
        +> ifElse
            astContext.IsFirstChild
            (genAttributes astContext ats -- "type ")
            (!- "and " +> genOnelinerAttributes astContext ats)
        +> opt sepSpace ao genAccess
        +> genTypeAndParam astContext s tds tcs preferPostfix

    match tdr with
    | Simple (TDSREnum ecs) ->
        typeName
        +> sepEq
        +> indent
        +> sepNln
        +> genTriviaFor
            SynTypeDefnSimpleRepr_Enum
            tdr.Range
            (col sepNln ecs (genEnumCase astContext)
             +> onlyIf (List.isNotEmpty ms) sepNln
             +> sepNlnBetweenTypeAndMembers tdr ms
             +> genMemberDefnList
                 { astContext with
                       InterfaceRange = None }
                 ms
             // Add newline after un-indent to be spacing-correct
             +> unindent)

    | Simple (TDSRUnion (ao', xs)) ->
        let unionCases (ctx: Context) =
            match xs with
            | [] -> ctx
            | [ UnionCase (attrs, _, _, _, UnionCaseType fields) as x ] when List.isEmpty ms ->
                let hasVerticalBar =
                    ctx.Config.BarBeforeDiscriminatedUnionDeclaration
                    || List.isNotEmpty attrs
                    || List.isEmpty fields

                let short =
                    genTriviaFor
                        SynTypeDefnSimpleRepr_Union
                        tdr.Range
                        (opt sepSpace ao' genAccess
                         +> genUnionCase astContext hasVerticalBar x)

                let long =
                    genTriviaFor
                        SynTypeDefnSimpleRepr_Union
                        tdr.Range
                        (opt sepSpace ao' genAccess
                         +> genUnionCase astContext true x)

                expressionFitsOnRestOfLine (indent +> sepSpace +> short) (indent +> sepNln +> long) ctx
            | xs ->
                indent
                +> sepNln
                +> genTriviaFor
                    SynTypeDefnSimpleRepr_Union
                    tdr.Range
                    (opt sepNln ao' genAccess
                     +> col sepNln xs (genUnionCase astContext true))
                <| ctx

        typeName
        +> sepEq
        +> unionCases
        +> onlyIf (List.isNotEmpty ms) sepNln
        +> sepNlnBetweenTypeAndMembers tdr ms
        +> genMemberDefnList
            { astContext with
                  InterfaceRange = None }
            ms
        +> unindent

    | Simple (TDSRRecord (ao', fs)) ->
        let smallExpression =
            sepSpace
            +> optSingle (fun ao -> genAccess ao +> sepSpace) ao'
            +> sepOpenS
            +> leaveLeftBrace tdr.Range
            +> col sepSemi fs (genField astContext "")
            +> sepCloseS
            +> leaveNodeTokenByName node.Range RBRACE

        let multilineExpression =
            ifAlignBrackets
                (genMultilineSimpleRecordTypeDefnAlignBrackets tdr ms ao' fs astContext)
                (genMultilineSimpleRecordTypeDefn tdr ms ao' fs astContext)

        let bodyExpr ctx =
            let size = getRecordSize ctx fs

            if (List.isEmpty ms) then
                (isSmallExpression size smallExpression multilineExpression
                 +> leaveNodeFor SynTypeDefnSimpleRepr_Record tdr.Range // this will only print something when there is trivia after } in the short expression
                // Yet it cannot be part of the short expression otherwise the multiline expression would be triggered unwillingly.
                )
                    ctx
            else
                multilineExpression ctx

        typeName
        +> sepEq
        +> indent
        +> enterNodeFor SynTypeDefnSimpleRepr_Record tdr.Range
        +> bodyExpr
        +> leaveNodeFor SynTypeDefnSimpleRepr_Record tdr.Range
        +> unindent

    | Simple TDSRNone -> typeName
    | Simple (TDSRTypeAbbrev t) ->
        let genTypeAbbrev =
            let needsParenthesis =
                match t with
                | SynType.Tuple (isStruct, typeNames, _) -> (isStruct && List.length typeNames > 1)
                | _ -> false

            ifElse needsParenthesis sepOpenT sepNone
            +> genType astContext false t
            +> ifElse needsParenthesis sepCloseT sepNone
            |> genTriviaFor SynTypeDefnSimpleRepr_TypeAbbrev tdr.Range

        let genMembers =
            ifElse
                (List.isEmpty ms)
                (!- "")
                (indent ++ "with"
                 +> indent
                 +> sepNln
                 +> sepNlnBetweenTypeAndMembers tdr ms
                 +> genMemberDefnList
                     { astContext with
                           InterfaceRange = None }
                     ms
                 +> unindent
                 +> unindent)

        let genTypeBody =
            autoIndentAndNlnIfExpressionExceedsPageWidth genTypeAbbrev
            +> genMembers

        typeName +> sepEq +> sepSpace +> genTypeBody
    | Simple (TDSRException (ExceptionDefRepr (ats, px, ao, uc))) -> genExceptionBody astContext ats px ao uc

    | ObjectModel (TCSimple (TCInterface
                   | TCClass) as tdk,
                   MemberDefnList (impCtor, others),
                   range) ->
        let interfaceRange =
            match tdk with
            | TCSimple TCInterface -> Some range
            | _ -> None

        let astContext =
            { astContext with
                  InterfaceRange = interfaceRange }

        typeName
        +> sepSpaceBeforeClassConstructor
        +> leadingExpressionIsMultiline
            (opt sepNone impCtor (genMemberDefn astContext))
            (fun isMulti ctx ->
                if isMulti
                   && ctx.Config.AlternativeLongMemberDefinitions then
                    sepEqFixed ctx
                else
                    sepEq ctx)
        +> indent
        +> sepNln
        +> genTypeDefKind tdk
        +> indent
        +> onlyIf (List.isNotEmpty others) sepNln
        +> sepNlnBetweenTypeAndMembers tdr ms
        +> genMemberDefnList astContext others
        +> unindent
        ++ "end"
        +> unindent

    | ObjectModel (TCSimple TCStruct as tdk, MemberDefnList (impCtor, others), _) ->
        let sepMem =
            match ms with
            | [] -> sepNone
            | _ -> sepNln

        typeName
        +> opt sepNone impCtor (genMemberDefn astContext)
        +> sepEq
        +> indent
        +> sepNln
        +> genTypeDefKind tdk
        +> indent
        +> sepNln
        +> genMemberDefnList astContext others
        +> unindent
        ++ "end"
        +> sepMem
        // Prints any members outside the struct-end construct
        +> genMemberDefnList astContext ms
        +> unindent

    | ObjectModel (TCSimple TCAugmentation, _, _) ->
        typeName -- " with"
        +> indent
        // Remember that we use MemberDefn of parent node
        +> sepNln
        +> sepNlnBetweenTypeAndMembers tdr ms
        +> genMemberDefnList
            { astContext with
                  InterfaceRange = None }
            ms
        +> unindent

    | ObjectModel (TCDelegate (FunType ts), _, _) ->
        typeName
        +> sepEq
        +> sepSpace
        +> !- "delegate of "
        +> genTypeList astContext ts

    | ObjectModel (TCSimple TCUnspecified, MemberDefnList (impCtor, others), _) when not (List.isEmpty ms) ->
        typeName
        +> opt
            sepNone
            impCtor
            (genMemberDefn
                { astContext with
                      InterfaceRange = None })
        +> sepEq
        +> indent
        +> sepNln
        +> genMemberDefnList
            { astContext with
                  InterfaceRange = None }
            others
        +> sepNln
        -- "with"
        +> indent
        +> sepNln
        +> genMemberDefnList
            { astContext with
                  InterfaceRange = None }
            ms
        +> unindent
        +> unindent

    | ObjectModel (_, MemberDefnList (impCtor, others), _) ->
        typeName
        +> opt
            sepNone
            impCtor
            (fun mdf ->
                sepSpaceBeforeClassConstructor
                +> genMemberDefn
                    { astContext with
                          InterfaceRange = None }
                    mdf)
        +> sepEq
        +> indent
        +> sepNln
        +> genMemberDefnList
            { astContext with
                  InterfaceRange = None }
            others
        +> unindent

    | ExceptionRepr (ExceptionDefRepr (ats, px, ao, uc)) -> genExceptionBody astContext ats px ao uc
    |> genTriviaFor TypeDefn_ node.Range

and genMultilineSimpleRecordTypeDefn tdr ms ao' fs astContext =
    // the typeName is already printed
    sepNlnUnlessLastEventIsNewline
    +> opt (indent +> sepNln) ao' genAccess
    +> sepOpenS
    +> atCurrentColumn (
        leaveLeftBrace tdr.Range
        +> col sepSemiNln fs (genField astContext "")
    )
    +> sepCloseS
    +> leaveNodeTokenByName tdr.Range RBRACE
    +> optSingle (fun _ -> unindent) ao'
    +> onlyIf (List.isNotEmpty ms) sepNln
    +> sepNlnBetweenTypeAndMembers tdr ms
    +> genMemberDefnList
        { astContext with
              InterfaceRange = None }
        ms

and genMultilineSimpleRecordTypeDefnAlignBrackets tdr ms ao' fs astContext =
    // the typeName is already printed
    sepNlnUnlessLastEventIsNewline
    +> opt (indent +> sepNln) ao' genAccess
    +> sepOpenSFixed
    +> indent
    +> sepNln
    +> atCurrentColumn (
        leaveLeftBrace tdr.Range
        +> col sepSemiNln fs (genField astContext "")
    )
    +> unindent
    +> sepNln
    +> sepCloseSFixed
    +> optSingle (fun _ -> unindent) ao'
    +> onlyIf (List.isNotEmpty ms) sepNln
    +> sepNlnBetweenTypeAndMembers tdr ms
    +> genMemberDefnList
        { astContext with
              InterfaceRange = None }
        ms

and sepNlnBetweenSigTypeAndMembers (synTypeDefnRepr: SynTypeDefnSigRepr) (ms: SynMemberSig list) : Context -> Context =
    match List.tryHead ms with
    | Some m ->
        let range, mainNodeType =
            match m with
            | SynMemberSig.Interface (_, r) -> r, SynMemberSig_Interface
            | SynMemberSig.Inherit (_, r) -> r, SynMemberSig_Inherit
            | SynMemberSig.Member (_, _, r) -> r, SynMemberSig_Member
            | SynMemberSig.NestedType (_, r) -> r, SynMemberSig_NestedType
            | SynMemberSig.ValField (_, r) -> r, SynMemberSig_ValField

        sepNlnTypeAndMembers synTypeDefnRepr.Range.End range mainNodeType
    | None -> sepNone

and genSigTypeDefn astContext (SigTypeDef (ats, px, ao, tds, tcs, tdr, ms, s, preferPostfix, fullRange)) =
    let genTriviaForOnelinerAttributes f (ctx: Context) =
        match ats with
        | [] -> f ctx
        | h :: _ ->
            (enterNodeFor SynAttributeList_ h.Range
             +> f
             +> leaveNodeFor SynAttributeList_ h.Range)
                ctx

    let genXmlTypeKeywordAttrsAccess =
        genPreXmlDoc px
        +> ifElse
            astContext.IsFirstChild
            (genAttributes astContext ats -- "type ")
            ((!- "and " +> genOnelinerAttributes astContext ats)
             |> genTriviaForOnelinerAttributes)
        +> opt sepSpace ao genAccess

    let typeName =
        genXmlTypeKeywordAttrsAccess
        +> genTypeAndParam astContext s tds tcs preferPostfix

    match tdr with
    | SigSimple (TDSREnum ecs) ->
        typeName
        +> sepEq
        +> indent
        +> sepNln
        +> col sepNln ecs (genEnumCase astContext)
        +> sepNlnBetweenSigTypeAndMembers tdr ms
        +> colPre sepNln sepNln ms (genMemberSig astContext)
        // Add newline after un-indent to be spacing-correct
        +> unindent

    | SigSimple (TDSRUnion (ao', xs)) ->
        let unionCases (ctx: Context) =
            match xs with
            | [] -> ctx
            | [ UnionCase (attrs, _, _, _, UnionCaseType fields) as x ] when List.isEmpty ms ->
                let hasVerticalBar =
                    ctx.Config.BarBeforeDiscriminatedUnionDeclaration
                    || List.isNotEmpty attrs
                    || List.isEmpty fields

                let short =
                    genTriviaFor
                        SynTypeDefnSimpleRepr_Union
                        tdr.Range
                        (opt sepSpace ao' genAccess
                         +> genUnionCase astContext hasVerticalBar x)

                let long =
                    genTriviaFor
                        SynTypeDefnSimpleRepr_Union
                        tdr.Range
                        (opt sepSpace ao' genAccess
                         +> genUnionCase astContext true x)

                expressionFitsOnRestOfLine (indent +> sepSpace +> short) (indent +> sepNln +> long) ctx
            | xs ->
                (indent
                 +> sepNln
                 +> (opt sepNln ao' genAccess
                     +> col sepNln xs (genUnionCase astContext true)
                     |> genTriviaFor SynTypeDefnSimpleRepr_Union tdr.Range))
                    ctx

        typeName
        +> sepEq
        +> unionCases
        +> sepNlnBetweenSigTypeAndMembers tdr ms
        +> colPre sepNln sepNln ms (genMemberSig astContext)
        +> unindent

    | SigSimple (TDSRRecord (ao', fs)) ->
        let smallExpression =
            sepSpace
            +> optSingle (fun ao -> genAccess ao +> sepSpace) ao'
            +> sepOpenS
            +> leaveLeftBrace tdr.Range
            +> col sepSemi fs (genField astContext "")
            +> sepCloseS
            +> leaveNodeTokenByName tdr.Range RBRACE

        let multilineExpression =
            ifAlignBrackets
                (genSigSimpleRecordAlignBrackets tdr ms ao' fs astContext)
                (genSigSimpleRecord tdr ms ao' fs astContext)

        let bodyExpr ctx =
            let size = getRecordSize ctx fs

            if (List.isEmpty ms) then
                (isSmallExpression size smallExpression multilineExpression
                 +> leaveNodeFor SynTypeDefnSimpleRepr_Record tdr.Range // this will only print something when there is trivia after } in the short expression
                // Yet it cannot be part of the short expression otherwise the multiline expression would be triggered unwillingly.
                )
                    ctx
            else
                multilineExpression ctx

        typeName
        +> sepEq
        +> indent
        +> enterNodeFor SynTypeDefnSimpleRepr_Record tdr.Range
        +> bodyExpr
        +> leaveNodeFor SynTypeDefnSimpleRepr_Record tdr.Range
        +> unindent

    | SigSimple TDSRNone ->
        let genMembers =
            match ms with
            | [] -> sepNone
            | _ ->
                !- " with"
                +> indent
                +> sepNln
                +> sepNlnBetweenSigTypeAndMembers tdr ms
                +> col sepNln ms (genMemberSig astContext)
                +> unindent

        typeName +> genMembers
    | SigSimple (TDSRTypeAbbrev t) ->
        let genTypeAbbrev =
            let needsParenthesis =
                match t with
                | SynType.Tuple (isStruct, typeNames, _) -> (isStruct && List.length typeNames > 1)
                | _ -> false

            ifElse needsParenthesis sepOpenT sepNone
            +> genType astContext false t
            +> ifElse needsParenthesis sepCloseT sepNone

        let short =
            genTypeAndParam astContext s tds tcs preferPostfix
            +> sepEq
            +> sepSpace
            +> genTypeAbbrev

        let long =
            genTypeAndParam astContext s tds tcs preferPostfix
            +> sepSpace
            +> sepEqFixed
            +> indent
            +> sepNln
            +> genTypeAbbrev
            +> unindent

        genXmlTypeKeywordAttrsAccess
        +> expressionFitsOnRestOfLine short long
    | SigSimple (TDSRException (ExceptionDefRepr (ats, px, ao, uc))) -> genExceptionBody astContext ats px ao uc

    | SigObjectModel (TCSimple (TCStruct
                      | TCInterface
                      | TCClass) as tdk,
                      mds) ->
        typeName
        +> sepEq
        +> indent
        +> sepNln
        +> genTypeDefKind tdk
        +> indent
        +> colPre sepNln sepNln mds (genMemberSig astContext)
        +> unindent
        ++ "end"
        +> unindent

    | SigObjectModel (TCSimple TCAugmentation, _) ->
        typeName -- " with"
        +> indent
        +> sepNln
        // Remember that we use MemberSig of parent node
        +> col sepNln ms (genMemberSig astContext)
        +> unindent

    | SigObjectModel (TCDelegate (FunType ts), _) ->
        typeName +> sepEq +> sepSpace -- "delegate of "
        +> genTypeList astContext ts
    | SigObjectModel (_, mds) ->
        typeName
        +> sepEq
        +> indent
        +> sepNln
        +> col sepNln mds (genMemberSig astContext)
        +> unindent

    | SigExceptionRepr (SigExceptionDefRepr (ats, px, ao, uc)) -> genExceptionBody astContext ats px ao uc
    |> genTriviaFor TypeDefnSig_ fullRange

and genSigSimpleRecord tdr ms ao' fs astContext =
    // the typeName is already printed
    sepNlnUnlessLastEventIsNewline
    +> opt (indent +> sepNln) ao' genAccess
    +> sepOpenS
    +> atCurrentColumn (
        leaveLeftBrace tdr.Range
        +> col sepSemiNln fs (genField astContext "")
    )
    +> sepCloseS
    +> optSingle (fun _ -> unindent) ao'
    +> sepNlnBetweenSigTypeAndMembers tdr ms
    +> colPre sepNln sepNln ms (genMemberSig astContext)

and genSigSimpleRecordAlignBrackets tdr ms ao' fs astContext =
    // the typeName is already printed
    sepNlnUnlessLastEventIsNewline
    +> opt (indent +> sepNln) ao' genAccess
    +> sepOpenSFixed
    +> indent
    +> sepNln
    +> atCurrentColumn (
        leaveLeftBrace tdr.Range
        +> col sepSemiNln fs (genField astContext "")
    )
    +> unindent
    +> sepNln
    +> sepCloseSFixed
    +> optSingle (fun _ -> unindent) ao'
    +> sepNlnBetweenSigTypeAndMembers tdr ms
    +> colPre sepNln sepNln ms (genMemberSig astContext)

and genMemberSig astContext node =
    let range, mainNodeName =
        match node with
        | SynMemberSig.Member (_, _, r) -> r, SynMemberSig_Member
        | SynMemberSig.Interface (_, r) -> r, SynMemberSig_Interface
        | SynMemberSig.Inherit (_, r) -> r, SynMemberSig_Inherit
        | SynMemberSig.ValField (_, r) -> r, SynMemberSig_ValField
        | SynMemberSig.NestedType (_, r) -> r, SynMemberSig_NestedType

    match node with
    | MSMember (Val (ats, px, ao, s, _, t, vi, isInline, ValTyparDecls (tds, _, tcs)), mf) ->
        let (FunType namedArgs) = (t, vi)

        let isFunctionProperty =
            match t with
            | TFun _ -> true
            | _ -> false

        genPreXmlDoc px
        +> genAttributes astContext ats
        +> genMemberFlagsForMemberBinding
            { astContext with
                  InterfaceRange = None }
            mf
            range
        +> ifElse isInline (!- "inline ") sepNone
        +> opt sepSpace ao genAccess
        +> ifElse (s = "``new``") (!- "new") (!-s)
        +> genTypeParamPostfix astContext tds tcs
        +> sepColonWithSpacesFixed
        +> ifElse
            (List.isNotEmpty namedArgs)
            (autoIndentAndNlnIfExpressionExceedsPageWidth (genTypeList astContext namedArgs))
            (genConstraints astContext t vi)
        -- (genPropertyKind (not isFunctionProperty) mf.MemberKind)


    | MSInterface t -> !- "interface " +> genType astContext false t
    | MSInherit t -> !- "inherit " +> genType astContext false t
    | MSValField f -> genField astContext "val " f
    | MSNestedType _ -> invalidArg "md" "This is not implemented in F# compiler"
    |> genTriviaFor mainNodeName range

and genConstraints astContext (t: SynType) (vi: SynValInfo) =
    match t with
    | TWithGlobalConstraints (ti, tcs) ->
        let genType =
            match ti, vi with
            | TFuns ts, SynValInfo (curriedArgInfos, returnType) ->
                let namedArgInfos =
                    (List.map List.head curriedArgInfos)
                    @ [ returnType ]
                    |> List.map (fun (SynArgInfo (_, _, i)) -> i)

                coli
                    sepArrow
                    ts
                    (fun i t ->
                        let genNamedArg =
                            List.tryItem i namedArgInfos
                            |> Option.bind id
                            |> optSingle (fun (Ident s) -> !-s +> sepColon)

                        genNamedArg +> genType astContext false t)
            | _ -> genType astContext false ti

        genType
        +> sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (
            ifElse (List.isNotEmpty tcs) (!- "when ") sepSpace
            +> col wordAnd tcs (genTypeConstraint astContext)
        )
    | _ -> sepNone

and genTyparDecl astContext (TyparDecl (ats, tp)) =
    genOnelinerAttributes astContext ats
    +> genTypar astContext tp

and genTypeDefKind node =
    match node with
    | TCSimple TCUnspecified -> sepNone
    | TCSimple TCClass -> !- "class"
    | TCSimple TCInterface -> !- "interface"
    | TCSimple TCStruct -> !- "struct"
    | TCSimple TCRecord -> sepNone
    | TCSimple TCUnion -> sepNone
    | TCSimple TCAbbrev -> sepNone
    | TCSimple TCHiddenRepr -> sepNone
    | TCSimple TCAugmentation -> sepNone
    | TCSimple TCILAssemblyCode -> sepNone
    | TCDelegate _ -> sepNone

and genExceptionBody astContext ats px ao uc =
    genPreXmlDoc px +> genAttributes astContext ats
    -- "exception "
    +> opt sepSpace ao genAccess
    +> genUnionCase astContext false uc

and genException astContext (ExceptionDef (ats, px, ao, uc, ms) as node) =
    genExceptionBody astContext ats px ao uc
    +> ifElse
        ms.IsEmpty
        sepNone
        (!- " with"
         +> indent
         +> sepNln
         +> genMemberDefnList
             { astContext with
                   InterfaceRange = None }
             ms
         +> unindent)
    |> genTriviaFor SynExceptionDefn_ node.Range

and genSigException astContext (SigExceptionDef (ats, px, ao, uc, ms)) =
    genExceptionBody astContext ats px ao uc
    +> colPre sepNln sepNln ms (genMemberSig astContext)

and genUnionCase astContext (hasVerticalBar: bool) (UnionCase (ats, px, _, s, UnionCaseType fs) as node) =
    let shortExpr =
        colPre wordOf sepStar fs (genField { astContext with IsUnionField = true } "")

    let longExpr =
        wordOf
        +> indent
        +> sepNln
        +> atCurrentColumn (col (sepStar +> sepNln) fs (genField { astContext with IsUnionField = true } ""))
        +> unindent

    genPreXmlDoc px
    +> genTriviaBeforeClausePipe node.Range
    +> ifElse hasVerticalBar sepBar sepNone
    +> genOnelinerAttributes astContext ats
    -- s
    +> onlyIf (List.isNotEmpty fs) (expressionFitsOnRestOfLine shortExpr longExpr)
    |> genTriviaFor UnionCase_ node.Range

and genEnumCase astContext (EnumCase (ats, px, _, (_, _)) as node) =
    let genCase (ctx: Context) =
        let expr =
            match node with
            | EnumCase (_, _, identInAST, (c, r)) ->
                let triviaNode =
                    Map.tryFindOrEmptyList EnumCase_ ctx.TriviaMainNodes
                    |> List.tryFind (fun tn -> RangeHelpers.rangeEq tn.Range r)

                match triviaNode with
                | Some ({ ContentItself = Some (Number n) } as tn) ->
                    printContentBefore tn
                    +> !-identInAST
                    +> !- " = "
                    +> !-n
                    +> printContentAfter tn
                | Some tn ->
                    printContentBefore tn
                    +> !-identInAST
                    +> !- " = "
                    +> genConst c r
                    +> printContentAfter tn
                | None -> !-identInAST +> !- " = " +> genConst c r

        expr ctx

    genPreXmlDoc px
    +> genTriviaBeforeClausePipe node.Range
    +> sepBar
    +> genOnelinerAttributes astContext ats
    +> genCase

and genField astContext prefix (Field (ats, px, ao, isStatic, isMutable, t, so) as node) =
    let range =
        match node with
        | SynField.Field (_, _, _, _, _, _, _, range) -> range
    // Being protective on union case declaration
    let t =
        genType astContext astContext.IsUnionField t

    genPreXmlDoc px
    +> genAttributes astContext ats
    +> ifElse isStatic (!- "static ") sepNone
    -- prefix
    +> ifElse isMutable (!- "mutable ") sepNone
    +> opt sepSpace ao genAccess
    +> opt sepColon so (!-)
    +> t
    |> genTriviaFor Field_ range

and genType astContext outerBracket t =
    let rec loop current =
        match current with
        | THashConstraint t ->
            let wrapInParentheses f =
                match t with
                | TApp (_, ts, isPostfix) when (isPostfix && List.isNotEmpty ts) -> sepOpenT +> f +> sepCloseT
                | _ -> f

            !- "#" +> wrapInParentheses (loop t)
        | TMeasurePower (t, n) -> loop t -- "^" +> str n
        | TMeasureDivide (t1, t2) -> loop t1 -- " / " +> loop t2
        | TStaticConstant (c, r) -> genConst c r
        | TStaticConstantExpr e -> !- "const" +> genExpr astContext e
        | TStaticConstantNamed (t1, t2) ->
            loop t1 -- "="
            +> addSpaceIfSynTypeStaticConstantHasAtSignBeforeString t2
            +> loop t2
        | TArray (t, n) -> loop t -- " [" +> rep (n - 1) (!- ",") -- "]"
        | TAnon -> sepWild
        | TVar tp -> genTypar astContext tp
        // Drop bracket around tuples before an arrow
        | TFun (TTuple ts, t) -> loopTTupleList ts +> sepArrow +> loop t
        // Do similar for tuples after an arrow
        | TFun (t, TTuple ts) -> loop t +> sepArrow +> loopTTupleList ts
        | TFuns ts -> col sepArrow ts loop
        | TApp (TLongIdent "nativeptr", [ t ], true) when astContext.IsCStylePattern -> loop t -- "*"
        | TApp (TLongIdent "byref", [ t ], true) when astContext.IsCStylePattern -> loop t -- "&"
        | TApp (t, ts, isPostfix) ->
            let postForm =
                match ts with
                | [] -> loop t
                | [ t' ] ->
                    match t with
                    | SynType.LongIdent (LongIdentWithDots.LongIdentWithDots ([ lid ], _)) when lid.idText = "[]" ->
                        loop t' +> sepSpace +> loop t
                    | _ -> loop t +> sepOpenAng +> loop t' +> sepCloseAng
                | ts ->
                    sepOpenT
                    +> col sepComma ts loop
                    +> sepCloseT
                    +> loop t

            ifElse
                isPostfix
                postForm
                (loop t
                 +> genPrefixTypes astContext ts current.Range)

        | TLongIdentApp (t, s, ts) ->
            loop t -- sprintf ".%s" s
            +> genPrefixTypes astContext ts current.Range
        | TTuple ts -> loopTTupleList ts
        | TStructTuple ts ->
            !- "struct "
            +> sepOpenT
            +> loopTTupleList ts
            +> sepCloseT
        | TWithGlobalConstraints (TVar _, [ TyparSubtypeOfType _ as tc ]) -> genTypeConstraint astContext tc
        | TWithGlobalConstraints (TFuns ts, tcs) ->
            col sepArrow ts loop
            +> colPre (!- " when ") wordAnd tcs (genTypeConstraint astContext)
        | TWithGlobalConstraints (t, tcs) ->
            loop t
            +> colPre (!- " when ") wordAnd tcs (genTypeConstraint astContext)
        | SynType.LongIdent (LongIdentWithDots.LongIdentWithDots ([ lid ], _)) when
            (astContext.IsCStylePattern && lid.idText = "[]")
            ->
            !- "[]"
        | TLongIdent s ->
            ifElseCtx
                (fun ctx ->
                    not ctx.Config.StrictMode
                    && astContext.IsCStylePattern)
                (!-(if s = "unit" then "void" else s))
                (!-s)
            |> genTriviaFor Ident_ current.Range
        | TAnonRecord (isStruct, fields) ->
            let smallExpression =
                ifElse isStruct !- "struct " sepNone
                +> sepOpenAnonRecd
                +> col sepSemi fields (genAnonRecordFieldType astContext)
                +> sepCloseAnonRecd

            let longExpression =
                ifElse isStruct !- "struct " sepNone
                +> sepOpenAnonRecd
                +> atCurrentColumn (col sepSemiNln fields (genAnonRecordFieldType astContext))
                +> sepCloseAnonRecd

            fun (ctx: Context) ->
                let size = getRecordSize ctx fields
                isSmallExpression size smallExpression longExpression ctx
        | TParen innerT ->
            sepOpenT
            +> loop innerT
            +> sepCloseT
            +> leaveNodeTokenByName current.Range RPAREN
        | t -> failwithf "Unexpected type: %O" t

    and loopTTupleList =
        function
        | [] -> sepNone
        | [ (_, t) ] -> loop t
        | (isDivide, t) :: ts ->
            loop t -- (if isDivide then " / " else " * ")
            +> loopTTupleList ts

    match t with
    | TFun (TTuple ts, t) ->
        ifElse
            outerBracket
            (sepOpenT
             +> loopTTupleList ts
             +> sepArrow
             +> loop t
             +> sepCloseT)
            (loopTTupleList ts +> sepArrow +> loop t)
    | TFuns ts ->
        let short = col sepArrow ts loop

        let long =
            match ts with
            | [] -> sepNone
            | h :: rest ->
                loop h
                +> indent
                +> sepNln
                +> sepArrowFixed
                +> sepSpace
                +> col (sepNln +> sepArrowFixed +> sepSpace) rest loop
                +> unindent

        let genTs = expressionFitsOnRestOfLine short long

        ifElse outerBracket (sepOpenT +> genTs +> sepCloseT) genTs
    | TTuple ts -> ifElse outerBracket (sepOpenT +> loopTTupleList ts +> sepCloseT) (loopTTupleList ts)
    | _ -> loop t

// for example: FSharpx.Regex< @"(?<value>\d+)" >
and addSpaceIfSynTypeStaticConstantHasAtSignBeforeString (t: SynType) (ctx: Context) =
    let hasAtSign =
        match t with
        | TStaticConstant (_, r) ->
            TriviaHelpers.``has content itself that matches``
                (function
                | StringContent sc -> sc.StartsWith("@")
                | _ -> false)
                r
                (Map.tryFindOrEmptyList SynConst_String ctx.TriviaMainNodes)
        | _ -> false

    onlyIf hasAtSign sepSpace ctx

and genAnonRecordFieldType astContext (AnonRecordFieldType (s, t)) =
    !-s +> sepColon +> (genType astContext false t)

and genPrefixTypes astContext node (range: Range) ctx =
    match node with
    | [] -> ctx
    // Where <  and ^ meet, we need an extra space. For example:  seq< ^a >
    | TVar (Typar (_, true)) as t :: ts ->
        (!- "< "
         +> col sepComma (t :: ts) (genType astContext false)
         -- " >")
            ctx
    | t :: _ ->
        (!- "<"
         +> atCurrentColumnIndent (
             addSpaceIfSynTypeStaticConstantHasAtSignBeforeString t
             +> col sepComma node (genType astContext false)
             +> addSpaceIfSynTypeStaticConstantHasAtSignBeforeString t
         )
         +> tokN range GREATER (!- ">"))
            ctx

and genTypeList astContext node =
    let gt (t, args: SynArgInfo list) =
        match t, args with
        | TTuple ts', _ ->
            let hasBracket = not node.IsEmpty

            let gt sepBefore =
                if args.Length = ts'.Length then
                    col
                        sepBefore
                        (Seq.zip args (Seq.map snd ts'))
                        (fun (ArgInfo (ats, so, isOpt), t) ->
                            genOnelinerAttributes astContext ats
                            +> opt
                                sepColon
                                so
                                (if isOpt then
                                     (sprintf "?%s" >> (!-))
                                 else
                                     (!-))
                            +> genType astContext hasBracket t)
                else
                    col sepBefore ts' (snd >> genType astContext hasBracket)

            let shortExpr = gt sepStar
            let longExpr = gt (sepNln +> sepStarFixed)
            expressionFitsOnRestOfLine shortExpr longExpr

        | _, [ ArgInfo (ats, so, isOpt) ] ->
            match t with
            | TTuple _ -> not node.IsEmpty
            | TFun _ -> true // Fun is grouped by brackets inside 'genType astContext true t'
            | _ -> false
            |> fun hasBracket ->
                genOnelinerAttributes astContext ats
                +> opt
                    sepColon
                    so
                    (if isOpt then
                         (sprintf "?%s" >> (!-))
                     else
                         (!-))
                +> genType astContext hasBracket t
        | _ -> genType astContext false t

    let shortExpr = col sepArrow node gt

    let longExpr = col (sepArrow +> sepNln) node gt

    expressionFitsOnRestOfLine shortExpr longExpr

and genTypar astContext (Typar (s, isHead) as node) =
    ifElse isHead (ifElse astContext.IsFirstTypeParam (!- " ^") (!- "^")) (!- "'")
    -- s
    |> genTriviaFor SynType_Var node.Range

and genTypeConstraint astContext node =
    match node with
    | TyparSingle (kind, tp) ->
        genTypar astContext tp +> sepColon
        -- sprintf "%O" kind
    | TyparDefaultsToType (tp, t) ->
        !- "default "
        +> genTypar astContext tp
        +> sepColon
        +> genType astContext false t
    | TyparSubtypeOfType (tp, t) ->
        genTypar astContext tp -- " :> "
        +> genType astContext false t
    | TyparSupportsMember (tps, msg) ->
        genTyparList astContext tps
        +> sepColon
        +> sepOpenT
        +> genMemberSig astContext msg
        +> sepCloseT
    | TyparIsEnum (tp, ts) ->
        genTypar astContext tp +> sepColon -- "enum<"
        +> col sepComma ts (genType astContext false)
        -- ">"
    | TyparIsDelegate (tp, ts) ->
        genTypar astContext tp +> sepColon -- "delegate<"
        +> col sepComma ts (genType astContext false)
        -- ">"

and genInterfaceImpl astContext (InterfaceImpl (t, bs, range)) =
    match bs with
    | [] -> !- "interface " +> genType astContext false t
    | bs ->
        !- "interface " +> genType astContext false t
        -- " with"
        +> indent
        +> sepNln
        +> genMemberBindingList
            { astContext with
                  InterfaceRange = Some range }
            bs
        +> unindent

and genClause astContext hasBar (Clause (p, e, eo) as ce) =
    let arrowRange (ctx: Context) = ctx.MkRange p.Range.End e.Range.Start

    let astCtx =
        { astContext with
              IsInsideMatchClausePattern = true }

    let patAndBody =
        genPat astCtx p
        +> leadingExpressionIsMultiline
            (optPre
                (!- " when")
                sepNone
                eo
                (fun e ->
                    let short = sepSpace +> genExpr astContext e

                    let long =
                        match e with
                        | AppParenArg app ->
                            indent
                            +> sepNln
                            +> genAlternativeAppWithParenthesis app astContext
                            +> unindent
                        | e ->
                            indent
                            +> sepNln
                            +> (genExpr astContext e)
                            +> unindent

                    expressionFitsOnRestOfLine short long))
            (fun isMultiline ctx ->
                if isMultiline then
                    (indent
                     +> sepNln
                     +> tokN (arrowRange ctx) RARROW sepArrowFixed
                     +> sepNln
                     +> genExpr astContext e
                     +> unindent)
                        ctx
                else
                    (tokN (arrowRange ctx) RARROW sepArrow
                     +> autoIndentAndNlnIfExpressionExceedsPageWidth (genExpr astContext e))
                        ctx)

    genTriviaBeforeClausePipe p.Range
    +> (onlyIf hasBar sepBar +> patAndBody
        |> genTriviaFor SynMatchClause_Clause ce.Range)

and genClauses astContext cs =
    col sepNln cs (genClause astContext true)

/// Each multiline member definition has a pre and post new line.
and genMemberDefnList astContext nodes =
    let rec collectItems
        (nodes: SynMemberDefn list)
        (finalContinuation: ColMultilineItem list -> ColMultilineItem list)
        : ColMultilineItem list =
        match nodes with
        | [] -> finalContinuation []
        | PropertyWithGetSetMemberDefn (gs, rest) ->
            let attrs =
                getRangesFromAttributesFromSynBinding (fst gs)

            let rangeOfFirstMember = List.head nodes |> fun m -> m.Range

            let expr =
                enterNodeFor SynMemberDefn_Member rangeOfFirstMember
                +> genPropertyWithGetSet astContext gs (Some rangeOfFirstMember)

            let sepNln =
                sepNlnConsideringTriviaContentBeforeWithAttributesFor SynMemberDefn_Member rangeOfFirstMember attrs

            collectItems
                rest
                (fun restItems ->
                    ColMultilineItem(expr, sepNln) :: restItems
                    |> finalContinuation)
        | m :: rest ->
            let attrs =
                getRangesFromAttributesFromSynMemberDefinition m

            let expr = genMemberDefn astContext m

            let sepNln =
                sepNlnConsideringTriviaContentBeforeWithAttributesFor (synMemberDefnToFsAstType m) m.Range attrs

            collectItems
                rest
                (fun restItems ->
                    ColMultilineItem(expr, sepNln) :: restItems
                    |> finalContinuation)

    collectItems nodes id
    |> colWithNlnWhenItemIsMultilineUsingConfig

and genMemberDefn astContext node =
    match node with
    | MDNestedType _ -> invalidArg "md" "This is not implemented in F# compiler"
    | MDOpen s -> !-(sprintf "open %s" s)
    // What is the role of so
    | MDImplicitInherit (t, e, _) ->
        let genBasecall =
            let shortExpr = genExpr astContext e

            let longExpr =
                match e with
                | Paren (lpr, Tuple (es, tr), rpr, pr) ->
                    indent
                    +> sepNln
                    +> indent
                    +> sepOpenTFor lpr
                    +> sepNln
                    +> (col (sepComma +> sepNln) es (genExpr astContext)
                        |> genTriviaFor SynExpr_Tuple tr)
                    +> unindent
                    +> sepNln
                    +> unindent
                    +> sepCloseTFor rpr pr
                    +> unindent
                    |> genTriviaFor SynExpr_Paren pr
                | _ -> genExpr astContext e

            expressionFitsOnRestOfLine shortExpr longExpr

        !- "inherit "
        +> genType astContext false t
        +> addSpaceBeforeClassConstructor e
        +> genBasecall

    | MDInherit (t, _) -> !- "inherit " +> genType astContext false t
    | MDValField f -> genField astContext "val " f
    | MDImplicitCtor (ats, ao, ps, so) ->
        let rec simplePats ps =
            match ps with
            | SynSimplePats.SimplePats (pats, _) -> pats
            | SynSimplePats.Typed (spts, _, _) -> simplePats spts

        let genCtor =
            let shortExpr =
                optPre sepSpace sepSpace ao genAccess
                +> ((sepOpenT
                     +> col sepComma (simplePats ps) (genSimplePat astContext)
                     +> sepCloseT)
                    |> genTriviaFor SynSimplePats_SimplePats ps.Range)

            let emptyPats =
                let rec isEmpty ps =
                    match ps with
                    | SynSimplePats.SimplePats ([], _) -> true
                    | SynSimplePats.SimplePats _ -> false
                    | SynSimplePats.Typed (spts, _, _) -> isEmpty spts

                isEmpty ps

            let longExpr ctx =
                (indent
                 +> sepNln
                 +> optSingle (fun ao -> genAccess ao +> sepNln) ao
                 +> ifElse
                     emptyPats
                     (sepOpenT +> sepCloseT)
                     (fun ctx ->
                         let shortPats =
                             sepOpenT
                             +> col sepComma (simplePats ps) (genSimplePat astContext)
                             +> sepCloseT

                         let longPats =
                             sepOpenT
                             +> indent
                             +> sepNln
                             +> col (sepComma +> sepNln) (simplePats ps) (genSimplePat astContext)
                             +> unindent
                             +> sepNln
                             +> sepCloseT

                         let triviaBeforePats =
                             Map.tryFindOrEmptyList SynSimplePats_SimplePats ctx.TriviaMainNodes
                             |> List.tryFind (fun tn -> RangeHelpers.rangeEq tn.Range ps.Range)

                         match triviaBeforePats with
                         | Some tn ->
                             (printContentBefore tn
                              +> expressionFitsOnRestOfLine shortPats longPats
                              +> printContentAfter tn)
                                 ctx
                         | _ -> longPats ctx)
                 +> onlyIf ctx.Config.AlternativeLongMemberDefinitions sepNln
                 +> unindent)
                    ctx

            expressionFitsOnRestOfLine shortExpr longExpr

        // In implicit constructor, attributes should come even before access qualifiers
        ifElse ats.IsEmpty sepNone (sepSpace +> genOnelinerAttributes astContext ats)
        +> genCtor
        +> optPre (!- " as ") sepNone so (!-)

    | MDMember b -> genMemberBinding astContext b
    | MDLetBindings (isStatic, isRec, b :: bs) ->
        let prefix =
            if isStatic && isRec then
                "static let rec "
            elif isStatic then
                "static let "
            elif isRec then
                "let rec "
            else
                "let "

        let items =
            let bsItems =
                bs
                |> List.map
                    (fun andBinding ->
                        let expr =
                            enterNodeFor (synBindingToFsAstType b) andBinding.RangeOfBindingAndRhs
                            +> genLetBinding { astContext with IsFirstChild = false } "and " andBinding

                        ColMultilineItem(
                            expr,
                            sepNlnConsideringTriviaContentBeforeForMainNode
                                NormalBinding_
                                andBinding.RangeOfBindingAndRhs
                        ))

            ColMultilineItem(genLetBinding { astContext with IsFirstChild = true } prefix b, sepNone)
            :: bsItems

        colWithNlnWhenItemIsMultilineUsingConfig items

    | MDInterface (t, mdo, range) ->
        !- "interface "
        +> genType astContext false t
        +> opt
            sepNone
            mdo
            (fun mds ->
                !- " with"
                +> indent
                +> sepNln
                +> genMemberDefnList
                    { astContext with
                          // Reset this property to avoid problems with the generation of the attributes on the members
                          // See 1668
                          IsFirstChild = true
                          InterfaceRange = Some range }
                    mds
                +> unindent)

    | MDAutoProperty (ats, px, ao, mk, e, s, _isStatic, typeOpt, memberKindToMemberFlags) ->
        let isFunctionProperty =
            match typeOpt with
            | Some (TFun _) -> true
            | _ -> false

        genPreXmlDoc px
        +> genAttributes astContext ats
        +> genMemberFlags astContext (memberKindToMemberFlags mk)
        +> str "val "
        +> opt sepSpace ao genAccess
        -- s
        +> optPre sepColon sepNone typeOpt (genType astContext false)
        +> sepEq
        +> sepSpaceOrIndentAndNlnIfExpressionExceedsPageWidth (
            genExpr astContext e
            -- genPropertyKind (not isFunctionProperty) mk
        )

    | MDAbstractSlot (ats, px, ao, s, t, vi, ValTyparDecls (tds, _, tcs), MFMemberFlags mk) ->
        let (FunType namedArgs) = (t, vi)

        let isFunctionProperty =
            match t with
            | TFun _ -> true
            | _ -> false

        let genAbstractMemberKeyword (ctx: Context) =
            Map.tryFindOrEmptyList MEMBER ctx.TriviaTokenNodes
            |> List.choose
                (fun tn ->
                    if tn.Range.StartLine = node.Range.StartLine then
                        match tn.ContentItself with
                        | Some (Keyword kw) -> Some kw.Content
                        | _ -> None
                    else
                        None)
            |> List.tryHead
            |> fun keywordOpt ->
                match keywordOpt with
                | Some kw -> sprintf "%s %s" kw s
                | None -> sprintf "abstract %s" s
            |> fun s -> !- s ctx

        genPreXmlDoc px
        +> genAttributes astContext ats
        +> opt sepSpace ao genAccess
        +> genAbstractMemberKeyword
        +> genTypeParamPostfix astContext tds tcs
        +> sepColonWithSpacesFixed
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (genTypeList astContext namedArgs)
        -- genPropertyKind (not isFunctionProperty) mk
        +> autoIndentAndNlnIfExpressionExceedsPageWidth (genConstraints astContext t vi)

    | md -> failwithf "Unexpected member definition: %O" md
    |> genTriviaFor (synMemberDefnToFsAstType node) node.Range

and genPropertyKind useSyntacticSugar node =
    match node with
    | PropertyGet ->
        // Try to use syntactic sugar on real properties (not methods in disguise)
        if useSyntacticSugar then
            ""
        else
            " with get"
    | PropertySet -> " with set"
    | PropertyGetSet -> " with get, set"
    | _ -> ""

and genSimplePat astContext node =
    match node with
    | SPId (s, isOptArg, _) -> ifElse isOptArg (!-(sprintf "?%s" s)) (!-s)
    | SPTyped (sp, t) ->
        genSimplePat astContext sp
        +> sepColon
        +> genType astContext false t
    | SPAttrib (ats, sp) ->
        genOnelinerAttributes astContext ats
        +> genSimplePat astContext sp

and genSimplePats astContext node =
    match node with
    // Remove parentheses on an extremely simple pattern
    | SimplePats [ SPId _ as sp ] -> genSimplePat astContext sp
    | SimplePats ps ->
        sepOpenT
        +> col sepComma ps (genSimplePat astContext)
        +> sepCloseT
    | SPSTyped (ps, t) ->
        genSimplePats astContext ps
        +> sepColon
        +> genType astContext false t

and genPatRecordFieldName astContext (PatRecordFieldName (s1, s2, p)) =
    ifElse (s1 = "") (!-(sprintf "%s = " s2)) (!-(sprintf "%s.%s = " s1 s2))
    +> genPat
        { astContext with
              IsInsideMatchClausePattern = false }
        p // see issue 1252.

and genPatWithIdent astContext (ido, p) =
    opt (sepEq +> sepSpace) ido (!-)
    +> genPat astContext p

and genPat astContext pat =
    match pat with
    | PatOptionalVal s -> !-(sprintf "?%s" s)
    | PatAttrib (p, ats) ->
        genOnelinerAttributes astContext ats
        +> genPat astContext p
    | PatOr (p1, p2) ->
        let barRange (ctx: Context) = ctx.MkRange p1.Range.End p2.Range.Start

        genPat astContext p1
        +> ifElse astContext.IsInsideMatchClausePattern sepNln sepSpace
        +> fun ctx -> enterNodeTokenByName (barRange ctx) BAR ctx
        -- "| "
        +> genPat astContext p2
    | PatAnds ps -> col (!- " & ") ps (genPat astContext)
    | PatNullary PatNull -> !- "null"
    | PatNullary PatWild -> sepWild
    | PatTyped (p, t) ->
        // CStyle patterns only occur on extern declaration so it doesn't escalate to expressions
        // We lookup sources to get extern types since it has quite many exceptions compared to normal F# types
        ifElse
            astContext.IsCStylePattern
            (genType astContext false t
             +> sepSpace
             +> genPat astContext p)
            (genPat astContext p
             +> sepColon
             +> atCurrentColumnIndent (genType astContext false t))

    | PatNamed (ao, PatNullary PatWild, s) ->
        opt sepSpace ao genAccess
        +> infixOperatorFromTrivia pat.Range s
    | PatNamed (ao, p, s) ->
        opt sepSpace ao genAccess +> genPat astContext p
        -- sprintf " as %s" s
    | PatLongIdent (ao, s, ps, tpso) ->
        let aoc = opt sepSpace ao genAccess

        let tpsoc =
            opt sepNone tpso (fun (ValTyparDecls (tds, _, tcs)) -> genTypeParamPostfix astContext tds tcs)
        // Override escaped new keyword
        let s = if s = "``new``" then "new" else s

        match ps with
        | [] -> aoc -- s +> tpsoc
        | [ (_, PatTuple [ p1; p2 ]) ] when s = "(::)" ->
            aoc +> genPat astContext p1 -- " :: "
            +> genPat astContext p2
        | [ ido, p as ip ] ->
            aoc
            +> infixOperatorFromTrivia pat.Range s
            +> tpsoc
            +> ifElse
                (hasParenInPat p || Option.isSome ido)
                (ifElseCtx
                    (fun ctx -> addSpaceBeforeParensInFunDef ctx.Config.SpaceBeforeParameter s p)
                    sepSpace
                    sepNone)
                sepSpace
            +> ifElse
                (Option.isSome ido)
                (sepOpenT
                 +> genPatWithIdent astContext ip
                 +> sepCloseT)
                (genPatWithIdent astContext ip)
        // This pattern is potentially long
        | ps ->
            let hasBracket =
                ps |> Seq.map fst |> Seq.exists Option.isSome

            let genName = aoc -- s +> tpsoc +> sepSpace

            let genParameters =
                expressionFitsOnRestOfLine
                    (atCurrentColumn (col (ifElse hasBracket sepSemi sepSpace) ps (genPatWithIdent astContext)))
                    (atCurrentColumn (col sepNln ps (genPatWithIdent astContext)))

            genName
            +> ifElse hasBracket sepOpenT sepNone
            +> genParameters
            +> ifElse hasBracket sepCloseT sepNone

    | PatParen PatUnitConst -> !- "()"
    | PatParen p ->
        let isParenNecessary =
            if astContext.IsInsideMatchClausePattern then
                match p with
                | SynPat.Named (expression, _, _, _, _) ->
                    match expression with
                    | SynPat.Record (_, _) -> true
                    | _ -> false
                | SynPat.Wild _ -> false
                | _ -> true
            else
                match p with
                | SynPat.Named (expression, _, _, _, _) ->
                    match expression with
                    | SynPat.Wild _ -> false
                    | _ -> true
                | _ -> true

        expressionFitsOnRestOfLine
            (ifElse isParenNecessary sepOpenT sepSpace
             +> genPat astContext p
             +> enterNodeTokenByName pat.Range RPAREN
             +> ifElse isParenNecessary sepCloseT sepNone)

            (ifElse
                astContext.IsInsideMatchClausePattern
                (ifElse isParenNecessary ((indent +> sepNln +> sepOpenT +> indent +> sepNln)) sepNone)
                (ifElse isParenNecessary sepOpenT sepSpace)
             +> genPat astContext p
             +> enterNodeTokenByName pat.Range RPAREN
             +> (ifElse
                     astContext.IsInsideMatchClausePattern
                     (ifElse isParenNecessary (unindent +> sepNln +> sepCloseT +> unindent) sepNone)
                     (ifElse isParenNecessary sepCloseT sepNone)))
    | PatTuple ps ->
        expressionFitsOnRestOfLine
            (col sepComma ps (genPat astContext))
            (atCurrentColumn (col (sepComma +> sepNln) ps (genPat astContext)))
    | PatStructTuple ps ->
        !- "struct "
        +> sepOpenT
        +> atCurrentColumn (colAutoNlnSkip0 sepComma ps (genPat astContext))
        +> sepCloseT
    | PatSeq (patListType, [ PatOrs patOrs ]) ->
        let sepOpen, sepClose =
            match patListType with
            | PatArray -> sepOpenA, sepCloseA
            | PatList -> sepOpenL, sepCloseL

        let short =
            sepOpen
            +> col (sepSpace +> sepBar) patOrs (genPat astContext)
            +> sepClose

        let long =
            sepOpen
            +> atCurrentColumnIndent (
                match patOrs with
                | [] -> sepNone
                | hp :: pats ->
                    genPat astContext hp +> sepNln -- " "
                    +> atCurrentColumn (
                        sepBar
                        +> col (sepNln +> sepBar) pats (genPat astContext)
                    )
            )
            +> sepClose

        expressionFitsOnRestOfLine short long
    | PatSeq (PatList, ps) ->
        let genPats =
            let short =
                colAutoNlnSkip0 sepSemi ps (genPat astContext)

            let long = col sepSemiNln ps (genPat astContext)
            expressionFitsOnRestOfLine short long

        ifElse ps.IsEmpty (sepOpenLFixed +> sepCloseLFixed) (sepOpenL +> atCurrentColumn genPats +> sepCloseL)

    | PatSeq (PatArray, ps) ->
        let genPats =
            let short =
                colAutoNlnSkip0 sepSemi ps (genPat astContext)

            let long = col sepSemiNln ps (genPat astContext)
            expressionFitsOnRestOfLine short long

        ifElse ps.IsEmpty (sepOpenAFixed +> sepCloseAFixed) (sepOpenA +> atCurrentColumn genPats +> sepCloseA)

    | PatRecord xs ->
        let smallRecordExpr =
            sepOpenS
            +> col sepSemi xs (genPatRecordFieldName astContext)
            +> sepCloseS

        // Note that MultilineBlockBracketsOnSameColumn is not taken into account here.
        let multilineRecordExpr =
            sepOpenS
            +> atCurrentColumn (col sepSemiNln xs (genPatRecordFieldName astContext))
            +> sepCloseS

        let multilineRecordExprAlignBrackets =
            sepOpenSFixed
            +> indent
            +> sepNln
            +> atCurrentColumn (col sepSemiNln xs (genPatRecordFieldName astContext))
            +> unindent
            +> sepNln
            +> sepCloseSFixed
            |> atCurrentColumnIndent

        let multilineExpressionIfAlignBrackets =
            ifAlignBrackets multilineRecordExprAlignBrackets multilineRecordExpr

        fun ctx ->
            let size = getRecordSize ctx xs
            isSmallExpression size smallRecordExpr multilineExpressionIfAlignBrackets ctx
    | PatConst (c, r) -> genConst c r
    | PatIsInst t -> !- ":? " +> genType astContext false t
    // Quotes will be printed by inner expression
    | PatQuoteExpr e -> genExpr astContext e
    | p -> failwithf "Unexpected pattern: %O" p
    |> (match pat with
        | SynPat.Named _ -> genTriviaFor SynPat_Named pat.Range
        | SynPat.Wild _ -> genTriviaFor SynPat_Wild pat.Range
        | SynPat.LongIdent _ -> genTriviaFor SynPat_LongIdent pat.Range
        | SynPat.Paren _ -> genTriviaFor SynPat_Paren pat.Range
        | _ -> id)

and genSynBindingFunction
    (astContext: ASTContext)
    (isMemberDefinition: bool)
    (px: FSharp.Compiler.XmlDoc.PreXmlDoc)
    (ats: SynAttributes)
    (pref: Context -> Context)
    (ao: SynAccess option)
    (isInline: bool)
    (isMutable: bool)
    (functionName: string)
    (patRange: Range)
    (parameters: (string option * SynPat) list)
    (genericTypeParameters: SynValTyparDecls option)
    (e: SynExpr)
    (ctx: Context)
    =
    let spaceBefore, alternativeSyntax =
        if isMemberDefinition then
            ctx.Config.SpaceBeforeMember, ctx.Config.AlternativeLongMemberDefinitions
        else
            ctx.Config.SpaceBeforeParameter, ctx.Config.AlignFunctionSignatureToIndentation

    let genAttrIsFirstChild =
        onlyIf astContext.IsFirstChild (genAttributes astContext ats)

    let genPref =
        if astContext.IsFirstChild then
            pref
        else
            (pref +> genOnelinerAttributes astContext ats)

    let afterLetKeyword =
        ifElse isMutable (!- "mutable ") sepNone
        +> ifElse isInline (!- "inline ") sepNone
        +> opt sepSpace ao genAccess

    let genFunctionName =
        getIndentBetweenTicksFromSynPat patRange functionName
        +> opt
            sepNone
            genericTypeParameters
            (fun (ValTyparDecls (tds, _, tcs)) -> genTypeParamPostfix astContext tds tcs)

    let genSignature =
        let rangeBetweenBindingPatternAndExpression =
            let endOfParameters =
                List.last parameters
                |> snd
                |> fun p -> p.Range.End

            ctx.MkRange endOfParameters e.Range.Start

        let spaceBeforeParameters =
            match parameters with
            | [] -> sepNone
            | [ (_, p) ] -> ifElse (addSpaceBeforeParensInFunDef spaceBefore functionName p) sepSpace sepNone
            | _ -> sepSpace

        let short =
            genPref
            +> afterLetKeyword
            +> genFunctionName
            +> spaceBeforeParameters
            +> col sepSpace parameters (genPatWithIdent astContext)
            +> tokN rangeBetweenBindingPatternAndExpression EQUALS sepEq

        let long (ctx: Context) =
            let genParameters, hasSingleTupledArg =
                match parameters with
                | [ _, (PatParen (PatTuple ps) as pp) ] ->
                    genParenTupleWithIndentAndNewlines astContext ps pp.Range, true
                | _ -> col sepNln parameters (genPatWithIdent astContext), false

            (genPref
             +> afterLetKeyword
             +> sepSpace
             +> genFunctionName
             +> indent
             +> sepNln
             +> genParameters
             +> ifElse (hasSingleTupledArg && not alternativeSyntax) sepSpace sepNln
             +> tokN rangeBetweenBindingPatternAndExpression EQUALS sepEqFixed
             +> unindent)
                ctx

        expressionFitsOnRestOfLine short long

    let body (ctx: Context) =
        genExprKeepIndentInBranch astContext e ctx

    let genExpr isMultiline =
        if isMultiline then
            (indent +> sepNln +> body +> unindent)
        else
            sepSpaceIfShortExpressionOrAddIndentAndNewline ctx.Config.MaxFunctionBindingWidth body

    (genPreXmlDoc px
     +> genAttrIsFirstChild
     +> leadingExpressionIsMultiline genSignature genExpr)
        ctx

and genSynBindingFunctionWithReturnType
    (astContext: ASTContext)
    (isMemberDefinition: bool)
    (px: FSharp.Compiler.XmlDoc.PreXmlDoc)
    (ats: SynAttributes)
    (pref: Context -> Context)
    (ao: SynAccess option)
    (isInline: bool)
    (isMutable: bool)
    (functionName: string)
    (patRange: Range)
    (parameters: (string option * SynPat) list)
    (genericTypeParameters: SynValTyparDecls option)
    (returnType: SynType)
    (valInfo: SynValInfo)
    (e: SynExpr)
    (ctx: Context)
    =
    let spaceBefore, alternativeSyntax =
        if isMemberDefinition then
            ctx.Config.SpaceBeforeMember, ctx.Config.AlternativeLongMemberDefinitions
        else
            ctx.Config.SpaceBeforeParameter, ctx.Config.AlignFunctionSignatureToIndentation

    let genAttrIsFirstChild =
        onlyIf astContext.IsFirstChild (genAttributes astContext ats)

    let genPref =
        if astContext.IsFirstChild then
            pref
        else
            pref +> genOnelinerAttributes astContext ats

    let afterLetKeyword =
        ifElse isMutable (!- "mutable ") sepNone
        +> ifElse isInline (!- "inline ") sepNone
        +> opt sepSpace ao genAccess

    let genFunctionName =
        getIndentBetweenTicksFromSynPat patRange functionName
        +> opt
            sepNone
            genericTypeParameters
            (fun (ValTyparDecls (tds, _, tcs)) -> genTypeParamPostfix astContext tds tcs)

    let genReturnType isFixed =
        let genMetadataAttributes =
            match valInfo with
            | SynValInfo (_, SynArgInfo (attributes, _, _)) -> genOnelinerAttributes astContext attributes

        enterNodeFor SynBindingReturnInfo_ returnType.Range
        +> ifElse isFixed (sepColonFixed +> sepSpace) sepColonWithSpacesFixed
        +> genMetadataAttributes
        +> genType astContext false returnType

    let genSignature =
        let equalsRange =
            ctx.MkRange returnType.Range.End e.Range.Start

        let spaceBeforeParameters =
            match parameters with
            | [] -> sepNone
            | [ (_, p) ] -> ifElse (addSpaceBeforeParensInFunDef spaceBefore functionName p) sepSpace sepNone
            | _ -> sepSpace

        let short =
            genPref
            +> afterLetKeyword
            +> sepSpace
            +> genFunctionName
            +> spaceBeforeParameters
            +> col sepSpace parameters (genPatWithIdent astContext)
            +> genReturnType false
            +> tokN equalsRange EQUALS sepEq

        let long (ctx: Context) =
            let genParameters, hasSingleTupledArg =
                match parameters with
                | [ _, (PatParen (PatTuple ps) as pp) ] ->
                    genParenTupleWithIndentAndNewlines astContext ps pp.Range, true
                | _ -> col sepNln parameters (genPatWithIdent astContext), false

            (genPref
             +> afterLetKeyword
             +> sepSpace
             +> genFunctionName
             +> indent
             +> sepNln
             +> genParameters
             +> onlyIf (not hasSingleTupledArg || alternativeSyntax) sepNln
             +> genReturnType (not hasSingleTupledArg || alternativeSyntax)
             +> ifElse alternativeSyntax (sepNln +> tokN equalsRange EQUALS sepEqFixed) sepEq
             +> unindent)
                ctx

        expressionFitsOnRestOfLine short long

    let body = genExprKeepIndentInBranch astContext e

    let genExpr isMultiline =
        if isMultiline then
            (indent +> sepNln +> body +> unindent)
        else
            sepSpaceIfShortExpressionOrAddIndentAndNewline ctx.Config.MaxFunctionBindingWidth body

    (genPreXmlDoc px
     +> genAttrIsFirstChild
     +> leadingExpressionIsMultiline genSignature genExpr)
        ctx

and genLetBindingDestructedTuple
    (astContext: ASTContext)
    (px: FSharp.Compiler.XmlDoc.PreXmlDoc)
    (ats: SynAttributes)
    (pref: string)
    (ao: SynAccess option)
    (isInline: bool)
    (isMutable: bool)
    (pat: SynPat)
    (e: SynExpr)
    =
    let genAttrAndPref =
        if astContext.IsFirstChild then
            (genAttributes astContext ats -- pref)
        else
            (!-pref +> genOnelinerAttributes astContext ats)

    let afterLetKeyword =
        opt sepSpace ao genAccess
        +> ifElse isMutable (!- "mutable ") sepNone
        +> ifElse isInline (!- "inline ") sepNone

    let genDestructedTuples =
        expressionFitsOnRestOfLine (genPat astContext pat) (sepOpenT +> genPat astContext pat +> sepCloseT)

    let equalsRange (ctx: Context) = ctx.MkRange pat.Range.End e.Range.Start

    genPreXmlDoc px
    +> leadingExpressionIsMultiline
        (genAttrAndPref
         +> afterLetKeyword
         +> sepSpace
         +> genDestructedTuples
         +> (fun ctx -> tokN (equalsRange ctx) EQUALS sepEq ctx))
        (fun isMultiline ctx ->
            let short = sepSpace +> genExpr astContext e

            let long =
                indent
                +> sepNln
                +> genExpr astContext e
                +> unindent

            if isMultiline then
                long ctx
            else
                isShortExpression ctx.Config.MaxValueBindingWidth short long ctx)

and genSynBindingValue
    (astContext: ASTContext)
    (px: FSharp.Compiler.XmlDoc.PreXmlDoc)
    (ats: SynAttributes)
    (pref: Context -> Context)
    (ao: SynAccess option)
    (isInline: bool)
    (isMutable: bool)
    (valueName: SynPat)
    (returnType: SynType option)
    (e: SynExpr)
    =
    let genAttrIsFirstChild =
        onlyIf astContext.IsFirstChild (genAttributes astContext ats)

    let genPref =
        if astContext.IsFirstChild then
            pref
        else
            (pref +> genOnelinerAttributes astContext ats)

    let afterLetKeyword =
        opt sepSpace ao genAccess
        +> ifElse isMutable (!- "mutable ") sepNone
        +> ifElse isInline (!- "inline ") sepNone

    let genValueName = genPat astContext valueName

    let genReturnType =
        match returnType with
        | Some rt ->
            let hasGenerics =
                match valueName with
                | SynPat.LongIdent (_, _, Some _, _, _, _) -> true
                | _ -> false

            ifElse hasGenerics sepColonWithSpacesFixed sepColon
            +> genType astContext false rt
        | None -> sepNone

    let equalsRange (ctx: Context) =
        let endPos =
            match returnType with
            | Some rt -> rt.Range.End
            | None -> valueName.Range.End

        ctx.MkRange endPos e.Range.Start

    let genEqualsInBinding (equalsRange: Range) (ctx: Context) =
        let space =
            ctx.TriviaTokenNodes
            |> Map.tryFindOrEmptyList EQUALS
            |> fun triviaNodes ->
                match TriviaHelpers.findInRange triviaNodes equalsRange with
                | Some tn when (List.isNotEmpty tn.ContentAfter) -> sepNone
                | _ -> sepSpace

        (tokN equalsRange EQUALS sepEq +> space) ctx

    genPreXmlDoc px
    +> genAttrIsFirstChild
    +> leadingExpressionIsMultiline
        (genPref
         +> afterLetKeyword
         +> sepSpace
         +> genValueName
         +> genReturnType
         +> (fun ctx -> genEqualsInBinding (equalsRange ctx) ctx))
        (fun isMultiline ctx ->
            let short = genExprKeepIndentInBranch astContext e

            let long =
                indent
                +> sepNln
                +> genExprKeepIndentInBranch astContext e
                +> unindent

            if isMultiline then
                long ctx
            else
                isShortExpression ctx.Config.MaxValueBindingWidth short long ctx)

and genParenTupleWithIndentAndNewlines (astContext: ASTContext) (ps: SynPat list) (pr: range) : Context -> Context =
    sepOpenT
    +> indent
    +> sepNln
    +> col (sepComma +> sepNln) ps (genPat astContext)
    +> unindent
    +> sepNln
    +> sepCloseT
    |> genTriviaFor SynPat_Paren pr

and collectMultilineItemForSynExprKeepIndent
    (astContext: ASTContext)
    (inKeyWordTrivia: TriviaNode list)
    (e: SynExpr)
    : ColMultilineItem list =
    match e with
    | LetOrUses (bs, e) ->
        collectMultilineItemForLetOrUses
            astContext
            inKeyWordTrivia
            bs
            e
            (collectMultilineItemForSynExprKeepIndent astContext inKeyWordTrivia e)
    | Sequentials es ->
        let lastIndex = es.Length - 1

        es
        |> List.mapi
            (fun idx e ->
                if idx = lastIndex then
                    collectMultilineItemForSynExprKeepIndent astContext inKeyWordTrivia e
                else
                    collectMultilineItemForSynExpr astContext inKeyWordTrivia e)
        |> List.collect id
    | KeepIndentMatch (me, clauses, matchRange, matchTriviaType) ->
        ColMultilineItem(
            genKeepIndentMatch astContext me clauses matchRange matchTriviaType,
            sepNlnConsideringTriviaContentBeforeForMainNode matchTriviaType matchRange
        )
        |> List.singleton
    | KeepIndentIfThenElse (branches, elseBranch, ifElseRange) ->
        ColMultilineItem(
            genKeepIdentIf astContext branches elseBranch ifElseRange,
            sepNlnConsideringTriviaContentBeforeForMainNode SynExpr_IfThenElse ifElseRange
        )
        |> List.singleton
    | _ ->
        let t, r = synExprToFsAstType e
        [ ColMultilineItem(genExpr astContext e, sepNlnConsideringTriviaContentBeforeForMainNode t r) ]

and genExprKeepIndentInBranch (astContext: ASTContext) (e: SynExpr) : Context -> Context =
    let keepIndentExpr (ctx: Context) =
        let items =
            collectMultilineItemForSynExprKeepIndent astContext (Map.tryFindOrEmptyList IN ctx.TriviaTokenNodes) e

        colWithNlnWhenItemIsMultilineUsingConfig items ctx

    ifElseCtx (fun ctx -> ctx.Config.KeepIndentInBranch) keepIndentExpr (genExpr astContext e)

and genKeepIndentMatch
    (astContext: ASTContext)
    (e: SynExpr)
    (clauses: SynMatchClause list)
    (matchRange: Range)
    (triviaType: FsAstType)
    : Context -> Context =
    let withRange (ctx: Context) =
        ctx.MkRange e.Range.Start (List.head clauses).Range.Start

    let lastClauseIndex = clauses.Length - 1

    ifElse (triviaType = SynExpr_MatchBang) !- "match! " !- "match "
    +> genExprInIfOrMatch astContext e
    +> (fun ctx -> genWithAfterMatch (withRange ctx) ctx)
    +> sepNln
    +> coli
        sepNln
        clauses
        (fun idx ->
            if idx < lastClauseIndex then
                genClause astContext true
            else
                genLastClauseKeepIdent astContext)
    |> genTriviaFor triviaType matchRange

and genLastClauseKeepIdent (astContext: ASTContext) (Clause (pat, expr, whenExpr)) =
    sepBar
    +> genPat astContext pat
    +> sepSpace
    +> optSingle (genExpr astContext) whenExpr
    +> (fun ctx ->
        let arrowRange =
            ctx.MkRange pat.Range.End expr.Range.Start

        tokN arrowRange FsTokenType.RARROW sepArrowFixed ctx)
    +> sepNln
    +> (let t, r = synExprToFsAstType expr in sepNlnConsideringTriviaContentBeforeForMainNode t r)
    +> genExprKeepIndentInBranch astContext expr

and genKeepIdentIf
    (astContext: ASTContext)
    (branches: (SynExpr * SynExpr * Range * Range * SynExpr) list)
    (elseExpr: SynExpr)
    (ifElseRange: Range)
    =
    coli
        sepNln
        branches
        (fun idx (ifExpr, thenExpr, _r, _fullRange, _node) ->
            let genIf =
                let short =
                    ifElse (idx = 0) (!- "if ") (!- "elif ")
                    +> genExpr astContext ifExpr
                    +> !- " then"

                let long =
                    ifElse (idx = 0) (!- "if ") (!- "elif ")
                    +> genExprInIfOrMatch astContext ifExpr
                    +> sepSpace
                    +> !- "then"

                expressionFitsOnRestOfLine short long

            genIf
            +> indent
            +> sepNln
            +> genExpr astContext thenExpr
            +> unindent)
    +> sepNln
    +> !- "else"
    +> sepNln
    +> (let t, r = synExprToFsAstType elseExpr in sepNlnConsideringTriviaContentBeforeForMainNode t r)
    +> genExprKeepIndentInBranch astContext elseExpr
    |> genTriviaFor SynExpr_IfThenElse ifElseRange

and genConst (c: SynConst) (r: Range) =
    match c with
    | SynConst.Unit ->
        enterNodeTokenByName r LPAREN
        +> !- "("
        +> leaveNodeTokenByName r LPAREN
        +> enterNodeTokenByName r RPAREN
        +> !- ")"
        +> leaveNodeTokenByName r RPAREN
        |> genTriviaFor SynConst_Unit r
    | SynConst.Bool b ->
        !-(if b then "true" else "false")
        |> genTriviaFor SynConst_Bool r
    | SynConst.Byte _
    | SynConst.SByte _
    | SynConst.Int16 _
    | SynConst.Int32 _
    | SynConst.Int64 _
    | SynConst.UInt16 _
    | SynConst.UInt16s _
    | SynConst.UInt32 _
    | SynConst.UInt64 _
    | SynConst.Double _
    | SynConst.Single _
    | SynConst.Decimal _
    | SynConst.IntPtr _
    | SynConst.UInt64 _
    | SynConst.UIntPtr _
    | SynConst.UserNum _ -> genConstNumber c r
    | SynConst.String (s, r) ->
        fun (ctx: Context) ->
            let trivia =
                Map.tryFindOrEmptyList SynConst_String ctx.TriviaMainNodes
                |> List.tryFind (fun tv -> RangeHelpers.rangeEq tv.Range r)

            match trivia with
            | Some ({ ContentItself = Some (StringContent sc) } as tn)
            | Some ({ ContentItself = Some (KeywordString sc) } as tn) ->
                printContentBefore tn
                +> !-sc
                +> printContentAfter tn
            | Some ({ ContentBefore = [ Keyword ({ TokenInfo = { TokenName = "KEYWORD_STRING" }
                                                   Content = kw }) ] }) -> !-kw
            | Some ({ ContentBefore = [ Keyword { TokenInfo = { TokenName = "QMARK" } } ]
                      ContentItself = Some (IdentBetweenTicks ibt) }) -> !-ibt
            | Some { ContentBefore = [ Keyword { TokenInfo = { TokenName = "QMARK" } } ] } -> !-s
            | Some tn ->
                let escaped = Regex.Replace(s, "\"{1}", "\\\"")

                printContentBefore tn
                +> !-(sprintf "\"%s\"" escaped)
                +> printContentAfter tn
            | None ->
                let escaped = Regex.Replace(s, "\"{1}", "\\\"")
                !-(sprintf "\"%s\"" escaped)
            <| ctx
    | SynConst.Char c ->
        fun (ctx: Context) ->
            let tn =
                Map.tryFindOrEmptyList SynConst_Char ctx.TriviaMainNodes
                |> List.tryFind (fun t -> RangeHelpers.rangeEq t.Range r)

            let expr =
                match tn with
                | Some ({ ContentItself = Some (CharContent content) } as tn) ->
                    printContentBefore tn -- content
                    +> printContentAfter tn
                | Some tn ->
                    let escapedChar = Char.escape c

                    printContentBefore tn
                    -- (sprintf "\'%s\'" escapedChar)
                    +> printContentAfter tn
                | None ->
                    let escapedChar = Char.escape c
                    !-(sprintf "\'%s\'" escapedChar)

            expr ctx
    | SynConst.Bytes (bytes, r) ->
        genConstBytes bytes r
        |> genTriviaFor SynConst_Bytes r
    | SynConst.Measure (c, m) ->
        let measure =
            match m with
            | Measure m -> !-m

        let genNumber (ctx: Context) =
            match m with
            | SynMeasure.Seq (_, mr) ->
                let numberRange =
                    ctx.MkRange r.Start (Pos.mkPos mr.StartLine (mr.StartColumn - 1))

                genConstNumber c numberRange ctx
            | _ -> genConstNumber c r ctx

        genNumber
        +> measure
        +> leaveNodeTokenByName r GREATER

and genConstNumber (c: SynConst) (r: Range) =
    fun (ctx: Context) ->
        let findNumberAsContentItself (fallback: Context -> Context) (nodeType: FsAstType) =
            Map.tryFindOrEmptyList nodeType ctx.TriviaMainNodes
            |> List.tryFind (fun t -> RangeHelpers.rangeEq t.Range r)
            |> fun tn ->
                match tn with
                | Some ({ ContentItself = Some (Number n) } as tn) ->
                    printContentBefore tn
                    +> !-n
                    +> printContentAfter tn
                | Some tn ->
                    printContentBefore tn
                    +> fallback
                    +> printContentAfter tn
                | _ -> fallback

        let expr =
            match c with
            | SynConst.Byte v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Byte
            | SynConst.SByte v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_SByte
            | SynConst.Int16 v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Int16
            | SynConst.Int32 v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Int32
            | SynConst.Int64 v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Int64
            | SynConst.UInt16 v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_UInt16
            | SynConst.UInt16s v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_UInt16s
            | SynConst.UInt32 v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_UInt32
            | SynConst.UInt64 v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_UInt64
            | SynConst.Double v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Double
            | SynConst.Single v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Single
            | SynConst.Decimal v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_Decimal
            | SynConst.IntPtr v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_IntPtr
            | SynConst.UIntPtr v -> findNumberAsContentItself (!-(sprintf "%A" v)) SynConst_UIntPtr
            | SynConst.UserNum (v, s) -> findNumberAsContentItself (!-(sprintf "%s%s" v s)) SynConst_UserNum
            | _ -> failwithf "Cannot generating Const number for %A" c

        expr ctx

and genConstBytes (bytes: byte []) (r: Range) =
    fun (ctx: Context) ->
        let trivia =
            Map.tryFindOrEmptyList SynConst_Bytes ctx.TriviaMainNodes
            |> List.tryFind (fun t -> RangeHelpers.rangeEq t.Range r)
            |> Option.bind
                (fun tv ->
                    match tv.ContentItself with
                    | Some (StringContent content) -> Some content
                    | _ -> None)

        match trivia with
        | Some t -> !-t
        | None -> !-(sprintf "%A" bytes)
        <| ctx

and genSynStaticOptimizationConstraint
    (astContext: ASTContext)
    (constraints: SynStaticOptimizationConstraint list)
    : Context -> Context =
    let genConstraint astContext con =
        match con with
        | SynStaticOptimizationConstraint.WhenTyparTyconEqualsTycon (t1, t2, _) ->
            genTypar astContext t1
            +> sepColon
            +> sepSpace
            +> genType astContext false t2
        | SynStaticOptimizationConstraint.WhenTyparIsStruct (t, _) -> genTypar astContext t

    !- " when "
    +> col sepSpace constraints (genConstraint astContext)

and genTriviaFor (mainNodeName: FsAstType) (range: Range) f ctx =
    (enterNodeFor mainNodeName range
     +> f
     +> leaveNodeFor mainNodeName range)
        ctx

and infixOperatorFromTrivia range fallback (ctx: Context) =
    // by specs, section 3.4 https://fsharp.org/specs/language-spec/4.1/FSharpSpec-4.1-latest.pdf#page=24&zoom=auto,-137,312
    let validIdentRegex =
        """^(_|\p{L}|\p{Nl})([_'0-9]|\p{L}|\p{Nl}\p{Pc}|\p{Mn}|\p{Mc}|\p{Cf})*$"""

    let isValidIdent x = Regex.Match(x, validIdentRegex).Success

    TriviaHelpers.getNodesForTypes
        [ SynPat_LongIdent
          SynPat_Named
          SynExpr_Ident ]
        ctx.TriviaMainNodes
    |> List.choose
        (fun t ->
            match t.Range = range with
            | true ->
                match t.ContentItself with
                | Some (IdentOperatorAsWord iiw) -> Some iiw
                | Some (IdentBetweenTicks iiw) when not (isValidIdent fallback) -> Some iiw // Used when value between ``...``
                | _ -> None
            | _ -> None)
    |> List.tryHead
    |> fun iiw ->
        match iiw with
        | Some iiw -> !- iiw ctx
        | None -> !- fallback ctx

and addSpaceBeforeClassConstructor expr =
    match expr with
    | Paren _
    | ConstExpr (SynConst.Unit, _) -> sepSpaceBeforeClassConstructor
    | _ -> sepSpace
