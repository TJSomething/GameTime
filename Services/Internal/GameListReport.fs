namespace GameTime.Services.Internal

open GameTime.Data.Entities

[<CLIMutable>]
type GameListReport = { Games: Game seq }
    with
    static member IsSerializable =
        PreserveRecordFields<Game>