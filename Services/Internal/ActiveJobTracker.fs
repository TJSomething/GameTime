namespace GameTime.Services.Internal

open System.Collections.Generic

type ActiveJobTracker() =
    let mutable nextOrderNumber = 0
    let jobIdToOrderNumber = Dictionary<int, int>()
    let activeOrderNumbers = SortedSet<int>()

    member this.StartJob(id: int) =
        lock this (fun () ->
            if jobIdToOrderNumber.TryAdd(id, nextOrderNumber) then
                activeOrderNumbers.Add(nextOrderNumber) |> ignore
                nextOrderNumber <- nextOrderNumber + 1
                true
            else
                false)

    member this.CloseJob(id: int) =
        lock this (fun () ->
            match jobIdToOrderNumber.TryGetValue(id) with
            | (true, order) ->
                activeOrderNumbers.Remove(order) |> ignore
                jobIdToOrderNumber.Remove(id)
            | (false, _) -> false)

    /// <summary>
    /// Gets the number of jobs ahead of the given ID
    /// </summary>
    /// <param name="id">the job ID to check</param>
    /// <returns>the position of the given ID, if that job is active</returns>
    member this.GetJobOrder(id: int) =
        lock this (fun () ->
            match jobIdToOrderNumber.TryGetValue(id) with
            | (true, order) -> Some(order - activeOrderNumbers.Min)
            | (false, _) -> None)
