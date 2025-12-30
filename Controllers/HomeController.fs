namespace GameTime.Controllers

open Microsoft.AspNetCore.Http

open Giraffe.ViewEngine

open GameTime.Data
open GameTime.Data.Entities
open GameTime.Services
open GameTime.ViewFns

// This can't be opened before Data or the app crashes
open Dapper
open Dapper.FSharp.SQLite

type HomeController(dbContext: DbContext, config: AppConfig) =
    member this.Index(pathBase: string) =
        task {
            use conn = dbContext.GetConnection()

            let! gameCountResult =
                select {
                    for g in dbContext.Game do
                        count "*" "Value"
                }
                |> conn.SelectAsync<{| Value: int64 |}>
                
            let gameCount =
                gameCountResult
                |> Seq.tryHead
                |> Option.map _.Value
                |> Option.defaultValue -1
 
            let! recentGames =
                select {
                    for g in dbContext.Game do
                        where (isNotNullValue g.UpdateFinishedAt)
                        andWhere (isNotNullValue g.Title)
                        orderByDescending g.UpdateFinishedAt
                        take 0 10
                }
                |> conn.SelectAsync<Game>
            
            // language=sql
            let! randomGames =
                """
with row_count as (
    -- max(rowid) doesn't require a table scan like count(*)
    select max(rowid) as max_rowid from Play
),
random_play_loop(gameids, nextrowid, count) as (
    select
        -- We store the collected ids in gameids.
        -- This is required to prevent duplicate gameids.
        json('[]'),
        -- random() returns a random int64. At that size, radix bias should be minimal.
        -- rowid is 1-indexed.
        abs(random() % (select max_rowid from row_count)) + 1 as nextrowid,
        0
    union all
    select
        case
            when (
                select count(*)
                from Play
                -- Make sure that the rowid selected in the last iteration
                -- actually refers to an undeleted row.
                --
                -- A random rowid with retries is the best because:
                -- * using offset requires a table scan
                -- * the Id column is sparse because we haven't loaded all
                --   plays from BGG, which would require a lot of retries
                -- * rowid is periodically compacted during vacuum, decreasing
                --   the required retries
                -- * exact rowid search is always log(N)
                where Play.rowid = nextrowid
                    -- ensure that the given row doesn't have a game that we've
                    -- already seen
                    and Play.GameId not in (
                        select value from json_each(gameids)
                    )
            ) = 0 then
                gameids
            else
                json_insert(
                    gameids,
                    '$[#]',
                    -- We don't need to check for the already seen criterion
                    -- twice.
                    (select GameId from Play where Play.rowid = nextrowid)
                )
            end,
        abs(random() % (select max_rowid from row_count)) + 1,
        count + 1
    from random_play_loop
    where
        -- Terminate when we have enough games.
        json_array_length(gameids) < 10
        -- It's possible that there aren't enough games and we don't want an
        -- infinite loop if that happens.
        and count < 1000
),
-- Pull the IDs out of the JSON array in a CTE because that looks prettier.
random_game_id as (
    select value as GameId
    from json_each((
        select gameids
        from random_play_loop
        order by count desc
        limit 1
    ))
)
select Game.*
from random_game_id
join Game
    on Game.Id = random_game_id.GameId;
                """ |> conn.QueryAsync<Game>
            PreserveRecordFields<Game>
            
            return
                Results.Content(
                    statusCode = 200,
                    contentType = "text/html",
                    content = (
                        Home.Render(
                            pathBase = pathBase,
                            gameCount = gameCount,
                            recentGames = recentGames,
                            randomGames = randomGames,
                            bggToken = config.BggFrontendToken
                        )
                        |> RenderView.AsString.htmlDocument)
                )
        }
