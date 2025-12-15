# GameTime

This is a ASP.NET web service written in F# that fetches play times for games
on [BoardGameGeek](https://boardgamegeek.com) and calculates the percentiles for those times at different play counts.

ASP.NET is used in minimal API mode using [Giraffe.ViewEngine](https://giraffe.wiki/view-engine) to keep the artifact
size down. [Dapper.FSharp](https://github.com/Dzoukr/Dapper.FSharp) is used with SQLite for persistence.

Application-specific settings are set with settings.json or with the `GAMETIME_` environment variable prefix:

- `sqliteConnectionString`: a connection string for SQLite (default: `Data Source=GameTime.db;Foreign Keys=True`)
- `PathBase`: the base path for URLs (default is the empty string)
- `CacheSizeBytes`: the number of bytes reserved for the in-memory cache (default: 100 MB)
- `BggFrontendToken`: the token for the frontend to call BGG
- `BggBackendToken`: the token for the backend to call BGG
