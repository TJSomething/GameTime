namespace GameTime.Services.Identity

open System.Threading.Tasks
open Microsoft.AspNetCore.Identity

type AppRoleStore() =
    interface IRoleStore<string> with
        member this.CreateAsync(role, cancellationToken) =
            failwith "not supported"
        member this.DeleteAsync(role, cancellationToken) =
            failwith "not supported"
        member this.Dispose() = ()
        member this.FindByIdAsync(roleId, cancellationToken) =
            if roleId = "admin" then
                Task.FromResult("admin")
            else
                failwith "not found"
        member this.FindByNameAsync(normalizedRoleName, cancellationToken) =
            if normalizedRoleName = "admin" then
                Task.FromResult("admin")
            else
                failwith "not found"
        member this.GetNormalizedRoleNameAsync(role, cancellationToken) =
            if role = "admin" then
                Task.FromResult("admin")
            else
                failwith "not found"
        member this.GetRoleIdAsync(role, cancellationToken) =
            Task.FromResult(role)
        member this.GetRoleNameAsync(role, cancellationToken) =
            Task.FromResult(role)
        member this.SetNormalizedRoleNameAsync(role, normalizedName, cancellationToken) =
            failwith "not supported"
        member this.SetRoleNameAsync(role, roleName, cancellationToken) =
            failwith "not supported"
        member this.UpdateAsync(role, cancellationToken) =
            failwith "not supported"
    
