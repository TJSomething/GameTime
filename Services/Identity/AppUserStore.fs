namespace GameTime.Services.Identity

open System
open System.Threading.Tasks
open Dapper.FSharp.SQLite
open Microsoft.AspNetCore.Identity

open GameTime.Data

type AppUserStore(dbContext: DbContext) =
    let UserTable = table<AppUser>
    let connection = dbContext.GetConnection()
    
    let createUserWhenReady (user: AppUser) =
        task {
            if user.Email <> null && user.PasswordHash <> null then
                if user.Id = null then
                    user.Id <- Guid.NewGuid().ToString()
                if user.Role = null then
                    user.Role <- "admin"
                let now = DateTime.UtcNow.ToString("o")
                if user.CreatedAt = null then
                    user.CreatedAt <- now
                if user.UpdatedAt = null then
                    user.UpdatedAt <- now
                    
                let! existingUserResult =
                    select {
                        for u in UserTable do
                            where (like u.Email user.Email)
                    }
                    |> connection.SelectAsync<AppUser>
                
                // Clear out unused users
                let expiry = DateTime.UtcNow.AddDays(-7).ToString("o")
                let! _ =
                    delete {
                        for u in UserTable do
                            where (u.CreatedAt < expiry)
                    }
                    |> connection.DeleteAsync
                
                if existingUserResult |> Seq.isEmpty |> not then
                    return false
                else
                    let! _ =
                        insert {
                            into UserTable
                            value user
                        }
                        |> connection.InsertAsync
                    
                    return true
            else
                return false 
        }
            
    interface IUserStore<AppUser> with
        member this.CreateAsync(user, cancellationToken) =
            task {
                let! isCreated = createUserWhenReady user
                    
                if isCreated then
                    return IdentityResult.Success
                else
                    return IdentityResult.Failed([| IdentityErrorDescriber().DefaultError() |])
            }
            
        member this.DeleteAsync(user, cancellationToken) =
            failwith "not supported"
        
        member this.Dispose() =
            connection.Dispose()
        
        member this.FindByIdAsync(userId, cancellationToken) =
            task {
                let! userResult =
                    select {
                        for u in UserTable do
                            where (u.Id = userId)
                    }
                    |> connection.SelectAsync<AppUser>
                
                return userResult |> Seq.tryHead |> Option.toObj
            }
            
        member this.FindByNameAsync(normalizedUserName, cancellationToken) =
            task {
                let! userResult =
                    select {
                        for u in UserTable do
                            where (like u.Email normalizedUserName)
                    }
                    |> connection.SelectAsync<AppUser>
                
                return userResult |> Seq.tryHead |> Option.toObj
            }
            
        member this.GetNormalizedUserNameAsync(user, cancellationToken) =
            task {
                return user.Email.Normalize()
            }
            
        member this.GetUserIdAsync(user, cancellationToken) =
            task {
                return user.Id
            }
            
        member this.GetUserNameAsync(user, cancellationToken) =
            task {
                return user.Email
            }
            
        member this.SetNormalizedUserNameAsync(user, normalizedName, cancellationToken) =
            Task.FromResult()
        
        member this.SetUserNameAsync(user, userName, cancellationToken) =
            task {
                user.Email <- userName
                ()
            }
        
        member this.UpdateAsync(user, cancellationToken) =
            task {
                let now = DateTime.UtcNow.ToString("o")
                
                user.UpdatedAt <- now
                
                let! _ =
                    update {
                        for u in UserTable do
                            setColumn u.Email user.Email
                            setColumn u.PasswordHash user.PasswordHash
                            setColumn u.Role user.Role
                            setColumn u.EmailConfirmed user.EmailConfirmed
                            setColumn u.UpdatedAt now
                            where (u.Id = user.Id)
                    }
                    |> connection.UpdateAsync
                    
                return IdentityResult.Success
            }


    interface IUserPasswordStore<AppUser> with
        member this.SetPasswordHashAsync(user, passwordHash, cancellationToken) =
            task {
                user.PasswordHash <- passwordHash
                ()
            }
            
        member this.GetPasswordHashAsync(user, cancellationToken) =
            task {
                return user.PasswordHash
            }
            
        member this.HasPasswordAsync(user, cancellationToken) =
            task {
                return true
            }
    
    interface IUserEmailStore<AppUser> with
        member this.FindByEmailAsync(normalizedEmail, cancellationToken) =
            task {
                let! userResult =
                    select {
                        for u in UserTable do
                            where (like u.Email normalizedEmail)
                    }
                    |> connection.SelectAsync<AppUser>
                
                return userResult |> Seq.tryHead |> Option.toObj
            }
            
        member this.GetEmailAsync(user, cancellationToken) =
            Task.FromResult(user.Email)
            
        member this.GetEmailConfirmedAsync(user, cancellationToken) =
            Task.FromResult(user.EmailConfirmed)
            
        member this.GetNormalizedEmailAsync(user, cancellationToken) =
            Task.FromResult(user.Email)
            
        member this.SetEmailAsync(user, email, cancellationToken) =
            task {
                user.Email <- email
            }
        
        member this.SetEmailConfirmedAsync(user, confirmed, cancellationToken) =
            task {
                user.EmailConfirmed <- confirmed
            }
            
        member this.SetNormalizedEmailAsync(user, normalizedEmail, cancellationToken) =
            Task.FromResult()
    