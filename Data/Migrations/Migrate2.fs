module GameTime.Data.Migrations.Migrate2

open System.Data

open Dapper

let Run (conn: IDbConnection) =
    task {
        let! results =
            """select name from pragma_table_xinfo('Play') where name = 'FetchedAt'"""
            |> conn.QueryAsync

        let count = results.AsList().Count

        if count > 0 then
            let! _ = """alter table Play drop column FetchedAt""" |> conn.ExecuteAsync
            ()

        return ()
    }