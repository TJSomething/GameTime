namespace GameTime.Services.Internal

open System
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Microsoft.Extensions.Logging

type XmlFetcher(logger: ILogger) =
    let mutable requestDelay = 1000
    let mutable lastRequestCompletion = DateTime.UnixEpoch
    let requestSemaphore = new SemaphoreSlim(1, 1)

    let rec handle (url: string) =
        let mutable gotSemaphore = false

        let release () =
            if gotSemaphore then
                requestSemaphore.Release() |> ignore
                gotSemaphore <- false

        task {
            try
                do! requestSemaphore.WaitAsync()
                gotSemaphore <- true

                use client = new HttpClient()

                let safeRequestWait =
                    (lastRequestCompletion + TimeSpan.FromMilliseconds(requestDelay)) - DateTime.Now

                if (safeRequestWait.TotalMilliseconds > 0.0) then
                    do! Task.Delay(safeRequestWait) // Throttle the request with a delay

                let! response = client.GetAsync(url)

                match response.StatusCode with
                | System.Net.HttpStatusCode.OK ->
                    let! rawXmlContent = response.Content.ReadAsStringAsync()
                    lastRequestCompletion <- DateTime.Now

                    // BGG doesn't escape ampersands correctly
                    let xmlContent =
                        Regex.Replace(rawXmlContent, "&(?!lt;|gt;|amp;|quot;|apos;|#[0-9]+;|#x[0-9a-fA-F]+;)", "&amp;")

                    return XDocument.Parse(xmlContent)
                | System.Net.HttpStatusCode.TooManyRequests ->
                    // Handle 429 errors by increasing the delay and retrying
                    requestDelay <- requestDelay * 2

                    logger.LogWarning(
                        $"Received 429 Too Many Requests. Retrying with backoff (delay: {requestDelay} ms)..."
                    )

                    // Semaphores are not reentrant, and this could run across threads
                    release ()

                    return! handle url
                | _ ->
                    // Handle other status codes (e.g., 500, 404) don't require increased delay
                    logger.LogError($"Error: Status code %d{int response.StatusCode}")

                    release ()

                    return! handle url
            finally
                release ()
        }

    member this.downloadXmlAsync(url: string) = handle url
