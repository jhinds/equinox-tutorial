module Lease.Api

open Dto
open FSharp.UMX
open System
open Suave
open Suave.Operators
open Suave.RequestErrors
open Suave.Filters

type Handle = HttpContext -> AsyncResult<string,string>
type HandlePath<'PathParams> = 'PathParams -> Handle

let ok s =
    fun (ctx:HttpContext) -> 
        { ctx with 
            response = 
                { ctx.response with 
                    status = HTTP_200.status
                    content = UTF8.bytes s |> Bytes }}

let JSON = Writers.setMimeType "application/json; charset=utf-8"

let createHandler 
    (handle:Handle) =
    fun (ctx:HttpContext) ->
        let onSuccess data = ok data ctx |> Some
        let onFailure err = failwith err
        handle ctx |> AsyncResult.bimap onSuccess onFailure

let createPathHandler 
    (handlePath:HandlePath<'PathParams>) = 
    fun (pathParams:'PathParams) ->
        handlePath pathParams
        |> createHandler

let handleCreateLease
    (service:Service)
    : Handle =
    fun (ctx:HttpContext) ->
        let { request = { rawForm = body }} = ctx
        asyncResult {
            let! newLease =
                body
                |> LeaseSchema.deserializeFromBytes
                |> Result.map LeaseSchema.toDomain
                |> AsyncResult.ofResult
            return! service.create newLease
        }

let handleGetLease 
    (service:Service)
    : HandlePath<string> =
    fun leaseIdParam (ctx:HttpContext) ->
        let { request = req } = ctx
        asyncResult {
            let! leaseId = 
                leaseIdParam 
                |> Guid.tryParse
                |> Option.map UMX.tag<leaseId>
                |> Result.ofOption (sprintf "could not parse get leaseIdParam %s" leaseIdParam)
                |> AsyncResult.ofResult
            let asOf = 
                match req.queryParam "asOf" with
                | Choice1Of2 asOfStr ->
                    asOfStr
                    |> DateTime.tryParse
                    |> Option.map AsOf
                | _ -> None
            let asAt = 
                match req.queryParam "asAt" with
                | Choice1Of2 asOfStr ->
                    asOfStr
                    |> DateTime.tryParse
                    |> Option.map AsAt
                | _ -> None
            let! result =
                match (asOf, asAt) with
                | (None, None) -> service.get leaseId Latest
                | (Some asOfDate, None) -> service.get leaseId asOfDate
                | (None, Some asAtDate) -> service.get leaseId asAtDate
                | (Some _, Some _) -> "only specify asOf or asAt, not both" |> AsyncResult.ofError
            return result
        }

let handleModifyLease 
    (service:Service)
    : HandlePath<string> =
    fun leaseIdParam (ctx:HttpContext) ->
        let { request = { rawForm = body } as req } = ctx
        asyncResult {
            let! leaseId = 
                leaseIdParam 
                |> Guid.tryParse
                |> Option.map UMX.tag<leaseId>
                |> Result.ofOption "could not parse modify leaseIdParam"
                |> AsyncResult.ofResult
            let! newLease =
                body
                |> ModifiedLeaseSchema.deserializeFromBytes
                |> Result.map (ModifiedLeaseSchema.toDomain leaseId)
                |> AsyncResult.ofResult
            let lease =
                { LeaseId = leaseId
                  StartDate = newLease.StartDate
                  MaturityDate = newLease.MaturityDate
                  MonthlyPaymentAmount = newLease.MonthlyPaymentAmount }
            let effDate = 
                match req.queryParam "effDate" with
                | Choice1Of2 effDateStr ->
                    effDateStr
                    |> DateTime.tryParse
                    |> Option.map UMX.tag<effectiveDate>
                | _ -> None
                |> Option.defaultValue %newLease.StartDate 
            return! service.modify lease effDate
        }

let handleTerminateLease
    (service:Service)
    : HandlePath<string> =
    fun leaseIdParam (ctx:HttpContext) ->
        let { request = req } = ctx
        asyncResult {
            let! leaseId = 
                leaseIdParam 
                |> Guid.tryParse
                |> Option.map UMX.tag<leaseId>
                |> Result.ofOption "could not parse delete leaseIdParam"
                |> AsyncResult.ofResult
            let effDate = 
                match req.queryParam "effDate" with
                | Choice1Of2 effDateStr ->
                    effDateStr
                    |> DateTime.tryParse
                    |> Option.map UMX.tag<effectiveDate>
                | _ -> None
                |> Option.defaultValue %DateTime.UtcNow
            return! service.terminate leaseId effDate
        }

let handleSchedulePayment
    (service:Service)
    : HandlePath<string> =
    fun leaseIdParam (ctx:HttpContext) ->
        let { request = { rawForm = body } } = ctx
        asyncResult {
            let! leaseId = 
                leaseIdParam 
                |> Guid.tryParse
                |> Option.map UMX.tag<leaseId>
                |> Result.ofOption (sprintf "could not parse schedule leaseIdParam %s" leaseIdParam)
                |> AsyncResult.ofResult
            let! payment =
                body
                |> PaymentSchema.deserializeFromBytes
                |> Result.map PaymentSchema.toDomain
                |> AsyncResult.ofResult
            return! service.schedulePayment leaseId payment
        }

let handleReceivePayment
    (service:Service)
    : HandlePath<string> =
    fun leaseIdParam (ctx:HttpContext) ->
        let { request = { rawForm = body } } = ctx
        asyncResult {
            let! leaseId = 
                leaseIdParam 
                |> Guid.tryParse
                |> Option.map UMX.tag<leaseId>
                |> Result.ofOption (sprintf "could not parse receive payment leaseIdParam %s" leaseIdParam)
                |> AsyncResult.ofResult
            let! payment =
                body
                |> PaymentSchema.deserializeFromBytes
                |> Result.map PaymentSchema.toDomain
                |> AsyncResult.ofResult
            return! service.receivePayment leaseId payment
        }

let handleUndo
    (service:Service)
    : HandlePath<string * string> =
    fun (leaseIdParam, eventIdParam) (ctx:HttpContext) ->
        asyncResult {
            let! leaseId = 
                leaseIdParam 
                |> Guid.tryParse
                |> Option.map UMX.tag<leaseId>
                |> Result.ofOption "could not parse undo leaseIdParam"
                |> AsyncResult.ofResult
            let! eventId = 
                eventIdParam 
                |> Int.tryParse
                |> Option.map UMX.tag<eventId>
                |> Result.ofOption "could not parse eventId"
                |> AsyncResult.ofResult
            return! service.undo leaseId eventId
        }

let init (service:Service) =
    let handleGetLease' = handleGetLease service
    let handleCreateLease' = handleCreateLease service
    let handleSchedulePayment' = handleSchedulePayment service
    let handleReceivePayment' = handleReceivePayment service
    let handleModifyLease' = handleModifyLease service
    let handleTerminateLease' = handleTerminateLease service
    let handleUndo' = handleUndo service
    choose
        [ path "/lease" >=> choose 
            [ POST >=> (createHandler handleCreateLease') >=> JSON ]
          pathRegex "/lease/[^/]+?$" >=> choose
            [ GET >=> pathScan "/lease/%s" (createPathHandler handleGetLease') >=> JSON
              PUT >=> pathScan "/lease/%s" (createPathHandler handleModifyLease') >=> JSON
              DELETE >=> pathScan "/lease/%s" (createPathHandler handleTerminateLease') >=> JSON ]
          pathRegex "/lease/[^/]+?/[^/]+?$" >=> choose
            [ POST >=> choose
                  [ pathScan "/lease/%s/schedule" (createPathHandler handleSchedulePayment') >=> JSON
                    pathScan "/lease/%s/payment" (createPathHandler handleReceivePayment') >=> JSON ]
              DELETE >=> pathScan "/lease/%s/%s" (createPathHandler handleUndo') >=> JSON ]
          NOT_FOUND "handler not implemented" ]
