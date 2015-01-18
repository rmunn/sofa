namespace sofa

open System

open FSharp.Data
open Newtonsoft.Json

type IdAndRev = {
    _id: string
    _rev: string
}

type Response = {
    id: string
    rev: string
    ok: bool
}

[<AutoOpen>]
module Convenience = 
    let mapHeaders headers = 
        headers |> Map.map (fun k (v:string) -> 
                        v.Split([|","|], StringSplitOptions.RemoveEmptyEntries) 
                        |> Seq.map (fun s -> s.Trim ()) 
                        |> Seq.filter (not << String.IsNullOrWhiteSpace)
                        |> Seq.toList
                    )

    let defaultDeserialzer str: (string * string * 'a) =
        let model = JsonConvert.DeserializeObject<'a>(str)
        let idandrev = JsonConvert.DeserializeObject<IdAndRev>(str)
        (idandrev._id, idandrev._rev, model)

    let resultDeserializer str = 
        JsonConvert.DeserializeObject<Response>(str)

    let defaultSerializer obj = 
        JsonConvert.SerializeObject obj

type Database = 
    {
        Url: string
    }
    member x.NormalizeUrl () =
        match x.Url.[x.Url.Length - 1] with 
        | '/' -> x.Url
        | _ -> x.Url + "/"

type SeatedSofa<'obj> = 
    {
        get: (string -> (string * string * 'obj) option Async)
        head: (string -> (string * Map<string, string list>) option Async)
        put: (string * string option -> 'obj -> (string * string) option Async)
        delete: (string * string -> (string * string) option Async)
    } 

module Sofa =
    /// get a resource from the db
    let get<'a> (db:Database) (ser: string -> string * string * 'a)  http id : Async<(string * string * 'a) option> = 
        async {
            let! res = http (sprintf "%s%O" (db.NormalizeUrl ()) id) 
            match res with 
            | Some x -> 
                let body, headers = x
                return Some (body |> ser)
            | None -> return None
        }
    
    let head (db:Database) (http: string -> Map<string, string list> option Async) id = 
        async {
            let! headers = http (sprintf "%s%O" (db.NormalizeUrl ()) id)
            return 
                match headers with 
                | Some h -> Some (h.["E-Tag"] |> List.head, h)
                | None -> None
        }

    let put<'a> (db:Database) (ser: 'a -> string) http (id, rev:string option) (model:'a) =
        async {
            let! resp = http (sprintf "%s%O" (db.NormalizeUrl()) id) (ser model)
            return 
                match resp with 
                | Some x -> 
                    let putResult, headers = x 
                    let putResult = putResult |> resultDeserializer

                    Some (putResult.id, putResult.rev)
                | None -> None
        }

    let delete (db:Database) http (id, rev) =
        async {
            let! resp = http (sprintf "%s%O" (db.NormalizeUrl ()) id) rev
            return 
                match resp with 
                | Some x -> 
                    let deleteResult, headers = x
                    let deleteResult = deleteResult |> resultDeserializer

                    Some (deleteResult.id, deleteResult.rev)
                | None -> None
        }
                 
    let build<'a> db = 
        let headReq url =
            async { 
                let! res = Http.AsyncRequest (url, httpMethod = "HEAD")
                return 
                    match res.StatusCode with 
                    | 200 | 304 -> Some (res.Headers  |> mapHeaders)
                    | 401 -> failwith "Read privilege required"
                    | 404 -> None
                    | _ -> failwith "Something else happend that shouldn't have"
            }

        let getReq url = 
            async {
                let! res = Http.AsyncRequest url

                return 
                    match res.StatusCode with 
                    | 200 | 304 -> 
                        let body = 
                            match res.Body with
                            | HttpResponseBody.Text x -> x
                            | _ -> failwith "expecting a textbased response"

                        Some (body, res.Headers |> mapHeaders)
                    | 404 -> None
                    | 401 -> failwith "Read privilege required"
                    | 400 -> failwith "Bad request"
                    | _ -> failwith "Something else happend that shouldn't have"
            }

        let deleteReq url rev =
            async {
                let! res = Http.AsyncRequest (url, query = [("rev", rev)], httpMethod = "DELETE")
                return 
                    match res.StatusCode with
                    | 200 | 202 ->
                        let body = 
                            match res.Body with
                            | HttpResponseBody.Text x -> x
                            | _ -> failwith "expecting a textbased response"

                        Some (body, res.Headers |> mapHeaders)
                    | 404 -> None
                    | 401 -> failwith "Write privilege required"
                    | 400 -> failwith "Bad request"
                    | 409 -> failwith "Specified revision is not the latest for target document"
                    | _ -> failwith "Something else happend that shouldn't have"
            }

        let putReq url model = 
            async {
                let! res = Http.AsyncRequest (url, headers = ["Content-Type", "application/json"], body = TextRequest(model))
                return 
                    match res.StatusCode with
                    | 201 | 202 -> 
                        let body = 
                            match res.Body with 
                            | HttpResponseBody.Text x -> x
                            | _ -> failwith "expecting a textbased response"
                        Some (body, res.Headers |> mapHeaders)
                    | 404 -> failwith "Database doesn't exist"
                    | 401 -> failwith "Write privilege required"
                    | 400 -> failwith "Bad request"
                    | 409 -> failwith "Document with the specified ID already exists or specified revision is not latest for target document"
                    | _ -> failwith "Something else happend that shouldn't have"
            }
        
        {
            get = get db defaultDeserialzer getReq
            head = head db headReq
            put = put db defaultSerializer putReq 
            delete = delete db deleteReq
        }

    