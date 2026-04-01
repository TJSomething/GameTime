namespace GameTime.Services.Identity

[<CLIMutable>]
type AppUser =
    { mutable Id: string
      mutable Email: string
      mutable PasswordHash: string
      mutable EmailConfirmed: bool
      mutable Role: string
      mutable CreatedAt: string
      mutable UpdatedAt: string }