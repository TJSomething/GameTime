module GameTime.Data.Migrations.Migrate1

open System.Data

open Dapper

let Run (conn: IDbConnection) =
    task {
        let! _ =
            """
            create table if not exists Play
            (
                Id int identity
                    constraint Play_pk
                        primary key,
                GameId int not null,
                Length int not null,
                PlayerCount int not null,
                FetchedAt text not null,
                foreign key(GameId) References Game(Id)
            )
            """
            |> conn.ExecuteAsync

        return ()
    }