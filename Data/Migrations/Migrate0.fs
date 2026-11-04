module GameTime.Data.Migrations.Migrate0

open System.Data
open Dapper

let Run (conn: IDbConnection) =
    task {
        let! _ =
            """
            create table if not exists Game
            (
                Id int identity
                    constraint Game_pk
                        primary key,
                Title text null,
                AddedAt text not null,
                UpdateStartedAt text null,
                UpdateTouchedAt text null,
                UpdateFinishedAt text null,
                FetchedPlays int not null,
                TotalPlays int not null
            )
            """
            |> conn.ExecuteAsync

        return ()
    }
