namespace GameTime.Services.Internal

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open GameTime.Services
open Microsoft.Extensions.Logging

type XmlFetcher(logger: ILogger, config: AppConfig) =
    let mutable requestDelay = 1000
    let mutable lastRequestCompletion = DateTime.UnixEpoch
    let requestSemaphore = new SemaphoreSlim(1, 1)
    let client =
        let c = new HttpClient()
        c.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", config.BggBackendToken)
        c

    let handle (url: string) =
        let mutable gotSemaphore = false

        let release () =
            if gotSemaphore then
                requestSemaphore.Release() |> ignore
                gotSemaphore <- false

        let tryFetchTask () =
            task {
                try
                    do! requestSemaphore.WaitAsync()
                    gotSemaphore <- true

                    let safeRequestWait =
                        (lastRequestCompletion + TimeSpan.FromMilliseconds(requestDelay)) - DateTime.Now

                    if (safeRequestWait.TotalMilliseconds > 0.0) then
                        do! Task.Delay(safeRequestWait) // Throttle the request with a delay

                    let! response = client.GetAsync(url)

                    match response.StatusCode with
                    | System.Net.HttpStatusCode.OK ->
                        let! rawXmlContent = response.Content.ReadAsStringAsync()

                        // BGG doesn't escape ampersands correctly
                        let xmlContent =
                            Regex.Replace(
                                rawXmlContent,
                                "&(?!lt;|gt;|amp;|quot;|apos;|#[0-9]+;|#x[0-9a-fA-F]+;)",
                                "&amp;"
                            )

                        return Some(XDocument.Parse(xmlContent))
                    | System.Net.HttpStatusCode.TooManyRequests ->
                        // Handle 429 errors by increasing the delay and retrying
                        requestDelay <- requestDelay * 2

                        logger.LogWarning(
                            $"Received 429 Too Many Requests. Retrying with backoff (delay: {requestDelay} ms)..."
                        )

                        return None
                    | _ ->
                        // Handle other status codes (e.g., 500, 404) don't require increased delay
                        logger.LogError($"Error: Status code %d{int response.StatusCode}")

                        return None
                finally
                    release ()
            }

        // We are running a Task inside an Async inside a Task because
        // we want infinite retry without blowing up the stack, which we
        // can only get with Async but all the HTTP client stuff wants to
        // use a Task.
        let rec go () =
            async {
                try
                    let! result = Async.AwaitTask(tryFetchTask ())
                    lastRequestCompletion <- DateTime.Now
                    release ()

                    match result with
                    | Some success -> return success
                    | None -> return! go ()
                with ex ->
                    logger.LogError(ex, "Unexpected error fetching")
                    lastRequestCompletion <- DateTime.Now
                    release ()
                    return! go ()

            }

        Async.StartAsTask(go ())

    member this.downloadXmlAsync(url: string) = handle url
